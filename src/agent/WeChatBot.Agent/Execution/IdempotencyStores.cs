using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeChatBot.Agent.Contracts;

namespace WeChatBot.Agent.Execution;

public enum IdempotencyDisposition
{
    Started,
    Completed,
    InProgress,
    Conflict
}

public sealed record IdempotencyBeginResult(
    IdempotencyDisposition Disposition,
    CommandExecutionResult? CachedResult,
    string? ExistingCommandId);

public interface IIdempotencyStore
{
    ValueTask<IdempotencyBeginResult> TryBeginAsync(
        string idempotencyKey,
        string commandId,
        string commandFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        string idempotencyKey,
        CommandExecutionResult result,
        CancellationToken cancellationToken);
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ValueTask<IdempotencyBeginResult> TryBeginAsync(
        string idempotencyKey,
        string commandId,
        string commandFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keyHash = IdempotencyKeyHasher.Hash(idempotencyKey);
        lock (_sync)
        {
            if (_entries.TryGetValue(keyHash, out var entry))
            {
                if (!string.Equals(entry.CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(new IdempotencyBeginResult(
                        IdempotencyDisposition.Conflict,
                        null,
                        entry.CommandId));
                }

                return ValueTask.FromResult(entry.Result is null
                    ? new IdempotencyBeginResult(IdempotencyDisposition.InProgress, null, entry.CommandId)
                    : new IdempotencyBeginResult(IdempotencyDisposition.Completed, entry.Result, entry.CommandId));
            }

            _entries.Add(keyHash, new Entry(commandId, commandFingerprint, now, null));
            return ValueTask.FromResult(new IdempotencyBeginResult(IdempotencyDisposition.Started, null, null));
        }
    }

    public ValueTask CompleteAsync(
        string idempotencyKey,
        CommandExecutionResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keyHash = IdempotencyKeyHasher.Hash(idempotencyKey);
        lock (_sync)
        {
            if (!_entries.TryGetValue(keyHash, out var entry))
            {
                throw new InvalidOperationException("Cannot complete an idempotency key that was not started.");
            }

            _entries[keyHash] = entry with { Result = result, UpdatedAt = result.CompletedAt };
        }

        return ValueTask.CompletedTask;
    }

    private sealed record Entry(
        string CommandId,
        string CommandFingerprint,
        DateTimeOffset UpdatedAt,
        CommandExecutionResult? Result);
}

public sealed class SqliteIdempotencyStore : IIdempotencyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const int StartedState = 0;
    private const int CompletedState = 1;
    private readonly string _connectionString;
    private readonly TimeSpan _completedRetention;
    private long _operationCount;

    public SqliteIdempotencyStore(string path, TimeSpan? completedRetention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The SQLite journal path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        _completedRetention = completedRetention ?? TimeSpan.FromDays(90);
        if (_completedRetention < TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedRetention),
                "Completed idempotency retention must be at least one day.");
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
        Initialize();
    }

    public async ValueTask<IdempotencyBeginResult> TryBeginAsync(
        string idempotencyKey,
        string commandId,
        string commandFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var keyHash = IdempotencyKeyHasher.Hash(idempotencyKey);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO idempotency_entries
                (key_hash, command_id, command_fingerprint, state, result_json, updated_at_unix_ms)
            VALUES
                ($key_hash, $command_id, $command_fingerprint, $state, NULL, $updated_at_unix_ms);
            """;
        insert.Parameters.AddWithValue("$key_hash", keyHash);
        insert.Parameters.AddWithValue("$command_id", commandId);
        insert.Parameters.AddWithValue("$command_fingerprint", commandFingerprint);
        insert.Parameters.AddWithValue("$state", StartedState);
        insert.Parameters.AddWithValue("$updated_at_unix_ms", now.ToUnixTimeMilliseconds());
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 1)
        {
            transaction.Commit();
            await MaybePruneAsync(now, cancellationToken).ConfigureAwait(false);
            return new IdempotencyBeginResult(IdempotencyDisposition.Started, null, null);
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT command_id, command_fingerprint, state, result_json
            FROM idempotency_entries
            WHERE key_hash = $key_hash;
            """;
        select.Parameters.AddWithValue("$key_hash", keyHash);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The idempotency row disappeared during an atomic claim.");
        }

        var existingCommandId = reader.GetString(0);
        var existingFingerprint = reader.GetString(1);
        var state = reader.GetInt32(2);
        var resultJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        await reader.DisposeAsync().ConfigureAwait(false);
        transaction.Commit();

        if (!string.Equals(existingFingerprint, commandFingerprint, StringComparison.Ordinal))
        {
            return new IdempotencyBeginResult(
                IdempotencyDisposition.Conflict,
                null,
                existingCommandId);
        }

        if (state == StartedState)
        {
            return new IdempotencyBeginResult(IdempotencyDisposition.InProgress, null, existingCommandId);
        }

        if (state != CompletedState || string.IsNullOrWhiteSpace(resultJson))
        {
            throw new InvalidDataException("The idempotency row contains an unsupported state.");
        }

        var cached = JsonSerializer.Deserialize<CommandExecutionResult>(resultJson, SerializerOptions)
            ?? throw new InvalidDataException("The cached command result is empty.");
        return new IdempotencyBeginResult(IdempotencyDisposition.Completed, cached, existingCommandId);
    }

    public async ValueTask CompleteAsync(
        string idempotencyKey,
        CommandExecutionResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var keyHash = IdempotencyKeyHasher.Hash(idempotencyKey);
        var resultJson = JsonSerializer.Serialize(result, SerializerOptions);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE idempotency_entries
            SET state = $completed_state,
                result_json = $result_json,
                updated_at_unix_ms = $updated_at_unix_ms
            WHERE key_hash = $key_hash AND state = $started_state;
            """;
        update.Parameters.AddWithValue("$completed_state", CompletedState);
        update.Parameters.AddWithValue("$result_json", resultJson);
        update.Parameters.AddWithValue("$updated_at_unix_ms", result.CompletedAt.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$key_hash", keyHash);
        update.Parameters.AddWithValue("$started_state", StartedState);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidOperationException("Cannot complete an idempotency key that is missing or already terminal.");
        }

        transaction.Commit();
        await MaybePruneAsync(result.CompletedAt, cancellationToken).ConfigureAwait(false);
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS idempotency_entries
            (
                key_hash TEXT PRIMARY KEY NOT NULL,
                command_id TEXT NOT NULL,
                command_fingerprint TEXT NOT NULL,
                state INTEGER NOT NULL CHECK (state IN (0, 1)),
                result_json TEXT NULL,
                updated_at_unix_ms INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_idempotency_terminal_retention
            ON idempotency_entries (state, updated_at_unix_ms);
            """;
        command.ExecuteNonQuery();
    }

    private async ValueTask MaybePruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _operationCount) % 1_000 != 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM idempotency_entries
            WHERE key_hash IN
            (
                SELECT key_hash
                FROM idempotency_entries
                WHERE state = $completed_state AND updated_at_unix_ms < $cutoff
                ORDER BY updated_at_unix_ms
                LIMIT 1000
            );
            """;
        command.Parameters.AddWithValue("$completed_state", CompletedState);
        command.Parameters.AddWithValue("$cutoff", now.Subtract(_completedRetention).ToUnixTimeMilliseconds());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static class IdempotencyKeyHasher
{
    public static string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

internal static class CommandContentFingerprint
{
    public static string Compute(IAgentCommand command)
    {
        var semanticContent = command switch
        {
            ObserveMentionsCommand mentions => new
            {
                mentions.Metadata.ContractVersion,
                mentions.Metadata.WeChatInstanceId,
                mentions.CapabilityCode,
                mentions.GroupStableId,
                mentions.ExpectedGroupDisplayName,
                mentions.BotDisplayName,
                mentions.CapturedAfter
            },
            UpdateRemarkCommand remark => (object)new
            {
                remark.Metadata.ContractVersion,
                remark.Metadata.WeChatInstanceId,
                remark.CapabilityCode,
                remark.TargetKind,
                remark.TargetStableId,
                remark.ExpectedDisplayName,
                remark.ExpectedCurrentRemark,
                remark.DesiredRemark
            },
            _ => new
            {
                command.Metadata.ContractVersion,
                command.Metadata.WeChatInstanceId,
                command.CapabilityCode,
                Kind = command.Kind.ToString()
            }
        };
        var json = JsonSerializer.Serialize(semanticContent);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
