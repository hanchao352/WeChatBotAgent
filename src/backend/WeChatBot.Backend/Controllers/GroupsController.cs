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
[Route("api/groups")]
public sealed class GroupsController(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupChat>>> List([FromQuery] int take = 100, CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500) throw DomainException.Validation("invalid_page_size", "take must be between 1 and 500.");
        return await db.Groups.AsNoTracking().OrderBy(x => x.DisplayName).Take(take).ToListAsync(cancellationToken);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GroupChat>> Get(Guid id, CancellationToken cancellationToken)
    {
        return await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
               ?? throw DomainException.NotFound("Group");
    }

    [HttpPost]
    public async Task<ActionResult<GroupChat>> Create(GroupCreateRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var entity = new GroupChat
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            ExternalId = request.ExternalId.Trim(),
            DisplayName = request.DisplayName.Trim(),
            BusinessName = TrimToNull(request.BusinessName),
            CurrentWeChatRemark = TrimToNull(request.CurrentWeChatRemark),
            ManualRemarkProtected = request.ManualRemarkProtected,
            ServiceExpiresAt = request.ServiceExpiresAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Groups.Add(entity);
        audit.Add("group.created", nameof(GroupChat), entity.Id.ToString("D"), details: new { entity.ExternalId });
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<GroupChat>> Update(Guid id, GroupUpdateRequest request, CancellationToken cancellationToken)
    {
        var entity = await db.Groups.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                     ?? throw DomainException.NotFound("Group");
        if (entity.Version != request.ExpectedVersion)
            throw DomainException.Conflict("concurrency_conflict", "The group changed after it was read.");

        entity.ExternalId = request.ExternalId.Trim();
        entity.DisplayName = request.DisplayName.Trim();
        entity.BusinessName = TrimToNull(request.BusinessName);
        entity.CurrentWeChatRemark = TrimToNull(request.CurrentWeChatRemark);
        entity.ManualRemarkProtected = request.ManualRemarkProtected;
        entity.ServiceExpiresAt = request.ServiceExpiresAt;
        entity.UpdatedAt = timeProvider.GetUtcNow();
        entity.Version++;
        audit.Add("group.updated", nameof(GroupChat), entity.Id.ToString("D"), details: new { entity.ExternalId, entity.Version });
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
