using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

public sealed record ActivationCodeResponse(
    Guid Id,
    string Code,
    string PackageCode,
    ServiceDurationKind Duration,
    DateTimeOffset ExpiresAt,
    string Notice);

public sealed record ActivationCodeSummary(
    Guid Id,
    string PackageCode,
    ServiceDurationKind Duration,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Status,
    DateTimeOffset? RedeemedAt,
    ServiceTargetKind? RedeemedTargetKind,
    Guid? RedeemedTargetId,
    Guid? EntitlementId,
    DateTimeOffset? RevokedAt,
    string? RevocationReason,
    long Version);

public sealed record ServicePackageResponse(
    Guid Id,
    string Code,
    string Name,
    PackageTier Tier,
    IReadOnlyList<string> Features,
    int Version);

public sealed record EntitlementResponse(
    Guid Id,
    ServiceTargetKind TargetKind,
    Guid TargetId,
    string PackageCode,
    PackageTier PackageTier,
    ServiceDurationKind Duration,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    EntitlementState State,
    string EffectiveStatus,
    string Source,
    long Version);

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/service-packages")]
public sealed class ServicePackagesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ServicePackageResponse>>> List(CancellationToken cancellationToken)
    {
        var packages = await db.ServicePackages.AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Tier)
            .ToListAsync(cancellationToken);
        return Ok(packages.Select(x => new ServicePackageResponse(
            x.Id,
            x.Code,
            x.Name,
            x.Tier,
            JsonSerializer.Deserialize<string[]>(x.FeaturesJson) ?? [],
            x.Version)).ToList());
    }
}

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/activation-codes")]
public sealed class ActivationCodesController(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    ActivationService activationService,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActivationCodeSummary>>> List(
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500) throw DomainException.Validation("invalid_page_size", "take must be between 1 and 500.");
        var codes = await db.ActivationCodes.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
        var packageIds = codes.Select(x => x.PackageId).Distinct().ToArray();
        var packages = await db.ServicePackages.AsNoTracking()
            .Where(x => packageIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Ok(codes.Select(x => ToSummary(x, packages[x.PackageId], now)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<ActivationCodeResponse>> Issue(IssueActivationCodeRequest request, CancellationToken cancellationToken)
    {
        var (entity, plaintext) = await activationService.IssueAsync(
            request.PackageCode.Trim().ToUpperInvariant(), request.Duration, request.ExpiresAt, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Created(string.Empty, new ActivationCodeResponse(
            entity.Id,
            plaintext,
            request.PackageCode.Trim().ToUpperInvariant(),
            entity.DurationKind,
            entity.ExpiresAt,
            "This code is returned once and cannot be recovered from storage."));
    }

    [HttpPost("redeem")]
    public async Task<ActionResult<RedemptionResult>> Redeem(
        RedeemActivationCodeRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await activationService.RedeemAsync(
            request.Code,
            request.TargetKind,
            request.TargetId,
            idempotencyKey ?? string.Empty,
            cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<ActivationCodeSummary>> Revoke(
        Guid id,
        RevokeActivationCodeRequest request,
        CancellationToken cancellationToken)
    {
        var entity = await db.ActivationCodes.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                     ?? throw DomainException.NotFound("Activation code");
        if (entity.Version != request.ExpectedVersion)
            throw DomainException.Conflict("concurrency_conflict", "The activation code changed after it was read.");
        if (entity.RedeemedAt is not null)
            throw DomainException.Conflict("activation_code_redeemed", "A redeemed activation code cannot be revoked.");
        if (entity.RevokedAt is null)
        {
            entity.RevokedAt = timeProvider.GetUtcNow();
            entity.RevokedBy = tenant.Actor;
            entity.RevocationReason = request.Reason.Trim();
            entity.Version++;
            audit.Add("activation-code.revoked", nameof(ActivationCode), entity.Id.ToString("D"), details: new
            {
                entity.PackageId,
                entity.RevocationReason,
                entity.Version
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        var package = await db.ServicePackages.AsNoTracking().SingleAsync(x => x.Id == entity.PackageId, cancellationToken);
        return ToSummary(entity, package, timeProvider.GetUtcNow());
    }

    private static ActivationCodeSummary ToSummary(ActivationCode entity, ServicePackage package, DateTimeOffset now) => new(
        entity.Id,
        package.Code,
        entity.DurationKind,
        entity.CreatedAt,
        entity.ExpiresAt,
        entity.RedeemedAt is not null ? "redeemed" : entity.RevokedAt is not null ? "revoked" : entity.ExpiresAt <= now ? "expired" : "available",
        entity.RedeemedAt,
        entity.RedeemedTargetKind,
        entity.RedeemedTargetId,
        entity.EntitlementId,
        entity.RevokedAt,
        entity.RevocationReason,
        entity.Version);
}

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/entitlements")]
public sealed class EntitlementsController(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    ActivationService activationService,
    AuditService audit) : ControllerBase
{
    [HttpPost("activate")]
    public async Task<ActionResult<RedemptionResult>> Activate(
        ActivateServiceRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        Ok(await activationService.ActivateForTargetAsync(
            request.PackageCode,
            request.Duration,
            request.TargetKind,
            request.TargetId,
            idempotencyKey ?? string.Empty,
            cancellationToken));

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EntitlementResponse>>> List(
        [FromQuery] ServiceTargetKind? targetKind,
        [FromQuery] Guid? targetId,
        [FromQuery] bool activeOnly = false,
        CancellationToken cancellationToken = default)
    {
        var query = db.Entitlements.AsNoTracking();
        if (targetKind is not null) query = query.Where(x => x.TargetKind == targetKind);
        if (targetId is not null) query = query.Where(x => x.TargetId == targetId);
        var now = timeProvider.GetUtcNow();
        if (activeOnly)
        {
            query = query.Where(x =>
                x.State == EntitlementState.Active &&
                x.StartsAt <= now &&
                (x.EndsAt == null || x.EndsAt > now));
        }
        var entities = await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(cancellationToken);
        var packageIds = entities.Select(x => x.PackageId).Distinct().ToArray();
        var packages = await db.ServicePackages.AsNoTracking()
            .Where(x => packageIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var results = entities.Select(x => ToResponse(x, packages[x.PackageId], now));
        return Ok(results.ToList());
    }

    [HttpGet("{id:guid}/ledger")]
    public async Task<ActionResult<IReadOnlyList<EntitlementLedger>>> Ledger(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.Entitlements.AsNoTracking().AnyAsync(x => x.Id == id, cancellationToken))
            throw DomainException.NotFound("Entitlement");
        return Ok(await db.EntitlementLedger.AsNoTracking()
            .Where(x => x.EntitlementId == id)
            .OrderBy(x => x.OccurredAt)
            .ToListAsync(cancellationToken));
    }

    [HttpPatch("{id:guid}/state")]
    public async Task<ActionResult<EntitlementResponse>> ChangeState(
        Guid id,
        EntitlementStateRequest request,
        CancellationToken cancellationToken)
    {
        var entity = await db.Entitlements.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                     ?? throw DomainException.NotFound("Entitlement");
        if (entity.Version != request.ExpectedVersion)
            throw DomainException.Conflict("concurrency_conflict", "The entitlement changed after it was read.");
        if (!Enum.IsDefined(request.State))
            throw DomainException.Validation("invalid_entitlement_state", "State must be a supported entitlement state.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 3)
            throw DomainException.Validation("state_reason_required", "A reason of at least three characters is required.");
        if (entity.State == EntitlementState.Revoked && request.State != EntitlementState.Revoked)
            throw DomainException.Conflict("revoked_entitlement_immutable", "A revoked entitlement cannot be reactivated.");
        if (entity.State == request.State)
        {
            var unchangedPackage = await db.ServicePackages.SingleAsync(x => x.Id == entity.PackageId, cancellationToken);
            return ToResponse(entity, unchangedPackage, timeProvider.GetUtcNow());
        }

        var now = timeProvider.GetUtcNow();
        entity.State = request.State;
        entity.SuspendedAt = request.State == EntitlementState.Suspended ? now : null;
        entity.RevokedAt = request.State == EntitlementState.Revoked ? now : entity.RevokedAt;
        entity.Version++;
        db.EntitlementLedger.Add(new EntitlementLedger
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            EntitlementId = entity.Id,
            EventType = request.State.ToString().ToLowerInvariant(),
            OccurredAt = now,
            Actor = tenant.Actor,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { request.Reason })
        });
        audit.Add("entitlement.state-changed", nameof(Entitlement), entity.Id.ToString("D"), details: new
        {
            state = request.State,
            request.Reason,
            entity.Version
        });
        await db.SaveChangesAsync(cancellationToken);
        var package = await db.ServicePackages.AsNoTracking().SingleAsync(x => x.Id == entity.PackageId, cancellationToken);
        return ToResponse(entity, package, now);
    }

    private static EntitlementResponse ToResponse(Entitlement entity, ServicePackage package, DateTimeOffset now) => new(
        entity.Id,
        entity.TargetKind,
        entity.TargetId,
        package.Code,
        package.Tier,
        entity.DurationKind,
        entity.StartsAt,
        entity.EndsAt,
        entity.State,
        EntitlementEvaluator.EffectiveStatus(entity, now),
        entity.Source,
        entity.Version);
}
