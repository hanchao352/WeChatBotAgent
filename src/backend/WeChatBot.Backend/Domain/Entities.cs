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
    /// <summary>获取或设置 Agent 注册记录的全局唯一标识。</summary>
    public Guid Id { get; set; }

    /// <summary>获取或设置注册记录所属租户，所有认证查询必须限定此租户。</summary>
    public Guid TenantId { get; set; }

    /// <summary>获取或设置展示用 Agent 标识；保留注册时的大小写。</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>获取或设置用于唯一匹配的规范化 Agent 标识。</summary>
    public string NormalizedAgentId { get; set; } = string.Empty;

    /// <summary>获取或设置该 Agent 固定绑定的微信实例标识。</summary>
    public string WeChatInstanceId { get; set; } = string.Empty;

    /// <summary>获取或设置注册是否允许建立心跳和执行控制面操作。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>获取或设置下发给 Agent 的配置版本，用于心跳响应。</summary>
    public string ConfigurationVersion { get; set; } = "1";

    /// <summary>
    /// 获取或设置 Agent 独立凭据的 SHA-256 十六进制摘要；永远不保存或序列化明文凭据。
    /// 旧数据库记录可能为空，表示必须由管理员重新签发凭据后才能认证。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CredentialHash { get; set; }

    /// <summary>获取或设置当前凭据首次签发时间；轮换时保留首次签发事实。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CredentialIssuedAt { get; set; }

    /// <summary>获取或设置最近一次凭据轮换时间；首次签发时为空。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CredentialRotatedAt { get; set; }

    /// <summary>获取或设置凭据吊销时间；非空时即使摘要匹配也必须拒绝认证。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CredentialRevokedAt { get; set; }

    /// <summary>获取或设置注册创建时间。</summary>
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>获取或设置注册或凭据状态最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>获取或设置并发版本；管理操作必须携带期望版本。</summary>
    public long Version { get; set; } = 1;
}

public sealed class AgentHeartbeatState : ITenantEntity
{
    /// <summary>获取或设置该遥测行所属的 Agent 注册主键，同时也是本实体主键。</summary>
    public Guid AgentRegistrationId { get; set; }

    /// <summary>获取或设置遥测所属租户，查询过滤器必须始终限定该值。</summary>
    public Guid TenantId { get; set; }

    /// <summary>获取或设置 Agent 声明的心跳发送时间。</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>获取或设置服务端接收并持久化该心跳的时间。</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>获取或设置 Agent 最近报告的运行状态。</summary>
    public AgentOperatingState RuntimeState { get; set; }

    /// <summary>获取或设置便于监控聚合的稳定原因代码。</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>获取或设置不含敏感信息的运行状态说明。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>获取或设置 Agent 最近一次运行状态变化时间。</summary>
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>获取或设置最近一次命令完成时间；尚未完成命令时为空。</summary>
    public DateTimeOffset? LastCommandCompletedAt { get; set; }

    /// <summary>获取或设置最近一次命令结果代码；尚无命令结果时为空。</summary>
    public string? LastCommandCode { get; set; }

    /// <summary>获取或设置 Agent 当前等待执行的命令数量，必须为非负值。</summary>
    public int QueueDepth { get; set; }

    /// <summary>获取或设置 Agent 当前正在执行的命令数量，必须为非负值。</summary>
    public int ActiveExecutions { get; set; }

    /// <summary>获取或设置 Agent 是否处于禁止真实外部修改的 dry-run 模式。</summary>
    public bool DryRun { get; set; }

    /// <summary>获取或设置 Agent 报告的软件版本。</summary>
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>获取或设置最近一次错误微信实例绑定被拒绝的服务端时间。</summary>
    public DateTimeOffset? LastRejectedAt { get; set; }

    /// <summary>获取或设置最近一次被拒绝的微信实例标识，仅用于限频审计。</summary>
    public string? LastRejectedWeChatInstanceId { get; set; }

    /// <summary>获取或设置乐观并发版本，防止旧心跳覆盖更新遥测。</summary>
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

/// <summary>
/// 表示一项待外部执行端应用的微信备注任务，并保存原子认领、续租和结果去重所需的服务端状态。
/// </summary>
public sealed class RemarkTask : ITenantEntity
{
    /// <summary>获取或设置任务的全局唯一标识。</summary>
    public Guid Id { get; set; }

    /// <summary>获取或设置任务所属租户；所有租约写入必须同时匹配此字段。</summary>
    public Guid TenantId { get; set; }

    /// <summary>获取或设置生成本任务的备注规则标识。</summary>
    public Guid RuleId { get; set; }

    /// <summary>获取或设置备注目标类型，仅允许联系人或群。</summary>
    public ServiceTargetKind TargetKind { get; set; }

    /// <summary>获取或设置租户内的备注目标标识。</summary>
    public Guid TargetId { get; set; }

    /// <summary>获取或设置创建任务时解析到的目标外部稳定标识，供 Agent 执行前定位和校验。</summary>
    public string TargetExternalId { get; set; } = string.Empty;

    /// <summary>获取或设置创建任务时解析到的目标显示名称，防止同名或错目标操作。</summary>
    public string ExpectedTargetDisplayName { get; set; } = string.Empty;

    /// <summary>获取或设置管理员创建任务时使用的幂等键。</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>获取或设置创建请求的稳定摘要，用于拒绝幂等键绑定不同载荷。</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>获取或设置经规则渲染并清洗后的预期备注。</summary>
    public string GeneratedRemark { get; set; } = string.Empty;

    /// <summary>获取或设置创建任务时读取到的系统备注快照。</summary>
    public string? OriginalSystemRemark { get; set; }

    /// <summary>获取或设置创建任务时读取到的微信备注快照。</summary>
    public string? OriginalWeChatRemark { get; set; }

    /// <summary>获取或设置任务业务状态；只有待处理任务可以持有租约。</summary>
    public RemarkTaskStatus Status { get; set; }

    /// <summary>获取或设置因备注保护或快照冲突产生的说明。</summary>
    public string? ConflictReason { get; set; }

    /// <summary>获取或设置执行失败原因；成功完成时必须为空。</summary>
    public string? FailureReason { get; set; }

    /// <summary>获取或设置当前租约持有者的规范化 Agent 标识；无活动租约时为空。</summary>
    public string? ClaimedByAgentId { get; set; }

    /// <summary>获取或设置当前租约绑定的微信实例标识；无活动租约时为空。</summary>
    public string? ClaimedWeChatInstanceId { get; set; }

    /// <summary>获取或设置不透明租约令牌的 SHA-256 摘要；数据库绝不保存令牌明文。</summary>
    public string? LeaseTokenHash { get; set; }

    /// <summary>获取或设置租约的 UTC 过期时刻；到期后原持有者不得续租、释放或完成。</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>获取或设置成功认领次数；每次首次认领或过期重领均递增一次。</summary>
    public int AttemptCount { get; set; }

    /// <summary>获取或设置最终完成结果的调用方幂等标识；仅在任务进入终态后保存。</summary>
    public string? CompletionResultId { get; set; }

    /// <summary>获取或设置任务创建的 UTC 时刻。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>获取或设置任务进入完成或失败终态的 UTC 时刻。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>获取或设置任务并发版本；所有租约状态转换必须显式匹配并递增。</summary>
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
