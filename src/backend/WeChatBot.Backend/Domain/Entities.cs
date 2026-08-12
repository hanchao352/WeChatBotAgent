namespace WeChatBot.Backend.Domain;

public interface ITenantEntity
{
    Guid TenantId { get; set; }
}

public enum ServiceTargetKind
{
    Contact = 1,
    Group = 2
}

public enum RemarkConflictPolicy
{
    Skip = 1,
    OverwriteSystemGeneratedOnly = 2
}

public enum RemarkTaskStatus
{
    Pending = 1,
    Conflict = 2,
    Completed = 3,
    Failed = 4,
    Canceled = 5
}

public enum PackageTier
{
    Basic = 1,
    AdvancedGeneral = 2
}

public enum ServiceDurationKind
{
    Days30 = 1,
    Days60 = 2,
    Days90 = 3,
    HalfYear = 4,
    OneYear = 5,
    Permanent = 6
}

public enum EntitlementState
{
    Active = 1,
    Suspended = 2,
    Revoked = 3
}

public enum MentionDecision
{
    Accepted = 1,
    IgnoredNotMentioned = 2,
    IgnoredBotMessage = 3,
    ActivationRequired = 4,
    AutomationPaused = 5
}

public enum BackupStatus
{
    Created = 1,
    Verified = 2,
    Corrupt = 3
}

public enum AgentOperatingState
{
    Starting = 0,
    Healthy = 1,
    PausedUnknownUi = 2,
    PausedControlPlane = 3,
    PausedOperator = 4,
    Maintenance = 5,
    Stopping = 6
}

public sealed class TenantState : ITenantEntity
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool AutomationPaused { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AgentRegistration : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string NormalizedAgentId { get; set; } = string.Empty;
    public string WeChatInstanceId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string ConfigurationVersion { get; set; } = "1";
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AgentHeartbeatState : ITenantEntity
{
    public Guid AgentRegistrationId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public AgentOperatingState RuntimeState { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; }
    public DateTimeOffset? LastCommandCompletedAt { get; set; }
    public string? LastCommandCode { get; set; }
    public int QueueDepth { get; set; }
    public int ActiveExecutions { get; set; }
    public bool DryRun { get; set; }
    public string AgentVersion { get; set; } = string.Empty;
    public DateTimeOffset? LastRejectedAt { get; set; }
    public string? LastRejectedWeChatInstanceId { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class Contact : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? WeChatId { get; set; }
    public string? CustomerCode { get; set; }
    public string? SystemRemark { get; set; }
    public string? CurrentWeChatRemark { get; set; }
    public bool ManualRemarkProtected { get; set; }
    public DateTimeOffset? ServiceExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class GroupChat : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? BusinessName { get; set; }
    public string? SystemRemark { get; set; }
    public string? CurrentWeChatRemark { get; set; }
    public bool ManualRemarkProtected { get; set; }
    public DateTimeOffset? ServiceExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class RemarkRule : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ServiceTargetKind TargetKind { get; set; }
    public string Template { get; set; } = string.Empty;
    public RemarkConflictPolicy ConflictPolicy { get; set; }
    public bool IsEnabled { get; set; }
    public int MaxLength { get; set; } = 32;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class RemarkTask : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RuleId { get; set; }
    public ServiceTargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string GeneratedRemark { get; set; } = string.Empty;
    public string? OriginalSystemRemark { get; set; }
    public string? OriginalWeChatRemark { get; set; }
    public RemarkTaskStatus Status { get; set; }
    public string? ConflictReason { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class GroupMentionEvent : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ExternalEventId { get; set; } = string.Empty;
    public Guid GroupId { get; set; }
    public string SenderExternalId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool MentionedBot { get; set; }
    public bool SenderIsBot { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public MentionDecision Decision { get; set; }
    public string? DecisionReason { get; set; }
    public Guid? EntitlementId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ServicePackage
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PackageTier Tier { get; set; }
    public string FeaturesJson { get; set; } = "[]";
    public bool IsEnabled { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class Entitlement : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public ServiceTargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public Guid PackageId { get; set; }
    public ServiceDurationKind DurationKind { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public EntitlementState State { get; set; }
    public string Source { get; set; } = string.Empty;
    public Guid? ActivationCodeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class EntitlementLedger : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EntitlementId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
}

public sealed class ActivationCode : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public Guid PackageId { get; set; }
    public ServiceDurationKind DurationKind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset? RedeemedAt { get; set; }
    public ServiceTargetKind? RedeemedTargetKind { get; set; }
    public Guid? RedeemedTargetId { get; set; }
    public Guid? EntitlementId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class IdempotencyRecord : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ResponseJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AuditLog : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? IpAddress { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public string IntegrityHash { get; set; } = string.Empty;
}

public sealed class BackupManifest : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public int SchemaVersion { get; set; }
    public string CountsJson { get; set; } = "{}";
    public BackupStatus Status { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class RestoreOperation : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BackupManifestId { get; set; }
    public Guid PreRestoreBackupManifestId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ReportJson { get; set; } = "{}";
}

public static class WellKnownPackages
{
    public static readonly Guid BasicId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
    public static readonly Guid AdvancedGeneralId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
}

public static class WellKnownFeatures
{
    public const string GroupMention = "group-mention";
    public const string AutoRemark = "auto-remark";
}
