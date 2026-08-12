using System.Security.Claims;

namespace WeChatBot.Backend.Infrastructure;

public sealed class TenantContext(IHttpContextAccessor httpContextAccessor)
{
    public Guid TenantId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User.FindFirstValue("tenant_id");
            return Guid.TryParse(value, out var tenantId) ? tenantId : Guid.Empty;
        }
    }

    public string Actor =>
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";

    public string CorrelationId =>
        httpContextAccessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N");

    public string? IpAddress =>
        httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
