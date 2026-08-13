using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Services;

public static class ServiceDurationCalculator
{
    public static DateTimeOffset? CalculateEnd(DateTimeOffset startsAt, ServiceDurationKind duration) => duration switch
    {
        ServiceDurationKind.Days30 => startsAt.AddDays(30),
        ServiceDurationKind.Days60 => startsAt.AddDays(60),
        ServiceDurationKind.Days90 => startsAt.AddDays(90),
        ServiceDurationKind.HalfYear => startsAt.AddMonths(6),
        ServiceDurationKind.OneYear => startsAt.AddYears(1),
        ServiceDurationKind.Permanent => null,
        _ => throw DomainException.Validation("invalid_duration", "The service duration is not supported.")
    };
}

public static class EntitlementEvaluator
{
    public static bool IsActive(Entitlement entitlement, DateTimeOffset instant) =>
        entitlement.State == EntitlementState.Active &&
        instant >= entitlement.StartsAt &&
        (entitlement.EndsAt is null || instant < entitlement.EndsAt.Value);

    public static string EffectiveStatus(Entitlement entitlement, DateTimeOffset instant)
    {
        if (entitlement.State == EntitlementState.Revoked) return "revoked";
        if (entitlement.State == EntitlementState.Suspended) return "suspended";
        if (instant < entitlement.StartsAt) return "scheduled";
        if (entitlement.EndsAt is not null && instant >= entitlement.EndsAt.Value) return "expired";
        return "active";
    }
}

public static class PackageFeatureSet
{
    public static bool Contains(string featuresJson, string requiredFeature)
    {
        if (string.IsNullOrWhiteSpace(featuresJson) || string.IsNullOrWhiteSpace(requiredFeature)) return false;

        try
        {
            using var document = JsonDocument.Parse(featuresJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            return document.RootElement.EnumerateArray().Any(element =>
                element.ValueKind == JsonValueKind.String &&
                string.Equals(
                    element.GetString()?.Trim(),
                    requiredFeature.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record RedemptionResult(
    Guid EntitlementId,
    string PackageCode,
    ServiceDurationKind Duration,
    ServiceTargetKind TargetKind,
    Guid TargetId,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string Status,
    bool Replayed);

public sealed class EntitlementService(AppDbContext db, TimeProvider timeProvider)
{
    public async Task<Entitlement?> FindActiveAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default)
    {
        var instant = at ?? timeProvider.GetUtcNow();
        var candidates = await db.Entitlements
            .Where(x =>
                x.TargetKind == targetKind &&
                x.TargetId == targetId &&
                x.State == EntitlementState.Active &&
                x.StartsAt <= instant &&
                (x.EndsAt == null || x.EndsAt > instant))
            .OrderByDescending(x => x.EndsAt == null)
            .ThenByDescending(x => x.EndsAt)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault();
    }

    public async Task<Entitlement?> FindActiveWithFeatureAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        string requiredFeature,
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requiredFeature))
            throw new ArgumentException("A required feature is required.", nameof(requiredFeature));

        var instant = at ?? timeProvider.GetUtcNow();
        var candidates = await db.Entitlements
            .Where(x =>
                x.TargetKind == targetKind &&
                x.TargetId == targetId &&
                x.State == EntitlementState.Active &&
                x.StartsAt <= instant &&
                (x.EndsAt == null || x.EndsAt > instant))
            .Join(
                db.ServicePackages,
                entitlement => entitlement.PackageId,
                package => package.Id,
                (entitlement, package) => new { Entitlement = entitlement, Package = package })
            .OrderByDescending(x => x.Entitlement.EndsAt == null)
            .ThenByDescending(x => x.Entitlement.EndsAt)
            .ToListAsync(cancellationToken);
        var matching = candidates
            .Where(x =>
                PackageFeatureSet.Contains(x.Package.FeaturesJson, requiredFeature) &&
                (x.Package.Tier != PackageTier.AdvancedGeneral || targetKind == ServiceTargetKind.Group))
            .ToList();
        if (matching.Count == 0) return null;

        var basicMatch = matching.FirstOrDefault(x => x.Package.Tier == PackageTier.Basic);
        if (basicMatch is not null) return basicMatch.Entitlement;

        var basicPackageIds = await db.ServicePackages.AsNoTracking()
            .Where(x => x.Tier == PackageTier.Basic)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var hasActiveBasicDependency = await db.Entitlements.AsNoTracking().AnyAsync(x =>
            x.TargetKind == targetKind &&
            x.TargetId == targetId &&
            basicPackageIds.Contains(x.PackageId) &&
            x.State == EntitlementState.Active &&
            x.StartsAt <= instant &&
            (x.EndsAt == null || x.EndsAt > instant), cancellationToken);

        return hasActiveBasicDependency
            ? matching[0].Entitlement
            : null;
    }
}

public sealed class ActivationService(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    ActivationCodeHasher codeHasher,
    AuditService audit)
{
    private const string RedeemOperation = "activation.redeem";
    private const string DirectActivateOperation = "entitlement.activate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(ActivationCode Entity, string Plaintext)> IssueAsync(
        string packageCode,
        ServiceDurationKind duration,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        _ = ServiceDurationCalculator.CalculateEnd(timeProvider.GetUtcNow(), duration);
        var package = await db.ServicePackages.SingleOrDefaultAsync(
            x => x.Code == packageCode && x.IsEnabled, cancellationToken)
            ?? throw DomainException.NotFound("Service package");

        var now = timeProvider.GetUtcNow();
        var expiration = expiresAt ?? now.AddDays(30);
        if (expiration <= now || expiration > now.AddYears(2))
        {
            throw DomainException.Validation("invalid_expiration", "Activation code expiration must be in the future and no more than two years away.");
        }

        var plaintext = GenerateCode();
        var entity = new ActivationCode
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            CodeHash = codeHasher.Hash(plaintext),
            PackageId = package.Id,
            DurationKind = duration,
            CreatedAt = now,
            ExpiresAt = expiration,
            CreatedBy = tenant.Actor
        };
        db.ActivationCodes.Add(entity);
        audit.Add("activation-code.issued", nameof(ActivationCode), entity.Id.ToString("D"), details: new
        {
            package.Code,
            duration,
            expiration
        });
        await db.SaveChangesAsync(cancellationToken);
        return (entity, plaintext);
    }

    public async Task<RedemptionResult> RedeemAsync(
        string code,
        ServiceTargetKind targetKind,
        Guid targetId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        idempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw DomainException.Validation("invalid_idempotency_key", "Idempotency-Key is required and must be at most 128 characters.");
        }

        var codeHash = codeHasher.Hash(code);
        var requestHash = StableHash.Sha256($"{codeHash}|{targetKind}|{targetId:N}");

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var replay = await TryReadIdempotentResultAsync(
                    RedeemOperation, idempotencyKey, requestHash, cancellationToken);
                if (replay is not null) return replay with { Replayed = true };

                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

                var activationCode = await db.ActivationCodes
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CodeHash == codeHash, cancellationToken)
                    ?? throw DomainException.NotFound("Activation code");

                if (activationCode.RedeemedAt is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await ResolveExistingRedemptionAsync(
                        activationCode, targetKind, targetId, idempotencyKey, requestHash, cancellationToken);
                }

                if (activationCode.RevokedAt is not null)
                {
                    throw DomainException.Conflict("activation_code_revoked", "The activation code has been revoked.");
                }

                var now = timeProvider.GetUtcNow();
                if (activationCode.ExpiresAt <= now)
                {
                    throw DomainException.Conflict("activation_code_expired", "The activation code has expired.");
                }

                await EnsureTargetExistsAsync(targetKind, targetId, cancellationToken);
                var package = await db.ServicePackages.SingleAsync(x => x.Id == activationCode.PackageId, cancellationToken);
                if (!package.IsEnabled)
                {
                    throw DomainException.Conflict(
                        "service_package_disabled",
                        "The service package for this activation code is disabled; the activation code was not consumed.");
                }
                if (package.Tier == PackageTier.AdvancedGeneral && targetKind != ServiceTargetKind.Group)
                {
                    throw DomainException.Validation(
                        "advanced_package_requires_group",
                        "Advanced general service packages can only be activated for group targets.");
                }
                if (package.Tier == PackageTier.AdvancedGeneral &&
                    !await HasActiveBasicDependencyAsync(targetKind, targetId, now, cancellationToken))
                {
                    throw DomainException.Conflict(
                        "advanced_package_requires_basic",
                        "An active basic service entitlement is required before activating an advanced service package.");
                }

                var startsAt = await ResolveEntitlementStartAsync(
                    targetKind,
                    targetId,
                    activationCode.PackageId,
                    now,
                    cancellationToken);
                var endsAt = ServiceDurationCalculator.CalculateEnd(startsAt, activationCode.DurationKind);
                var entitlementId = Guid.NewGuid();
                var affected = await db.ActivationCodes
                    .Where(x => x.Id == activationCode.Id && x.RedeemedAt == null && x.RevokedAt == null && x.ExpiresAt > now)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.RedeemedAt, now)
                        .SetProperty(x => x.RedeemedTargetKind, targetKind)
                        .SetProperty(x => x.RedeemedTargetId, targetId)
                        .SetProperty(x => x.EntitlementId, entitlementId)
                        .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);

                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                    var winner = await db.ActivationCodes.AsNoTracking()
                        .SingleAsync(x => x.Id == activationCode.Id, cancellationToken);
                    if (winner.RevokedAt is not null)
                        throw DomainException.Conflict("activation_code_revoked", "The activation code has been revoked.");
                    if (winner.RedeemedAt is null && winner.ExpiresAt <= timeProvider.GetUtcNow())
                        throw DomainException.Conflict("activation_code_expired", "The activation code has expired.");
                    return await ResolveExistingRedemptionAsync(
                        winner, targetKind, targetId, idempotencyKey, requestHash, cancellationToken);
                }

                var entitlement = new Entitlement
                {
                    Id = entitlementId,
                    TenantId = tenant.TenantId,
                    TargetKind = targetKind,
                    TargetId = targetId,
                    PackageId = activationCode.PackageId,
                    DurationKind = activationCode.DurationKind,
                    StartsAt = startsAt,
                    EndsAt = endsAt,
                    State = EntitlementState.Active,
                    Source = "activation-code",
                    ActivationCodeId = activationCode.Id,
                    CreatedAt = now
                };
                var result = new RedemptionResult(
                    entitlement.Id,
                    package.Code,
                    entitlement.DurationKind,
                    targetKind,
                    targetId,
                    startsAt,
                    endsAt,
                    startsAt > now ? "scheduled" : "active",
                    false);

                db.Entitlements.Add(entitlement);
                db.EntitlementLedger.Add(new EntitlementLedger
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    EntitlementId = entitlement.Id,
                    EventType = startsAt > now ? "scheduled" : "activated",
                    OccurredAt = now,
                    Actor = tenant.Actor,
                    DetailsJson = JsonSerializer.Serialize(new { source = "activation-code", activationCodeId = activationCode.Id }, JsonOptions)
                });
                db.IdempotencyRecords.Add(CreateIdempotencyRecord(
                    RedeemOperation, idempotencyKey, requestHash, result, now));
                audit.Add("activation-code.redeemed", nameof(ActivationCode), activationCode.Id.ToString("D"), details: new
                {
                    entitlementId,
                    targetKind,
                    targetId,
                    package.Code,
                    activationCode.DurationKind,
                    startsAt,
                    endsAt
                });

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                db.ChangeTracker.Clear();
                if (attempt < 7)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)), cancellationToken);
                }
            }
        }

        throw DomainException.Conflict("activation_busy", "Activation is temporarily busy. Retry with the same Idempotency-Key.");
    }

    public async Task<RedemptionResult> ActivateForTargetAsync(
        string packageCode,
        ServiceDurationKind duration,
        ServiceTargetKind targetKind,
        Guid targetId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        idempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        packageCode = packageCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw DomainException.Validation(
                "invalid_idempotency_key",
                "Idempotency-Key is required and must be at most 128 characters.");
        }
        _ = ServiceDurationCalculator.CalculateEnd(timeProvider.GetUtcNow(), duration);
        var requestHash = StableHash.Sha256($"{packageCode}|{duration}|{targetKind}|{targetId:N}");

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var replay = await TryReadIdempotentResultAsync(
                    DirectActivateOperation, idempotencyKey, requestHash, cancellationToken);
                if (replay is not null) return replay with { Replayed = true };

                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                replay = await TryReadIdempotentResultAsync(
                    DirectActivateOperation, idempotencyKey, requestHash, cancellationToken);
                if (replay is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return replay with { Replayed = true };
                }

                var package = await db.ServicePackages.SingleOrDefaultAsync(
                    x => x.Code == packageCode && x.IsEnabled, cancellationToken)
                    ?? throw DomainException.NotFound("Service package");
                await EnsureTargetExistsAsync(targetKind, targetId, cancellationToken);
                var now = timeProvider.GetUtcNow();
                await EnsurePackageTargetEligibleAsync(package, targetKind, targetId, now, cancellationToken);

                var startsAt = await ResolveEntitlementStartAsync(
                    targetKind, targetId, package.Id, now, cancellationToken);
                var endsAt = ServiceDurationCalculator.CalculateEnd(startsAt, duration);
                var entitlement = new Entitlement
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    TargetKind = targetKind,
                    TargetId = targetId,
                    PackageId = package.Id,
                    DurationKind = duration,
                    StartsAt = startsAt,
                    EndsAt = endsAt,
                    State = EntitlementState.Active,
                    Source = "admin-direct",
                    CreatedAt = now
                };
                var result = new RedemptionResult(
                    entitlement.Id,
                    package.Code,
                    duration,
                    targetKind,
                    targetId,
                    startsAt,
                    endsAt,
                    startsAt > now ? "scheduled" : "active",
                    false);

                db.Entitlements.Add(entitlement);
                db.EntitlementLedger.Add(new EntitlementLedger
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    EntitlementId = entitlement.Id,
                    EventType = startsAt > now ? "scheduled" : "activated",
                    OccurredAt = now,
                    Actor = tenant.Actor,
                    DetailsJson = JsonSerializer.Serialize(new { source = "admin-direct" }, JsonOptions)
                });
                db.IdempotencyRecords.Add(CreateIdempotencyRecord(
                    DirectActivateOperation, idempotencyKey, requestHash, result, now));
                audit.Add("entitlement.activated", nameof(Entitlement), entitlement.Id.ToString("D"), details: new
                {
                    targetKind,
                    targetId,
                    package.Code,
                    duration,
                    startsAt,
                    endsAt
                });

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                db.ChangeTracker.Clear();
                if (attempt < 7)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)), cancellationToken);
                }
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is SqliteException { SqliteErrorCode: 19 } && attempt < 7)
            {
                db.ChangeTracker.Clear();
                var winner = await TryReadIdempotentResultAsync(
                    DirectActivateOperation, idempotencyKey, requestHash, cancellationToken);
                if (winner is not null) return winner with { Replayed = true };
                await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)), cancellationToken);
            }
        }

        throw DomainException.Conflict(
            "activation_busy",
            "Service activation is temporarily busy. Retry with the same Idempotency-Key.");
    }

    private Task<bool> HasActiveBasicDependencyAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        DateTimeOffset instant,
        CancellationToken cancellationToken) =>
        db.Entitlements.AsNoTracking()
            .Where(x =>
                x.TargetKind == targetKind &&
                x.TargetId == targetId &&
                x.State == EntitlementState.Active &&
                x.StartsAt <= instant &&
                (x.EndsAt == null || x.EndsAt > instant))
            .Join(
                db.ServicePackages.Where(x => x.Tier == PackageTier.Basic),
                entitlement => entitlement.PackageId,
                package => package.Id,
                (_, _) => true)
            .AnyAsync(cancellationToken);

    private async Task EnsurePackageTargetEligibleAsync(
        ServicePackage package,
        ServiceTargetKind targetKind,
        Guid targetId,
        DateTimeOffset instant,
        CancellationToken cancellationToken)
    {
        if (package.Tier != PackageTier.AdvancedGeneral) return;
        if (targetKind != ServiceTargetKind.Group)
        {
            throw DomainException.Validation(
                "advanced_package_requires_group",
                "Advanced general service packages can only be activated for group targets.");
        }
        if (!await HasActiveBasicDependencyAsync(targetKind, targetId, instant, cancellationToken))
        {
            throw DomainException.Conflict(
                "advanced_package_requires_basic",
                "An active basic service entitlement is required before activating an advanced service package.");
        }
    }

    private async Task<RedemptionResult?> TryReadIdempotentResultAsync(
        string operation,
        string key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Operation == operation && x.Key == key, cancellationToken);
        if (record is null) return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(record.RequestHash),
                Convert.FromHexString(requestHash)))
        {
            throw DomainException.Conflict("idempotency_key_reused", "The Idempotency-Key was already used for a different request.");
        }

        return JsonSerializer.Deserialize<RedemptionResult>(record.ResponseJson, JsonOptions)
               ?? throw new InvalidOperationException("Stored idempotency response is invalid.");
    }

    private async Task<RedemptionResult> ResolveExistingRedemptionAsync(
        ActivationCode code,
        ServiceTargetKind targetKind,
        Guid targetId,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var idempotent = await TryReadIdempotentResultAsync(
            RedeemOperation, idempotencyKey, requestHash, cancellationToken);
        if (idempotent is not null) return idempotent with { Replayed = true };

        if (code.RedeemedTargetKind != targetKind || code.RedeemedTargetId != targetId || code.EntitlementId is null)
        {
            throw DomainException.Conflict("activation_code_redeemed", "The activation code has already been redeemed for another target.");
        }

        var entitlement = await db.Entitlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == code.EntitlementId.Value, cancellationToken)
            ?? throw DomainException.Conflict("redemption_inconsistent", "The activation ledger is inconsistent and requires administrator review.");
        var package = await db.ServicePackages.SingleAsync(x => x.Id == entitlement.PackageId, cancellationToken);
        var result = new RedemptionResult(
            entitlement.Id,
            package.Code,
            entitlement.DurationKind,
            entitlement.TargetKind,
            entitlement.TargetId,
            entitlement.StartsAt,
            entitlement.EndsAt,
            EntitlementEvaluator.EffectiveStatus(entitlement, timeProvider.GetUtcNow()),
            true);
        var now = timeProvider.GetUtcNow();
        db.IdempotencyRecords.Add(CreateIdempotencyRecord(
            RedeemOperation, idempotencyKey, requestHash, result, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await TryReadIdempotentResultAsync(
                RedeemOperation, idempotencyKey, requestHash, cancellationToken);
            if (winner is null) throw;
            return winner with { Replayed = true };
        }
        return result;
    }

    private IdempotencyRecord CreateIdempotencyRecord(
        string operation,
        string key,
        string requestHash,
        RedemptionResult response,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            Operation = operation,
            Key = key,
            RequestHash = requestHash,
            StatusCode = StatusCodes.Status200OK,
            ResponseJson = JsonSerializer.Serialize(response, JsonOptions),
            CreatedAt = now,
            ExpiresAt = now.AddDays(7)
        };

    private async Task EnsureTargetExistsAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var exists = targetKind switch
        {
            ServiceTargetKind.Contact => await db.Contacts.AnyAsync(x => x.Id == targetId, cancellationToken),
            ServiceTargetKind.Group => await db.Groups.AnyAsync(x => x.Id == targetId, cancellationToken),
            _ => false
        };
        if (!exists) throw DomainException.NotFound("Activation target");
    }

    private async Task<DateTimeOffset> ResolveEntitlementStartAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        Guid packageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // BASIC and ADVANCED_GENERAL are independent tracks; one never truncates or shifts the other.
        var existing = await db.Entitlements.AsNoTracking()
            .Where(x =>
                x.TargetKind == targetKind &&
                x.TargetId == targetId &&
                x.PackageId == packageId &&
                x.State != EntitlementState.Revoked &&
                (x.EndsAt == null || x.EndsAt > now))
            .ToListAsync(cancellationToken);
        if (existing.Any(x => x.EndsAt is null))
        {
            throw DomainException.Conflict(
                "permanent_entitlement_exists",
                "A non-revoked permanent entitlement already exists for this target and package; the activation code was not consumed.");
        }

        var latestEnd = existing
            .Where(x => x.EndsAt.HasValue)
            .Select(x => x.EndsAt!.Value)
            .DefaultIfEmpty(now)
            .Max();
        return latestEnd > now ? latestEnd : now;
    }

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')
            .ToUpperInvariant();
        return $"WXB-{token[..8]}-{token[8..16]}-{token[16..24]}-{token[24..32]}";
    }
}
