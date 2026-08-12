using System.ComponentModel.DataAnnotations;
using WeChatBot.Backend.Domain;

namespace WeChatBot.Backend.Contracts;

public sealed record ContactCreateRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string ExternalId,
    [param: Required, StringLength(256, MinimumLength = 1), NonWhiteSpace] string DisplayName,
    [param: StringLength(128)] string? WeChatId,
    [param: StringLength(128)] string? CustomerCode,
    [param: StringLength(256)] string? CurrentWeChatRemark,
    bool ManualRemarkProtected,
    DateTimeOffset? ServiceExpiresAt);

public sealed record ContactUpdateRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string ExternalId,
    [param: Required, StringLength(256, MinimumLength = 1), NonWhiteSpace] string DisplayName,
    [param: StringLength(128)] string? WeChatId,
    [param: StringLength(128)] string? CustomerCode,
    [param: StringLength(256)] string? CurrentWeChatRemark,
    bool ManualRemarkProtected,
    DateTimeOffset? ServiceExpiresAt);

public sealed record GroupCreateRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string ExternalId,
    [param: Required, StringLength(256, MinimumLength = 1), NonWhiteSpace] string DisplayName,
    [param: StringLength(256)] string? BusinessName,
    [param: StringLength(256)] string? CurrentWeChatRemark,
    bool ManualRemarkProtected,
    DateTimeOffset? ServiceExpiresAt);

public sealed record GroupUpdateRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string ExternalId,
    [param: Required, StringLength(256, MinimumLength = 1), NonWhiteSpace] string DisplayName,
    [param: StringLength(256)] string? BusinessName,
    [param: StringLength(256)] string? CurrentWeChatRemark,
    bool ManualRemarkProtected,
    DateTimeOffset? ServiceExpiresAt);

public sealed record RemarkRuleCreateRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string Name,
    [param: EnumDataType(typeof(ServiceTargetKind))] ServiceTargetKind TargetKind,
    [param: Required, StringLength(512, MinimumLength = 1)] string Template,
    [param: EnumDataType(typeof(RemarkConflictPolicy))] RemarkConflictPolicy ConflictPolicy,
    bool IsEnabled,
    [param: Range(1, 256)] int MaxLength = 32);

public sealed record RemarkRuleUpdateRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string Name,
    [param: EnumDataType(typeof(ServiceTargetKind))] ServiceTargetKind TargetKind,
    [param: Required, StringLength(512, MinimumLength = 1)] string Template,
    [param: EnumDataType(typeof(RemarkConflictPolicy))] RemarkConflictPolicy ConflictPolicy,
    bool IsEnabled,
    [param: Range(1, 256)] int MaxLength = 32);

public sealed record RemarkTaskRequest(Guid RuleId, Guid TargetId);

public sealed record RemarkTaskCompleteRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    bool Succeeded,
    [param: StringLength(256)] string? AppliedRemark,
    [param: StringLength(1000)] string? FailureReason);

public sealed record GroupMentionRequest(
    [param: Required, StringLength(160, MinimumLength = 1), NonWhiteSpace] string ExternalEventId,
    Guid GroupId,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string SenderExternalId,
    [param: Required, StringLength(4000, MinimumLength = 1), NonWhiteSpace] string Content,
    bool MentionedBot,
    bool SenderIsBot,
    DateTimeOffset CapturedAt);

public sealed record IssueActivationCodeRequest(
    [param: Required, StringLength(64, MinimumLength = 1), NonWhiteSpace] string PackageCode,
    [param: EnumDataType(typeof(ServiceDurationKind))] ServiceDurationKind Duration,
    DateTimeOffset? ExpiresAt);

public sealed record RedeemActivationCodeRequest(
    [param: Required, StringLength(80, MinimumLength = 16), NonWhiteSpace] string Code,
    [param: EnumDataType(typeof(ServiceTargetKind))] ServiceTargetKind TargetKind,
    Guid TargetId);

public sealed record ActivateServiceRequest(
    [param: Required, StringLength(64, MinimumLength = 1), NonWhiteSpace] string PackageCode,
    [param: EnumDataType(typeof(ServiceDurationKind))] ServiceDurationKind Duration,
    [param: EnumDataType(typeof(ServiceTargetKind))] ServiceTargetKind TargetKind,
    Guid TargetId);

public sealed record RevokeActivationCodeRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    [param: Required, StringLength(500, MinimumLength = 3), NonWhiteSpace] string Reason);

public sealed record EntitlementStateRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    [param: EnumDataType(typeof(EntitlementState))] EntitlementState State,
    [param: StringLength(500)] string? Reason);

public sealed record CreateBackupRequest([param: StringLength(160)] string? Reason);

public sealed record RestoreBackupRequest([param: Required, NonWhiteSpace] string Confirmation);

public sealed record AutomationStateRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    bool Paused,
    [param: Required, StringLength(500, MinimumLength = 3), NonWhiteSpace] string Reason);

public sealed record AgentRuntimeSnapshotRequest(
    [param: EnumDataType(typeof(AgentOperatingState))] AgentOperatingState State,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string ReasonCode,
    [param: Required, StringLength(1000, MinimumLength = 1), NonWhiteSpace] string Reason,
    DateTimeOffset ChangedAt,
    DateTimeOffset? LastCommandCompletedAt,
    [param: StringLength(128)] string? LastCommandCode);

public sealed record AgentHeartbeatRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string AgentId,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string WeChatInstanceId,
    DateTimeOffset SentAt,
    [param: Required] AgentRuntimeSnapshotRequest Runtime,
    [param: Range(0, 1000000)] int QueueDepth,
    [param: Range(0, 1000000)] int ActiveExecutions,
    bool DryRun,
    [param: Required, StringLength(64, MinimumLength = 1), NonWhiteSpace] string AgentVersion);

public sealed record RegisterAgentRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string AgentId,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string WeChatInstanceId,
    [param: Required, StringLength(64, MinimumLength = 1), NonWhiteSpace] string ConfigurationVersion);

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class NonWhiteSpaceAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is not string text || !string.IsNullOrWhiteSpace(text);

    public override string FormatErrorMessage(string name) => $"{name} cannot contain only whitespace.";
}

public sealed record AgentHeartbeatResponse(
    bool Accepted,
    bool EmergencyStop,
    string? ConfigurationVersion);

public sealed record AgentGroupMentionRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string WeChatInstanceId,
    [param: Required] GroupMentionRequest Event);

public sealed record AgentListItem(
    Guid Id,
    string AgentId,
    string WeChatInstanceId,
    bool IsEnabled,
    string ConfigurationVersion,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt,
    long Version,
    DateTimeOffset? SentAt,
    DateTimeOffset? ReceivedAt,
    AgentOperatingState? RuntimeState,
    string? ReasonCode,
    string? Reason,
    DateTimeOffset? ChangedAt,
    DateTimeOffset? LastCommandCompletedAt,
    string? LastCommandCode,
    int? QueueDepth,
    int? ActiveExecutions,
    bool? DryRun,
    string? AgentVersion,
    bool Online);
