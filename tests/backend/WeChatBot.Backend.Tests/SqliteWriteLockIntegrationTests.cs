using System.Data;
using Microsoft.Data.Sqlite;

namespace WeChatBot.Backend.Tests;

/// <summary>
/// 验证备注租约和 Agent 上报事务依赖的 SQLite 写锁语义，防止把无副作用语句误当作互斥屏障。
/// </summary>
public sealed class SqliteWriteLockIntegrationTests
{
    /// <summary>
    /// 验证延迟事务中的零行更新会升级为写事务，使第二连接在首事务提交前无法取得写锁。
    /// </summary>
    [Fact]
    public async Task Zero_row_update_acquires_sqlite_write_lock()
    {
        // 每次测试使用独立文件和关闭连接池的连接串，避免其他测试连接影响锁观察。
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "wechatbot-backend-tests",
            $"sqlite-write-lock-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = $"Data Source={databasePath};Default Timeout=1;Pooling=False";

        try
        {
            await InitializeDatabaseAsync(connectionString);
            await using var firstConnection = new SqliteConnection(connectionString);
            await firstConnection.OpenAsync();
            await using var firstTransaction = firstConnection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: true);

            // 该语句不修改任何行，但 SQLite 仍应把事务升级为写事务并持有数据库写锁。
            await new SqliteCommand(
                    "UPDATE LockProbe SET Version = Version WHERE 0 = 1;",
                    firstConnection,
                    firstTransaction)
                .ExecuteNonQueryAsync();

            await using var secondConnection = new SqliteConnection(connectionString);
            await secondConnection.OpenAsync();

            // 非延迟事务会立即尝试取得写锁；首事务未结束时必须收到 SQLITE_BUSY/LOCKED。
            var exception = Assert.Throws<SqliteException>(() =>
                secondConnection.BeginTransaction(IsolationLevel.Serializable, deferred: false));
            Assert.Contains(exception.SqliteErrorCode, new[] { 5, 6 });
            await firstTransaction.RollbackAsync();
        }
        finally
        {
            // 所有连接均已释放后删除独立数据库；清理失败不应掩盖锁语义断言结果。
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>创建锁探针使用的最小 SQLite 表。</summary>
    /// <param name="connectionString">关闭连接池且指向独立临时数据库的连接串。</param>
    private static async Task InitializeDatabaseAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await new SqliteCommand(
                "CREATE TABLE LockProbe (Id INTEGER PRIMARY KEY, Version INTEGER NOT NULL);" +
                "INSERT INTO LockProbe (Version) VALUES (1);",
                connection)
            .ExecuteNonQueryAsync();
    }
}
