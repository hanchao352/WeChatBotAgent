using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WeChatBot.Backend.Tests;

public class TestApplicationFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key-with-more-than-thirty-two-characters";
    public const string AgentApiKey = "test-agent-api-key-with-more-than-thirty-two-characters";
    public static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wechatbot-backend-tests", Guid.NewGuid().ToString("N"));
    public string BackupDirectory => Path.Combine(_root, "backups");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = $"Data Source={Path.Combine(_root, "test.db")};Default Timeout=30;Pooling=False",
                ["Auth:ApiKey"] = ApiKey,
                ["Auth:AgentApiKey"] = AgentApiKey,
                ["Auth:TenantId"] = TenantId.ToString("D"),
                ["Auth:ActorName"] = "integration-test-admin",
                ["Activation:HashPepper"] = "integration-test-activation-pepper-32-characters-minimum",
                ["Audit:IntegrityKey"] = "integration-test-audit-integrity-key-32-characters-minimum",
                ["Backup:Directory"] = BackupDirectory,
                ["Backup:EncryptionKeyBase64"] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("integration-test-backup-key"u8.ToArray()))
            });
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    public HttpClient CreateAgentClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", AgentApiKey);
        return client;
    }

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
