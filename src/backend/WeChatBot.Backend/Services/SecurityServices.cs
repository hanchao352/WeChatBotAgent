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

    public string ComputeIntegrityHash(AuditLog entry) => StableHash.HmacSha256(
        Canonicalize(entry),
        options.Value.IntegrityKey);

    public bool HasCurrentIntegrity(AuditLog entry) => HashEquals(
        entry.IntegrityHash,
        ComputeIntegrityHash(entry));

    public bool HasLegacyIntegrity(AuditLog entry) => HashEquals(
        entry.IntegrityHash,
        ComputeLegacyIntegrityHash(entry)) && HasSafeDelimitedFields(entry);

    public bool HasPreviousIntegrity(AuditLog entry) => HashEquals(
        entry.IntegrityHash,
        ComputePreviousIntegrityHash(entry)) && HasSafeDelimitedFields(entry);

    public bool HasValidIntegrity(AuditLog entry) =>
        HasCurrentIntegrity(entry) ||
        HasPreviousIntegrity(entry) ||
        (string.IsNullOrEmpty(entry.IpAddress) && HasLegacyIntegrity(entry));

    private static bool HashEquals(string stored, string expected)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(stored),
                Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

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
        entry.IntegrityHash = ComputeIntegrityHash(entry);
        db.AuditLogs.Add(entry);
        return entry;
    }

    private static string Canonicalize(AuditLog entry)
    {
        var canonical = new StringBuilder("audit-v2");
        AppendField(canonical, entry.Id.ToString("N"));
        AppendField(canonical, entry.TenantId.ToString("N"));
        AppendField(canonical, entry.CreatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(canonical, entry.Actor);
        AppendField(canonical, entry.Action);
        AppendField(canonical, entry.ResourceType);
        AppendField(canonical, entry.ResourceId);
        AppendField(canonical, entry.Success ? "1" : "0");
        AppendField(canonical, entry.IpAddress ?? string.Empty);
        AppendField(canonical, entry.CorrelationId);
        AppendField(canonical, entry.DetailsJson);
        return canonical.ToString();
    }

    private static void AppendField(StringBuilder canonical, string value)
    {
        canonical.Append('|');
        canonical.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
    }

    private string ComputePreviousIntegrityHash(AuditLog entry) => StableHash.HmacSha256(
        $"{entry.Id:N}|{entry.TenantId:N}|{entry.CreatedAt:O}|{entry.Actor}|{entry.Action}|{entry.ResourceType}|{entry.ResourceId}|{entry.Success}|{entry.IpAddress ?? string.Empty}|{entry.CorrelationId}|{entry.DetailsJson}",
        options.Value.IntegrityKey);

    private string ComputeLegacyIntegrityHash(AuditLog entry) => StableHash.HmacSha256(
        $"{entry.Id:N}|{entry.TenantId:N}|{entry.CreatedAt:O}|{entry.Actor}|{entry.Action}|{entry.ResourceType}|{entry.ResourceId}|{entry.Success}|{entry.CorrelationId}|{entry.DetailsJson}",
        options.Value.IntegrityKey);

    private static bool HasSafeDelimitedFields(AuditLog entry) =>
        !entry.Actor.Contains('|', StringComparison.Ordinal) &&
        !entry.Action.Contains('|', StringComparison.Ordinal) &&
        !entry.ResourceType.Contains('|', StringComparison.Ordinal) &&
        !entry.ResourceId.Contains('|', StringComparison.Ordinal) &&
        !(entry.IpAddress?.Contains('|', StringComparison.Ordinal) ?? false) &&
        !entry.CorrelationId.Contains('|', StringComparison.Ordinal);
}
