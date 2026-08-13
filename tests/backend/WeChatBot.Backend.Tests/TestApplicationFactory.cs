using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Tests;

/// <summary>创建使用独立临时 SQLite 数据库和测试配置的 ASP.NET Core 集成测试宿主。</summary>
public class TestApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>管理员测试客户端使用的固定 API Key，仅限测试进程内使用。</summary>
    public const string ApiKey = "test-api-key-with-more-than-thirty-two-characters";
    /// <summary>默认 Agent 测试客户端使用的共享兼容 Key，凭据专项测试会显式关闭该路径。</summary>
    public const string AgentApiKey = "test-agent-api-key-with-more-than-thirty-two-characters";
    /// <summary>所有测试宿主默认使用的固定租户标识。</summary>
    public static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    /// <summary>当前测试宿主的临时文件根目录，包含数据库和逻辑备份文件。</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wechatbot-backend-tests", Guid.NewGuid().ToString("N"));
    /// <summary>覆盖默认测试配置的可选键值集合。</summary>
    private readonly IReadOnlyDictionary<string, string?>? _overrides;
    /// <summary>凭据竞态测试使用的可选事务同步实现。</summary>
    private readonly IAgentMutationSynchronization? _agentMutationSynchronization;
    /// <summary>管理员备注任务竞态测试使用的可选事务同步实现。</summary>
    private readonly IRemarkTaskMutationSynchronization? _remarkTaskMutationSynchronization;
    /// <summary>测试数据库连接的默认锁等待秒数。</summary>
    private readonly int _databaseDefaultTimeoutSeconds = 30;
    /// <summary>获取测试宿主的逻辑备份目录绝对路径。</summary>
    public string BackupDirectory => Path.Combine(_root, "backups");

    /// <summary>获取测试数据库绝对路径，供锁语义回归测试建立独立连接。</summary>
    public string DatabasePath => Path.Combine(_root, "test.db");

    /// <summary>使用默认测试配置创建宿主。</summary>
    public TestApplicationFactory()
    {
    }

    /// <summary>使用指定配置覆盖项、同步实现和锁等待创建测试宿主。</summary>
    /// <param name="overrides">覆盖默认配置的键值集合。</param>
    /// <param name="agentMutationSynchronization">用于协调 Agent 事务边界的测试同步实现。</param>
    /// <param name="databaseDefaultTimeoutSeconds">SQLite 竞争连接的默认等待秒数。</param>
    /// <param name="remarkTaskMutationSynchronization">用于协调管理员备注任务事务边界的测试同步实现。</param>
    internal TestApplicationFactory(
        IReadOnlyDictionary<string, string?> overrides,
        IAgentMutationSynchronization? agentMutationSynchronization = null,
        int databaseDefaultTimeoutSeconds = 30,
        IRemarkTaskMutationSynchronization? remarkTaskMutationSynchronization = null)
    {
        _overrides = overrides;
        _agentMutationSynchronization = agentMutationSynchronization;
        _remarkTaskMutationSynchronization = remarkTaskMutationSynchronization;
        _databaseDefaultTimeoutSeconds = databaseDefaultTimeoutSeconds;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = $"Data Source={DatabasePath};Default Timeout={_databaseDefaultTimeoutSeconds};Pooling=False",
                ["Auth:ApiKey"] = ApiKey,
                ["Auth:AgentApiKey"] = AgentApiKey,
                ["Auth:TenantId"] = TenantId.ToString("D"),
                ["Auth:ActorName"] = "integration-test-admin",
                ["Auth:AgentActorName"] = "integration-test-agent",
                ["Auth:AllowAgentAutoRegistration"] = "true",
                // 现有非凭据专项测试显式使用旧共享 Key；新增安全测试会覆盖独立凭据路径。
                ["Auth:AllowLegacySharedAgentApiKey"] = "true",
                ["Activation:HashPepper"] = "integration-test-activation-pepper-32-characters-minimum",
                ["Audit:IntegrityKey"] = "integration-test-audit-integrity-key-32-characters-minimum",
                ["Pagination:ProtectionKey"] = "integration-test-cursor-protection-key-32-characters-minimum",
                ["RemarkTaskLease:DurationSeconds"] = "60",
                ["Backup:Directory"] = BackupDirectory,
                ["Backup:EncryptionKeyBase64"] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("integration-test-backup-key"u8.ToArray()))
            };
            if (_overrides is not null)
            {
                foreach (var pair in _overrides) values[pair.Key] = pair.Value;
            }
            configuration.AddInMemoryCollection(values);
        });
        if (_agentMutationSynchronization is not null)
        {
            // 专项并发测试以单例同步点替换生产空实现，使两个 HTTP 请求稳定停在目标事务边界。
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentMutationSynchronization>();
                services.AddSingleton(_agentMutationSynchronization);
            });
        }
        if (_remarkTaskMutationSynchronization is not null)
        {
            // 管理员备注竞态测试使用独立同步点，避免影响凭据竞态的 Agent 钩子。
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRemarkTaskMutationSynchronization>();
                services.AddSingleton(_remarkTaskMutationSynchronization);
            });
        }
    }

    /// <summary>创建携带管理员测试 API Key 的 HTTP 客户端。</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    /// <summary>创建携带默认 Agent 兼容 API Key 的 HTTP 客户端。</summary>
    public HttpClient CreateAgentClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", AgentApiKey);
        return client;
    }

    /// <summary>释放测试宿主并尽力删除本次测试生成的临时文件。</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !Directory.Exists(_root)) return;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Test output is isolated under the OS temp directory and can be reclaimed later.
        }
        catch (UnauthorizedAccessException)
        {
            // A transient file handle must not mask the test result.
        }
    }
}
