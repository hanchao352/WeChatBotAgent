using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WeChatBot.Backend.Contracts;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;
using WeChatBot.Backend.Services;

namespace WeChatBot.Backend.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/backups")]
public sealed class BackupsController(AppDbContext db, LogicalBackupService backups) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BackupManifest>>> List(
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500) throw DomainException.Validation("invalid_page_size", "take must be between 1 and 500.");
        return Ok(await db.BackupManifests.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<BackupManifest>> Create(CreateBackupRequest request, CancellationToken cancellationToken)
    {
        var manifest = await backups.CreateAsync(request.Reason?.Trim() ?? "manual", cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = manifest.Id }, manifest);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BackupManifest>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.BackupManifests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw DomainException.NotFound("Backup manifest");

    [HttpPost("{id:guid}/verify")]
    public async Task<ActionResult<BackupVerification>> Verify(Guid id, CancellationToken cancellationToken) =>
        Ok(await backups.VerifyAsync(id, cancellationToken));

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<RestoreResult>> Restore(
        Guid id,
        RestoreBackupRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        Ok(await backups.RestoreAsync(id, request.Confirmation, idempotencyKey ?? string.Empty, cancellationToken));
}

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/audit-logs")]
public sealed class AuditLogsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AuditLog>>> List(
        [FromQuery] string? action,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500) throw DomainException.Validation("invalid_page_size", "take must be between 1 and 500.");
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);
        return Ok(await query.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(cancellationToken));
    }
}

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/system-state")]
public sealed class SystemStateController(
    AppDbContext db,
    TimeProvider timeProvider,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TenantState>> Get(CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);

    [HttpPut("automation")]
    public async Task<ActionResult<TenantState>> SetAutomationState(AutomationStateRequest request, CancellationToken cancellationToken)
    {
        var state = await db.Tenants.SingleAsync(cancellationToken);
        if (state.Version != request.ExpectedVersion)
            throw DomainException.Conflict("concurrency_conflict", "The system state changed after it was read.");
        state.AutomationPaused = request.Paused;
        state.UpdatedAt = timeProvider.GetUtcNow();
        state.Version++;
        audit.Add(request.Paused ? "automation.paused" : "automation.resumed", nameof(TenantState), state.TenantId.ToString("D"), details: new
        {
            request.Reason,
            state.Version
        });
        await db.SaveChangesAsync(cancellationToken);
        return state;
    }
}
