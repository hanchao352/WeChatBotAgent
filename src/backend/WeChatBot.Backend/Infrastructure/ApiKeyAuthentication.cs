using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace WeChatBot.Backend.Infrastructure;

public sealed class AuthOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string AgentApiKey { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public string ActorName { get; set; } = "admin";
    public string AgentActorName { get; set; } = "agent";
}

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthOptions> authOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string AuthenticationSchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var suppliedValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var supplied = suppliedValues.ToString();
        var options = authOptions.Value;
        string actor;
        string role;
        if (!string.IsNullOrWhiteSpace(options.ApiKey) && SecretsEqual(supplied, options.ApiKey))
        {
            actor = options.ActorName;
            role = "Admin";
        }
        else if (!string.IsNullOrWhiteSpace(options.AgentApiKey) && SecretsEqual(supplied, options.AgentApiKey))
        {
            actor = options.AgentActorName;
            role = "Agent";
        }
        else
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, actor),
            new Claim(ClaimTypes.Name, actor),
            new Claim(ClaimTypes.Role, role),
            new Claim("tenant_id", options.TenantId.ToString("D"))
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationSchemeName)));
    }

    private static bool SecretsEqual(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
