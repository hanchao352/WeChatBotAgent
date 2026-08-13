using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Data;

namespace WeChatBot.Backend.Infrastructure;

/// <summary>
/// 保存管理员鉴权、租户边界以及仅供开发和测试迁移使用的共享 Agent 凭据配置。
/// </summary>
public sealed class AuthOptions
{
    /// <summary>获取或设置管理员 API Key；生产环境必须由安全配置源注入。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置旧版租户共享 Agent API Key；只有显式打开兼容开关时才会读取，生产环境禁止启用。
    /// </summary>
    public string AgentApiKey { get; set; } = string.Empty;

    /// <summary>获取或设置当前单租户部署的租户标识。</summary>
    public Guid TenantId { get; set; }

    /// <summary>获取或设置管理员审计主体名称。</summary>
    public string ActorName { get; set; } = "admin";

    /// <summary>获取或设置旧版共享 Agent 凭据的审计主体名称。</summary>
    public string AgentActorName { get; set; } = "agent";

    /// <summary>获取或设置旧版共享凭据是否可自动创建 Agent 注册；仅兼容测试可用。</summary>
    public bool AllowAgentAutoRegistration { get; set; }

    /// <summary>
    /// 获取或设置是否启用无法绑定具体注册身份的旧共享凭据；Production 启动门禁始终拒绝该值为真。
    /// </summary>
    public bool AllowLegacySharedAgentApiKey { get; set; }
}

/// <summary>
/// 集中定义认证生成的 Agent 身份声明，避免控制器和服务散落易拼错的字符串字面量。
/// </summary>
public static class AgentIdentityClaims
{
    /// <summary>绑定的 AgentRegistration 主键声明。</summary>
    public const string RegistrationId = "agent_registration_id";

    /// <summary>绑定的展示用 AgentId 声明。</summary>
    public const string AgentId = "agent_id";

    /// <summary>绑定的微信实例标识声明。</summary>
    public const string WeChatInstanceId = "wechat_instance_id";

    /// <summary>租户声明；管理员和 Agent 均使用同一名称。</summary>
    public const string TenantId = "tenant_id";

    /// <summary>
    /// 当前凭据摘要的服务端内部声明，用于认证完成后再次核对轮换或吊销竞态；不得写入日志或响应。
    /// </summary>
    public const string CredentialHash = "agent_credential_hash";

    /// <summary>标记无法绑定具体注册的旧共享凭据，仅兼容开发和测试。</summary>
    public const string LegacySharedCredential = "legacy_shared_agent_credential";
}

/// <summary>
/// 提供 Agent 独立凭据的密码学安全生成、不可逆摘要和固定时间摘要比较。
/// </summary>
public static class AgentCredentialSecurity
{
    /// <summary>随机载荷字节数；256 位熵满足不可猜测凭据要求。</summary>
    public const int RandomByteCount = 32;

    /// <summary>SHA-256 小写十六进制摘要长度。</summary>
    public const int HashLength = 64;

    /// <summary>凭据固定前缀，便于运维识别其类型且不降低随机载荷熵。</summary>
    private const string CredentialPrefix = "wba_";

    /// <summary>生成只应在签发响应中出现一次的 256 位随机 Agent 凭据。</summary>
    /// <returns>适合 HTTP 请求头传输的 Base64Url 明文凭据。</returns>
    public static string CreateCredential()
    {
        // 使用系统密码学随机源，去除 Base64 填充并替换 URL 特殊字符，避免 shell 和代理层转义差异。
        var encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(RandomByteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return CredentialPrefix + encoded;
    }

    /// <summary>计算只用于持久化和索引查询的 SHA-256 小写十六进制摘要。</summary>
    /// <param name="credential">请求携带或刚生成的明文凭据。</param>
    /// <returns>固定 64 字符摘要，无法还原明文凭据。</returns>
    public static string HashCredential(string credential) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    /// <summary>以固定时间比较两个合法十六进制摘要，畸形数据库值按不匹配处理。</summary>
    /// <param name="left">第一个 SHA-256 十六进制摘要。</param>
    /// <param name="right">第二个 SHA-256 十六进制摘要。</param>
    /// <returns>两个摘要字节完全一致时为真。</returns>
    public static bool HashesEqual(string? left, string? right)
    {
        if (left?.Length != HashLength || right?.Length != HashLength) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            // 历史或损坏数据不是有效凭据摘要，必须关闭认证而不是抛出 500。
            return false;
        }
    }
}

/// <summary>
/// 先校验管理员密钥，再通过独立凭据摘要查询真实 AgentRegistration 并生成不可伪造的绑定声明。
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthOptions> authOptions,
    AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    /// <summary>ASP.NET Core 注册使用的鉴权方案名。</summary>
    public const string AuthenticationSchemeName = "ApiKey";

    /// <summary>管理员和 Agent 发送凭据的 HTTP 请求头名称。</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>限制外部凭据长度，避免异常超长请求在哈希前制造无界内存压力。</summary>
    private const int MaximumCredentialLength = 512;

    /// <summary>校验请求凭据并构建管理员、独立 Agent 或显式兼容身份。</summary>
    /// <returns>认证成功票据、无凭据结果或统一失败结果。</returns>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var suppliedValues))
        {
            return AuthenticateResult.NoResult();
        }

        var supplied = suppliedValues.ToString();
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > MaximumCredentialLength)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var options = authOptions.Value;
        if (!string.IsNullOrWhiteSpace(options.ApiKey) && SecretsEqual(supplied, options.ApiKey))
        {
            return Success(CreateAdminClaims(options));
        }

        // 摘要查询显式绕过依赖尚未建立 tenant claim 的全局过滤器，同时在 SQL 条件中重新锁定配置租户。
        var suppliedHash = AgentCredentialSecurity.HashCredential(supplied);
        var registration = await db.AgentRegistrations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.TenantId == options.TenantId &&
                             candidate.CredentialHash == suppliedHash &&
                             candidate.CredentialRevokedAt == null &&
                             candidate.IsEnabled,
                Context.RequestAborted);
        if (registration is not null)
        {
            return Success(CreateAgentClaims(registration, suppliedHash));
        }

        // 兼容身份故意不生成注册绑定声明；仅旧测试可使用，业务服务可据此走显式旧路径。
        if (options.AllowLegacySharedAgentApiKey &&
            !string.IsNullOrWhiteSpace(options.AgentApiKey) &&
            SecretsEqual(supplied, options.AgentApiKey))
        {
            return Success(CreateLegacyAgentClaims(options));
        }

        return AuthenticateResult.Fail("Invalid API key.");
    }

    /// <summary>创建管理员身份声明。</summary>
    /// <param name="options">已验证的鉴权配置。</param>
    /// <returns>包含管理员角色和租户边界的声明集合。</returns>
    private static Claim[] CreateAdminClaims(AuthOptions options) =>
    [
        new Claim(ClaimTypes.NameIdentifier, options.ActorName),
        new Claim(ClaimTypes.Name, options.ActorName),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim(AgentIdentityClaims.TenantId, options.TenantId.ToString("D"))
    ];

    /// <summary>从数据库注册事实创建独立 Agent 身份声明。</summary>
    /// <param name="registration">凭据摘要唯一匹配的已启用注册。</param>
    /// <param name="credentialHash">本次请求凭据的摘要，仅供服务端竞态复核。</param>
    /// <returns>同时绑定注册、Agent、微信实例和租户的声明集合。</returns>
    private static Claim[] CreateAgentClaims(
        Domain.AgentRegistration registration,
        string credentialHash) =>
    [
        new Claim(ClaimTypes.NameIdentifier, registration.Id.ToString("D")),
        new Claim(ClaimTypes.Name, registration.AgentId),
        new Claim(ClaimTypes.Role, "Agent"),
        new Claim(AgentIdentityClaims.RegistrationId, registration.Id.ToString("D")),
        new Claim(AgentIdentityClaims.AgentId, registration.AgentId),
        new Claim(AgentIdentityClaims.WeChatInstanceId, registration.WeChatInstanceId),
        new Claim(AgentIdentityClaims.TenantId, registration.TenantId.ToString("D")),
        new Claim(AgentIdentityClaims.CredentialHash, credentialHash)
    ];

    /// <summary>创建明确标记为无设备绑定能力的旧共享 Agent 身份。</summary>
    /// <param name="options">包含兼容审计主体和租户的配置。</param>
    /// <returns>仅能由服务层显式兼容的 Agent 角色声明。</returns>
    private static Claim[] CreateLegacyAgentClaims(AuthOptions options) =>
    [
        new Claim(ClaimTypes.NameIdentifier, options.AgentActorName),
        new Claim(ClaimTypes.Name, options.AgentActorName),
        new Claim(ClaimTypes.Role, "Agent"),
        new Claim(AgentIdentityClaims.TenantId, options.TenantId.ToString("D")),
        new Claim(AgentIdentityClaims.LegacySharedCredential, bool.TrueString)
    ];

    /// <summary>将声明集合封装成当前方案的成功认证结果。</summary>
    /// <param name="claims">已经过服务端验证的声明。</param>
    /// <returns>可供授权中间件使用的认证票据。</returns>
    private static AuthenticateResult Success(IEnumerable<Claim> claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationSchemeName));
    }

    /// <summary>通过哈希后固定时间比较配置密钥，避免直接字符串比较泄露前缀信息。</summary>
    /// <param name="left">请求提供的密钥。</param>
    /// <param name="right">配置中的预期密钥。</param>
    /// <returns>两个密钥完整一致时为真。</returns>
    private static bool SecretsEqual(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
