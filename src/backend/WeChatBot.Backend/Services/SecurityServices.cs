using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Services;

public sealed class ActivationOptions
{
    public string HashPepper { get; set; } = string.Empty;
}

public sealed class AuditOptions
{
    public string IntegrityKey { get; set; } = string.Empty;
}

public static class StableHash
{
    public static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    public static string HmacSha256(string value, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed class ActivationCodeHasher(IOptions<ActivationOptions> options)
{
    public string Hash(string code)
    {
        var normalized = Normalize(code);
        return StableHash.HmacSha256(normalized, options.Value.HashPepper);
    }

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();
}

public sealed class AuditService(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    IOptions<AuditOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AuditLog Add(
        string action,
        string resourceType,
        string resourceId,
        bool success = true,
        object? details = null)
    {
        var detailsJson = JsonSerializer.Serialize(details ?? new { }, JsonOptions);
        var entry = new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            CreatedAt = timeProvider.GetUtcNow(),
            Actor = tenant.Actor,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Success = success,
            IpAddress = tenant.IpAddress,
            CorrelationId = tenant.CorrelationId,
            DetailsJson = detailsJson
        };
        entry.IntegrityHash = StableHash.HmacSha256(
            $"{entry.Id:N}|{entry.TenantId:N}|{entry.CreatedAt:O}|{entry.Actor}|{entry.Action}|{entry.ResourceType}|{entry.ResourceId}|{entry.Success}|{entry.CorrelationId}|{entry.DetailsJson}",
            options.Value.IntegrityKey);
        db.AuditLogs.Add(entry);
        return entry;
    }
}
