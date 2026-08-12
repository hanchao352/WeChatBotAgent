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
[Route("api/contacts")]
public sealed class ContactsController(
    AppDbContext db,
    TenantContext tenant,
    TimeProvider timeProvider,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Contact>>> List([FromQuery] int take = 100, CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500) throw DomainException.Validation("invalid_page_size", "take must be between 1 and 500.");
        return await db.Contacts.AsNoTracking().OrderBy(x => x.DisplayName).Take(take).ToListAsync(cancellationToken);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Contact>> Get(Guid id, CancellationToken cancellationToken)
    {
        return await db.Contacts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
               ?? throw DomainException.NotFound("Contact");
    }

    [HttpPost]
    public async Task<ActionResult<Contact>> Create(ContactCreateRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var entity = new Contact
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            ExternalId = request.ExternalId.Trim(),
            DisplayName = request.DisplayName.Trim(),
            WeChatId = TrimToNull(request.WeChatId),
            CustomerCode = TrimToNull(request.CustomerCode),
            CurrentWeChatRemark = TrimToNull(request.CurrentWeChatRemark),
            ManualRemarkProtected = request.ManualRemarkProtected,
            ServiceExpiresAt = request.ServiceExpiresAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Contacts.Add(entity);
        audit.Add("contact.created", nameof(Contact), entity.Id.ToString("D"), details: new { entity.ExternalId });
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<Contact>> Update(Guid id, ContactUpdateRequest request, CancellationToken cancellationToken)
    {
        var entity = await db.Contacts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                     ?? throw DomainException.NotFound("Contact");
        if (entity.Version != request.ExpectedVersion)
            throw DomainException.Conflict("concurrency_conflict", "The contact changed after it was read.");

        entity.ExternalId = request.ExternalId.Trim();
        entity.DisplayName = request.DisplayName.Trim();
        entity.WeChatId = TrimToNull(request.WeChatId);
        entity.CustomerCode = TrimToNull(request.CustomerCode);
        entity.CurrentWeChatRemark = TrimToNull(request.CurrentWeChatRemark);
        entity.ManualRemarkProtected = request.ManualRemarkProtected;
        entity.ServiceExpiresAt = request.ServiceExpiresAt;
        entity.UpdatedAt = timeProvider.GetUtcNow();
        entity.Version++;
        audit.Add("contact.updated", nameof(Contact), entity.Id.ToString("D"), details: new { entity.ExternalId, entity.Version });
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
