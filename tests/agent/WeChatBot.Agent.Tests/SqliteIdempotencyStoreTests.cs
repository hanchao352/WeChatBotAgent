using System.Text;
using WeChatBot.Agent.Contracts;
using WeChatBot.Agent.Execution;

namespace WeChatBot.Agent.Tests;

public sealed class SqliteIdempotencyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "WeChatBot.Agent.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedResultSurvivesStoreRestart()
    {
        var path = Path.Combine(_directory, "journal.db");
        var now = DateTimeOffset.UtcNow;
        var firstStore = new SqliteIdempotencyStore(path);
        var begin = await firstStore.TryBeginAsync("private-key", "command-1", "content-1", now, default);
        Assert.Equal(IdempotencyDisposition.Started, begin.Disposition);
        var result = CommandExecutionResult.Create(
            "command-1",
            CommandResultStatus.DryRun,
            "DONE",
            "done",
            now,
            now.AddSeconds(1));
        await firstStore.CompleteAsync("private-key", result, default);

        var restartedStore = new SqliteIdempotencyStore(path);
        var duplicate = await restartedStore.TryBeginAsync(
            "private-key",
            "command-2",
            "content-1",
            now.AddMinutes(1),
            default);

        Assert.Equal(IdempotencyDisposition.Completed, duplicate.Disposition);
        Assert.Equal(result, duplicate.CachedResult);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var journal = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain("private-key", journal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InProgressResultAfterRestartRequiresReview()
    {
        var path = Path.Combine(_directory, "journal.db");
        var firstStore = new SqliteIdempotencyStore(path);
        await firstStore.TryBeginAsync("key", "command-1", "content-1", DateTimeOffset.UtcNow, default);

        var restartedStore = new SqliteIdempotencyStore(path);
        var duplicate = await restartedStore.TryBeginAsync(
            "key",
            "command-2",
            "content-1",
            DateTimeOffset.UtcNow,
            default);

        Assert.Equal(IdempotencyDisposition.InProgress, duplicate.Disposition);
        Assert.Equal("command-1", duplicate.ExistingCommandId);
    }

    [Fact]
    public async Task SameKeyWithDifferentContentIsAConflict()
    {
        var path = Path.Combine(_directory, "journal.db");
        var store = new SqliteIdempotencyStore(path);
        await store.TryBeginAsync("key", "command-1", "content-1", DateTimeOffset.UtcNow, default);

        var conflict = await store.TryBeginAsync(
            "key",
            "command-2",
            "content-2",
            DateTimeOffset.UtcNow,
            default);

        Assert.Equal(IdempotencyDisposition.Conflict, conflict.Disposition);
        Assert.Equal("command-1", conflict.ExistingCommandId);
    }

    [Fact]
    public async Task ConcurrentClaimsHaveExactlyOneWinner()
    {
        var path = Path.Combine(_directory, "journal.db");
        var stores = Enumerable.Range(0, 20)
            .Select(_ => new SqliteIdempotencyStore(path))
            .ToArray();

        var claims = await Task.WhenAll(stores.Select((store, index) =>
            store.TryBeginAsync(
                "shared-key",
                $"command-{index}",
                "same-content",
                DateTimeOffset.UtcNow,
                default).AsTask()));

        Assert.Single(claims, claim => claim.Disposition == IdempotencyDisposition.Started);
        Assert.Equal(19, claims.Count(claim => claim.Disposition == IdempotencyDisposition.InProgress));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
