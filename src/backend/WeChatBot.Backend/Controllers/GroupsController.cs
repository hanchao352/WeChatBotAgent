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
    CursorProtector cursors,
    TimeProvider timeProvider,
    AuditService audit) : ControllerBase
{
    /// <summary>群列表游标的稳定范围标识；修改排序语义时必须同步升级该值。</summary>
    private const string CursorScope = "groups:display-name-id:v1";

    /// <summary>
    /// 按显示名称和唯一 ID 升序列出群。仅使用旧版 <paramref name="take"/> 时返回数组；
    /// 使用 <paramref name="pageSize"/> 或 <paramref name="cursor"/> 时返回游标分页对象。
    /// </summary>
    /// <param name="take">旧版数组响应的最大数量，保留用于现有调用方兼容。</param>
    /// <param name="pageSize">游标模式页容量，范围为 1 到 500。</param>
    /// <param name="cursor">上一页返回的不透明下一页游标。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>旧版群数组或新版 <see cref="CursorPage{T}"/>。</returns>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<GroupChat>>(StatusCodes.Status200OK)]
    [ProducesResponseType<CursorPage<GroupChat>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int take = CursorPaginationLimits.DefaultPageSize,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        // 以查询键是否出现区分协议模式，使显式空游标也能进入严格校验而不会静默回退。
        var cursorModeRequested = Request.Query.ContainsKey(nameof(pageSize)) ||
                                  Request.Query.ContainsKey(nameof(cursor));

        // 没有游标参数时保持原数组契约，避免现有管理端和集成方被响应包装破坏。
        if (!cursorModeRequested)
        {
            var legacyTake = CursorPaginationLimits.ValidateLegacyTake(take);
            var legacyItems = await OrderedGroups().Take(legacyTake).ToListAsync(cancellationToken);
            return Ok(legacyItems);
        }

        // 新模式禁止同时改变旧 take，避免两个容量参数含义冲突并产生不可预测结果。
        if (Request.Query.ContainsKey(nameof(take)))
        {
            throw DomainException.Validation(
                "conflicting_page_size",
                "take cannot be combined with pageSize or cursor.");
        }

        var resolvedPageSize = CursorPaginationLimits.Resolve(pageSize);
        IQueryable<GroupChat> query = OrderedGroups();
        if (Request.Query.ContainsKey(nameof(cursor)))
        {
            var position = cursors.Unprotect(cursor ?? string.Empty, CursorScope, tenant.TenantId);

            // 键集条件严格位于末项之后；唯一 ID 决胜键保证同名群不会重复或遗漏。
            query = query.Where(x =>
                string.Compare(x.DisplayName, position.SortKey) > 0 ||
                (x.DisplayName == position.SortKey && x.Id.CompareTo(position.Id) > 0));
        }

        // 多读取一条仅用于判断是否还有下一页，响应中不会暴露该探测记录。
        var candidates = await query.Take(resolvedPageSize + 1).ToListAsync(cancellationToken);
        var hasMore = candidates.Count > resolvedPageSize;
        if (hasMore) candidates.RemoveAt(resolvedPageSize);
        var nextCursor = hasMore
            ? cursors.Protect(
                CursorScope,
                tenant.TenantId,
                new CursorPosition(candidates[^1].DisplayName, candidates[^1].Id))
            : null;
        return Ok(new CursorPage<GroupChat>(candidates, nextCursor, hasMore));
    }

    /// <summary>
    /// 创建群稳定排序查询。唯一 ID 是显示名称相同时的决胜键，并与游标载荷保持一致。
    /// </summary>
    /// <returns>尚未执行、已启用租户过滤且无跟踪的有序查询。</returns>
    private IOrderedQueryable<GroupChat> OrderedGroups() =>
        db.Groups.AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Id);

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
