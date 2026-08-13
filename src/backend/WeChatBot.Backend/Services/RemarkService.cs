using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WeChatBot.Backend.Data;
using WeChatBot.Backend.Domain;
using WeChatBot.Backend.Infrastructure;

namespace WeChatBot.Backend.Services;

public sealed record RemarkPreview(
    ServiceTargetKind TargetKind,
    Guid TargetId,
    Guid RuleId,
    string TargetExternalId,
    string ExpectedTargetDisplayName,
    string GeneratedRemark,
    bool HasConflict,
    string? ConflictReason,
    string? CurrentSystemRemark,
    string? CurrentWeChatRemark);

/// <summary>
/// 提供备注模板预览以及备注任务完成前的业务校验和目标状态更新。
/// </summary>
public sealed class RemarkService(
    AppDbContext db,
    EntitlementService entitlements)
{
    /// <summary>查找目标当前授予自动备注功能的有效权益。</summary>
    public Task<Entitlement?> FindAutoRemarkEntitlementAsync(
        ServiceTargetKind targetKind,
        Guid targetId,
        CancellationToken cancellationToken) =>
        entitlements.FindActiveWithFeatureAsync(
            targetKind,
            targetId,
            WellKnownFeatures.AutoRemark,
            cancellationToken: cancellationToken);

    /// <summary>匹配备注模板中受支持的占位符名称。</summary>
    private static readonly Regex PlaceholderRegex = new(
        "\\{(?<name>[a-zA-Z][a-zA-Z0-9]*)\\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>联系人备注模板允许读取的字段集合。</summary>
    private static readonly IReadOnlySet<string> ContactFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "externalId", "displayName", "wechatId", "customerCode", "serviceExpiresAt"
    };

    /// <summary>群备注模板允许读取的字段集合。</summary>
    private static readonly IReadOnlySet<string> GroupFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "externalId", "displayName", "businessName", "serviceExpiresAt"
    };

    public void ValidateTemplate(ServiceTargetKind targetKind, string template, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw DomainException.Validation("empty_template", "Remark template cannot be empty.");
        if (maxLength is < 1 or > 256)
            throw DomainException.Validation("invalid_max_length", "Remark maximum length must be between 1 and 256.");

        var fields = targetKind switch
        {
            ServiceTargetKind.Contact => ContactFields,
            ServiceTargetKind.Group => GroupFields,
            _ => throw DomainException.Validation("invalid_target_kind", "Remark target kind is not supported.")
        };
        var matches = PlaceholderRegex.Matches(template);
        foreach (Match match in matches)
        {
            var name = match.Groups["name"].Value;
            if (!fields.Contains(name))
                throw DomainException.Validation("unknown_template_field", $"Template field '{name}' is not available for {targetKind} remarks.");
        }

        var withoutKnownFields = PlaceholderRegex.Replace(template, string.Empty);
        if (withoutKnownFields.Contains('{') || withoutKnownFields.Contains('}'))
            throw DomainException.Validation("malformed_template", "Remark template contains malformed braces.");
    }

    public async Task<RemarkPreview> PreviewAsync(Guid ruleId, Guid targetId, CancellationToken cancellationToken)
    {
        var rule = await db.RemarkRules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ruleId, cancellationToken)
                   ?? throw DomainException.NotFound("Remark rule");
        if (!rule.IsEnabled)
            throw DomainException.Conflict("remark_rule_disabled", "The remark rule is disabled.");

        ValidateTemplate(rule.TargetKind, rule.Template, rule.MaxLength);
        return rule.TargetKind switch
        {
            ServiceTargetKind.Contact => await PreviewContactAsync(rule, targetId, cancellationToken),
            ServiceTargetKind.Group => await PreviewGroupAsync(rule, targetId, cancellationToken),
            _ => throw DomainException.Validation("invalid_target_kind", "Remark target kind is not supported.")
        };
    }

    /// <summary>
    /// 校验任务仍满足自动化、权益和目标快照约束，并将成功结果应用到服务端备注镜像。
    /// 调用方必须在包含任务终态写入的同一数据库事务内保存更改。
    /// </summary>
    /// <param name="task">当前租户内待完成的备注任务。</param>
    /// <param name="appliedRemark">Agent 确认已应用的实际备注。</param>
    /// <param name="completedAt">服务端确认完成的 UTC 时刻。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    public async Task ApplySuccessfulTaskAsync(
        RemarkTask task,
        string? appliedRemark,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(appliedRemark, task.GeneratedRemark, StringComparison.Ordinal))
        {
            throw DomainException.Validation(
                "applied_remark_mismatch",
                "AppliedRemark must exactly match the generated remark.");
        }

        // 成功回报涉及真实外部副作用，暂停开关必须在落库前再次校验，不能只依赖认领时状态。
        var automationPaused = await db.Tenants.AsNoTracking()
            .Select(x => x.AutomationPaused)
            .SingleAsync(cancellationToken);
        if (automationPaused)
        {
            throw DomainException.Conflict(
                "automation_paused",
                "Automation is paused; successful remark completion cannot be accepted.");
        }

        // 权益可能在租约持有期间过期、暂停或撤销，因此完成时必须重新求值。
        var entitlement = await entitlements.FindActiveWithFeatureAsync(
            task.TargetKind,
            task.TargetId,
            WellKnownFeatures.AutoRemark,
            cancellationToken: cancellationToken);
        if (entitlement is null)
        {
            throw DomainException.Conflict(
                "auto_remark_feature_required",
                "The target has no active entitlement granting auto-remark.");
        }

        if (task.TargetKind == ServiceTargetKind.Contact)
        {
            var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Id == task.TargetId, cancellationToken)
                          ?? throw DomainException.NotFound("Contact");
            if (!string.Equals(contact.ExternalId, task.TargetExternalId, StringComparison.Ordinal) ||
                !string.Equals(contact.DisplayName, task.ExpectedTargetDisplayName, StringComparison.Ordinal))
            {
                throw DomainException.Conflict(
                    "remark_target_identity_changed",
                    "The target identity changed after this task was created; create a new task from the current target.");
            }
            if (contact.ManualRemarkProtected)
            {
                throw DomainException.Conflict(
                    "remark_now_protected",
                    "Manual remark protection was enabled after the task was created.");
            }

            EnsureRemarkSnapshotUnchanged(task, contact.SystemRemark, contact.CurrentWeChatRemark);
            contact.SystemRemark = task.GeneratedRemark;
            contact.CurrentWeChatRemark = task.GeneratedRemark;
            contact.UpdatedAt = completedAt;
            contact.Version++;
            return;
        }

        if (task.TargetKind == ServiceTargetKind.Group)
        {
            var group = await db.Groups.SingleOrDefaultAsync(x => x.Id == task.TargetId, cancellationToken)
                        ?? throw DomainException.NotFound("Group");
            if (!string.Equals(group.ExternalId, task.TargetExternalId, StringComparison.Ordinal) ||
                !string.Equals(group.DisplayName, task.ExpectedTargetDisplayName, StringComparison.Ordinal))
            {
                throw DomainException.Conflict(
                    "remark_target_identity_changed",
                    "The target identity changed after this task was created; create a new task from the current target.");
            }
            if (group.ManualRemarkProtected)
            {
                throw DomainException.Conflict(
                    "remark_now_protected",
                    "Manual remark protection was enabled after the task was created.");
            }

            EnsureRemarkSnapshotUnchanged(task, group.SystemRemark, group.CurrentWeChatRemark);
            group.SystemRemark = task.GeneratedRemark;
            group.CurrentWeChatRemark = task.GeneratedRemark;
            group.UpdatedAt = completedAt;
            group.Version++;
            return;
        }

        throw DomainException.Validation("invalid_target_kind", "Remark target kind is not supported.");
    }

    /// <summary>
    /// 确保目标的系统备注和微信备注仍与创建任务时的快照完全一致，防止覆盖租约期间的人工修改。
    /// </summary>
    /// <param name="task">保存原始快照的备注任务。</param>
    /// <param name="currentSystemRemark">目标当前系统备注。</param>
    /// <param name="currentWeChatRemark">目标当前微信备注。</param>
    private static void EnsureRemarkSnapshotUnchanged(
        RemarkTask task,
        string? currentSystemRemark,
        string? currentWeChatRemark)
    {
        var systemRemarkChanged = !string.Equals(
            task.OriginalSystemRemark,
            currentSystemRemark,
            StringComparison.Ordinal);
        var weChatRemarkChanged = !string.Equals(
            task.OriginalWeChatRemark,
            currentWeChatRemark,
            StringComparison.Ordinal);
        if (!systemRemarkChanged && !weChatRemarkChanged) return;

        // 调用方负责记录拒绝审计；此处只判定快照，确保同一规则可供管理员和 Agent 完成路径复用。
        throw DomainException.Conflict(
            "remark_target_changed",
            "The target remarks changed after this task was created; create a new task from the current state.");
    }

    private async Task<RemarkPreview> PreviewContactAsync(RemarkRule rule, Guid targetId, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetId, cancellationToken)
                      ?? throw DomainException.NotFound("Contact");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["externalId"] = contact.ExternalId,
            ["displayName"] = contact.DisplayName,
            ["wechatId"] = contact.WeChatId,
            ["customerCode"] = contact.CustomerCode,
            ["serviceExpiresAt"] = contact.ServiceExpiresAt?.ToString("yyyy-MM-dd")
        };
        return BuildPreview(
            rule,
            targetId,
            contact.ExternalId,
            contact.DisplayName,
            values,
            contact.SystemRemark,
            contact.CurrentWeChatRemark,
            contact.ManualRemarkProtected);
    }

    private async Task<RemarkPreview> PreviewGroupAsync(RemarkRule rule, Guid targetId, CancellationToken cancellationToken)
    {
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetId, cancellationToken)
                    ?? throw DomainException.NotFound("Group");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["externalId"] = group.ExternalId,
            ["displayName"] = group.DisplayName,
            ["businessName"] = group.BusinessName,
            ["serviceExpiresAt"] = group.ServiceExpiresAt?.ToString("yyyy-MM-dd")
        };
        return BuildPreview(
            rule,
            targetId,
            group.ExternalId,
            group.DisplayName,
            values,
            group.SystemRemark,
            group.CurrentWeChatRemark,
            group.ManualRemarkProtected);
    }

    private static RemarkPreview BuildPreview(
        RemarkRule rule,
        Guid targetId,
        string targetExternalId,
        string expectedTargetDisplayName,
        IReadOnlyDictionary<string, string?> values,
        string? systemRemark,
        string? weChatRemark,
        bool manualProtected)
    {
        var generated = PlaceholderRegex.Replace(rule.Template, match =>
        {
            var key = match.Groups["name"].Value;
            return values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        });
        generated = Sanitize(generated);
        if (generated.Length == 0)
            throw DomainException.Validation("empty_rendered_remark", "The template rendered an empty remark.");
        if (generated.Length > rule.MaxLength)
            throw DomainException.Validation("remark_too_long", $"The rendered remark is {generated.Length} characters; the configured maximum is {rule.MaxLength}.");

        string? conflict = null;
        if (manualProtected)
        {
            conflict = "The target has manual remark protection enabled.";
        }
        else if (!string.IsNullOrWhiteSpace(weChatRemark) &&
                 !string.Equals(weChatRemark, generated, StringComparison.Ordinal) &&
                 (rule.ConflictPolicy == RemarkConflictPolicy.Skip ||
                  !string.Equals(weChatRemark, systemRemark, StringComparison.Ordinal)))
        {
            conflict = "The current WeChat remark does not match the last system-generated remark.";
        }

        return new RemarkPreview(
            rule.TargetKind,
            targetId,
            rule.Id,
            targetExternalId,
            expectedTargetDisplayName,
            generated,
            conflict is not null,
            conflict,
            systemRemark,
            weChatRemark);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value.Trim())
        {
            var normalized = char.IsControl(character) ? ' ' : character;
            if (char.IsWhiteSpace(normalized))
            {
                if (previousWasSpace) continue;
                normalized = ' ';
                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }
            builder.Append(normalized);
        }
        return builder.ToString().Trim();
    }
}
