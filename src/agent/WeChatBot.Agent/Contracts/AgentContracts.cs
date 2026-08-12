using System.Collections.ObjectModel;

namespace WeChatBot.Agent.Contracts;

public enum AgentCommandKind
{
    ObserveMentions,
    UpdateRemark
}

public enum RemarkTargetKind
{
    Contact,
    Group
}

public sealed record CommandMetadata(
    string CommandId,
    string IdempotencyKey,
    string WeChatInstanceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    TimeSpan Timeout,
    string TraceId,
    int ContractVersion = 1);

public interface IAgentCommand
{
    AgentCommandKind Kind { get; }

    string CapabilityCode { get; }

    CommandMetadata Metadata { get; }
}

public sealed record ObserveMentionsCommand(
    CommandMetadata Metadata,
    string GroupStableId,
    string ExpectedGroupDisplayName,
    string BotDisplayName,
    DateTimeOffset? CapturedAfter) : IAgentCommand
{
    public AgentCommandKind Kind => AgentCommandKind.ObserveMentions;

    public string CapabilityCode => "mention.observe";
}

public sealed record UpdateRemarkCommand(
    CommandMetadata Metadata,
    RemarkTargetKind TargetKind,
    string TargetStableId,
    string ExpectedDisplayName,
    string? ExpectedCurrentRemark,
    string DesiredRemark) : IAgentCommand
{
    public AgentCommandKind Kind => AgentCommandKind.UpdateRemark;

    public string CapabilityCode => TargetKind == RemarkTargetKind.Contact
        ? "remark.contact.preview"
        : "remark.group.preview";
}

public sealed record MentionObservation(
    string EventId,
    string GroupStableId,
    string? SenderStableId,
    string Text,
    DateTimeOffset? ClientDisplayedAt,
    DateTimeOffset CapturedAt,
    bool IsAtAll,
    bool IsFromBot,
    string ContentFingerprint);

public enum CommandResultStatus
{
    Succeeded,
    DryRun,
    Rejected,
    Paused,
    TimedOut,
    Canceled,
    Indeterminate,
    Failed
}

public sealed record CommandExecutionResult(
    string CommandId,
    CommandResultStatus Status,
    string Code,
    string Summary,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool ExternalActionAttempted,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public static CommandExecutionResult Create(
        string commandId,
        CommandResultStatus status,
        string code,
        string summary,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        bool externalActionAttempted = false,
        IDictionary<string, string>? data = null) =>
        new(
            commandId,
            status,
            code,
            summary,
            startedAt,
            completedAt,
            externalActionAttempted,
            data is null
                ? null
                : new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(data, StringComparer.Ordinal)));
}

public static class AgentCommandValidator
{
    public static IReadOnlyList<string> Validate(IAgentCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<string>();
        var metadata = command.Metadata;

        RequireIdentifier(metadata.CommandId, nameof(metadata.CommandId), 128, errors);
        RequireIdentifier(metadata.IdempotencyKey, nameof(metadata.IdempotencyKey), 256, errors);
        RequireIdentifier(metadata.WeChatInstanceId, nameof(metadata.WeChatInstanceId), 128, errors);
        RequireIdentifier(metadata.TraceId, nameof(metadata.TraceId), 128, errors);

        if (metadata.ContractVersion != 1)
        {
            errors.Add($"Unsupported contract version: {metadata.ContractVersion}.");
        }

        if (metadata.IssuedAt > now.AddMinutes(5))
        {
            errors.Add("IssuedAt is more than five minutes in the future.");
        }

        if (metadata.ExpiresAt is not null && metadata.ExpiresAt <= metadata.IssuedAt)
        {
            errors.Add("ExpiresAt must be later than IssuedAt.");
        }

        if (metadata.Timeout < TimeSpan.FromMilliseconds(100) || metadata.Timeout > TimeSpan.FromMinutes(5))
        {
            errors.Add("Timeout must be between 100 milliseconds and five minutes.");
        }

        switch (command)
        {
            case ObserveMentionsCommand mentions:
                RequireValue(mentions.GroupStableId, nameof(mentions.GroupStableId), 256, errors);
                RequireValue(mentions.ExpectedGroupDisplayName, nameof(mentions.ExpectedGroupDisplayName), 256, errors);
                RequireValue(mentions.BotDisplayName, nameof(mentions.BotDisplayName), 128, errors);
                break;

            case UpdateRemarkCommand remark:
                RequireValue(remark.TargetStableId, nameof(remark.TargetStableId), 256, errors);
                RequireValue(remark.ExpectedDisplayName, nameof(remark.ExpectedDisplayName), 256, errors);
                RequireValue(remark.DesiredRemark, nameof(remark.DesiredRemark), 128, errors);
                if (remark.ExpectedCurrentRemark is { Length: > 128 })
                {
                    errors.Add("ExpectedCurrentRemark exceeds 128 characters.");
                }

                if (remark.DesiredRemark.Any(char.IsControl))
                {
                    errors.Add("DesiredRemark contains control characters.");
                }

                break;

            default:
                errors.Add($"Unsupported command type: {command.GetType().Name}.");
                break;
        }

        return errors;
    }

    private static void RequireIdentifier(string? value, string field, int maxLength, ICollection<string> errors)
    {
        RequireValue(value, field, maxLength, errors);
        if (!string.IsNullOrWhiteSpace(value) && value.Any(char.IsWhiteSpace))
        {
            errors.Add($"{field} must not contain whitespace.");
        }
    }

    private static void RequireValue(string? value, string field, int maxLength, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field} is required.");
        }
        else if (value.Length > maxLength)
        {
            errors.Add($"{field} exceeds {maxLength} characters.");
        }
    }
}
