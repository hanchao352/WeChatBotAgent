using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Data;

namespace WeChatBot.Backend.Infrastructure;

/// <summary>
/// 保存一次已通过认证并与数据库当前注册状态复核后的 Agent 身份。
/// </summary>
/// <param name="RegistrationId">AgentRegistration 主键。</param>
/// <param name="AgentId">注册记录中的展示用 AgentId。</param>
/// <param name="NormalizedAgentId">用于数据库唯一匹配的规范化 AgentId。</param>
/// <param name="WeChatInstanceId">注册记录固定绑定的微信实例。</param>
/// <param name="TenantId">注册记录所属租户。</param>
public sealed record BoundAgentIdentity(
    Guid RegistrationId,
    string AgentId,
    string NormalizedAgentId,
    string WeChatInstanceId,
    Guid TenantId);

/// <summary>
/// 集中校验认证 claim、路由/正文身份与数据库当前 AgentRegistration 完全一致，避免各端点重复且遗漏校验。
/// </summary>
public sealed class AgentIdentityBindingService(
    AppDbContext db,
    TenantContext tenant,
    IHttpContextAccessor httpContextAccessor,
    IOptions<AuthOptions> authOptions)
{
    /// <summary>
    /// 获取当前认证是否来自显式兼容的旧共享凭据；生产环境启动门禁保证该值永远为假。
    /// </summary>
    public bool IsLegacySharedCredential =>
        httpContextAccessor.HttpContext?.User.HasClaim(
            AgentIdentityClaims.LegacySharedCredential,
            bool.TrueString) == true;

    /// <summary>
    /// 校验心跳身份。独立凭据始终要求完全匹配；旧共享凭据允许实例不匹配进入既有拒绝心跳分支，
    /// 以便兼容测试仍能收到 Accepted=false，且不会写入冒用实例的心跳状态。
    /// </summary>
    /// <param name="agentId">心跳正文中的 AgentId。</param>
    /// <param name="weChatInstanceId">心跳正文中的微信实例标识。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>由认证与注册事实共同确定的 Agent 身份。</returns>
    public Task<BoundAgentIdentity> RequireHeartbeatAsync(
        string agentId,
        string weChatInstanceId,
        CancellationToken cancellationToken) =>
        RequireCoreAsync(agentId, weChatInstanceId, cancellationToken);

    /// <summary>
    /// 校验当前 Agent 身份。独立凭据路径会复核注册版本状态、摘要和绑定；兼容共享 Key 仅在显式开关下走旧路径。
    /// </summary>
    /// <param name="agentId">路由或心跳正文中的 AgentId。</param>
    /// <param name="weChatInstanceId">正文中的微信实例标识。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>由服务端数据库事实构造的已绑定身份。</returns>
    public Task<BoundAgentIdentity> RequireAsync(
        string agentId,
        string weChatInstanceId,
        CancellationToken cancellationToken) =>
        RequireCoreAsync(agentId, weChatInstanceId, cancellationToken);

    /// <summary>执行独立凭据与兼容凭据共用的身份校验流程。</summary>
    /// <param name="agentId">路由或正文中的 AgentId。</param>
    /// <param name="weChatInstanceId">正文中的微信实例标识。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>数据库当前有效注册对应的绑定身份。</returns>
    private async Task<BoundAgentIdentity> RequireCoreAsync(
        string agentId,
        string weChatInstanceId,
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole("Agent"))
        {
            throw DomainException.Forbidden(
                "agent_identity_required",
                "A verified Agent identity is required for this operation.");
        }

        ValidatePresentedIdentity(agentId, weChatInstanceId);
        if (principal.HasClaim(
                AgentIdentityClaims.LegacySharedCredential,
                bool.TrueString))
        {
            // 旧共享凭据无法证明设备身份，兼容路径保持原有业务层 409 语义；Production 永远无法启用此分支。
            return await RequireLegacyAsync(
                agentId,
                weChatInstanceId,
                cancellationToken);
        }

        var registrationIdText = principal.FindFirstValue(AgentIdentityClaims.RegistrationId);
        var claimedTenantText = principal.FindFirstValue(AgentIdentityClaims.TenantId);
        var claimedCredentialHash = principal.FindFirstValue(AgentIdentityClaims.CredentialHash);
        if (!Guid.TryParse(registrationIdText, out var registrationId) ||
            !Guid.TryParse(claimedTenantText, out var claimedTenantId) ||
            claimedTenantId != tenant.TenantId ||
            string.IsNullOrWhiteSpace(claimedCredentialHash))
        {
            throw IdentityMismatch();
        }

        // 每次业务操作重新读取注册，确保认证后发生的轮换、吊销或禁用立即使旧请求身份失效。
        var registration = await db.AgentRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == registrationId, cancellationToken);
        if (registration is null ||
            !registration.IsEnabled ||
            registration.CredentialRevokedAt is not null ||
            !AgentCredentialSecurity.HashesEqual(registration.CredentialHash, claimedCredentialHash) ||
            !string.Equals(
                registration.AgentId,
                principal.FindFirstValue(AgentIdentityClaims.AgentId),
                StringComparison.Ordinal) ||
            !string.Equals(
                registration.WeChatInstanceId,
                principal.FindFirstValue(AgentIdentityClaims.WeChatInstanceId),
                StringComparison.Ordinal) ||
            !string.Equals(registration.AgentId, agentId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(registration.WeChatInstanceId, weChatInstanceId.Trim(), StringComparison.Ordinal))
        {
            throw IdentityMismatch();
        }

        return new BoundAgentIdentity(
            registration.Id,
            registration.AgentId,
            registration.NormalizedAgentId,
            registration.WeChatInstanceId,
            registration.TenantId);
    }

    /// <summary>验证调用方身份字段的基本长度和非空约束，避免绕过 MVC 模型校验的内部调用。</summary>
    /// <param name="agentId">调用方提供的 AgentId。</param>
    /// <param name="weChatInstanceId">调用方提供的微信实例标识。</param>
    private static void ValidatePresentedIdentity(string agentId, string weChatInstanceId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 128 ||
            string.IsNullOrWhiteSpace(weChatInstanceId) || weChatInstanceId.Length > 128)
        {
            throw DomainException.Validation(
                "invalid_agent_binding",
                "AgentId and WeChatInstanceId are required and must be at most 128 characters.");
        }
    }

    /// <summary>
    /// 在开发/测试兼容模式中按请求字段读取注册；该路径没有密码学设备归属能力，不能用于生产。
    /// </summary>
    /// <param name="agentId">调用方声明的 AgentId。</param>
    /// <param name="weChatInstanceId">调用方声明的微信实例标识。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>仅供旧测试迁移使用的注册身份。</returns>
    private async Task<BoundAgentIdentity> RequireLegacyAsync(
        string agentId,
        string weChatInstanceId,
        CancellationToken cancellationToken)
    {
        if (!authOptions.Value.AllowLegacySharedAgentApiKey)
        {
            throw IdentityMismatch();
        }

        var normalizedAgentId = Services.AgentControlService.NormalizeAgentId(agentId);
        var normalizedInstanceId = weChatInstanceId.Trim();
        var registration = await db.AgentRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.NormalizedAgentId == normalizedAgentId, cancellationToken);
        if (registration is null)
        {
            // 返回空注册主键使后续在线租约检查产生既有 agent_lease_unavailable，而不会创建或授权任何记录。
            return new BoundAgentIdentity(
                Guid.Empty,
                agentId.Trim(),
                normalizedAgentId,
                normalizedInstanceId,
                tenant.TenantId);
        }
        if (!registration.IsEnabled)
        {
            throw IdentityMismatch();
        }

        return new BoundAgentIdentity(
            registration.Id,
            registration.AgentId,
            registration.NormalizedAgentId,
            registration.WeChatInstanceId,
            registration.TenantId);
    }

    /// <summary>创建不泄露注册是否存在、凭据状态或绑定字段的统一 403 异常。</summary>
    /// <returns>可由全局异常处理器转换为 Forbidden 的领域异常。</returns>
    private static DomainException IdentityMismatch() => DomainException.Forbidden(
        "agent_identity_mismatch",
        "The authenticated Agent identity does not match the requested Agent and WeChat binding.");
}
