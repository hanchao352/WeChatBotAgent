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
    string GeneratedRemark,
    bool HasConflict,
    string? ConflictReason,
    string? CurrentSystemRemark,
    string? CurrentWeChatRemark);

public sealed class RemarkService(AppDbContext db)
{
    private static readonly Regex PlaceholderRegex = new(
        "\\{(?<name>[a-zA-Z][a-zA-Z0-9]*)\\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlySet<string> ContactFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "externalId", "displayName", "wechatId", "customerCode", "serviceExpiresAt"
    };

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
        return BuildPreview(rule, targetId, values, contact.SystemRemark, contact.CurrentWeChatRemark, contact.ManualRemarkProtected);
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
        return BuildPreview(rule, targetId, values, group.SystemRemark, group.CurrentWeChatRemark, group.ManualRemarkProtected);
    }

    private static RemarkPreview BuildPreview(
        RemarkRule rule,
        Guid targetId,
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
