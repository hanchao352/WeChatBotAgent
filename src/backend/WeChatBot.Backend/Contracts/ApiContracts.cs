using System.ComponentModel.DataAnnotations;
using WeChatBot.Backend.Domain;

namespace WeChatBot.Backend.Contracts;

/// <summary>
/// 表示一次基于游标的列表查询结果；调用方只能使用服务端返回的下一页游标继续遍历，
/// 不应解析、拼接或持久依赖游标内部格式。
/// </summary>
/// <typeparam name="T">当前页元素的响应类型。</typeparam>
/// <param name="Items">当前页按接口约定顺序排列的元素；没有匹配数据时为空集合。</param>
/// <param name="NextCursor">存在后续数据时返回的不透明游标；到达末页时为 <see langword="null"/>。</param>
/// <param name="HasMore">指示当前页之后是否还有数据，且其值始终与 <paramref name="NextCursor"/> 是否存在一致。</param>
public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);

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

/// <summary>
/// 表示 Agent 领取下一项待处理备注任务时提交的实例绑定信息。
/// </summary>
/// <param name="WeChatInstanceId">当前 Agent 已注册且最近健康心跳所绑定的微信实例标识。</param>
public sealed record RemarkTaskClaimRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string WeChatInstanceId);

/// <summary>
/// 表示 Agent 对现有备注任务租约执行续租或主动释放时提交的持有证明。
/// </summary>
/// <param name="WeChatInstanceId">租约绑定的微信实例标识。</param>
/// <param name="LeaseToken">认领响应中返回且只应由持有者保存的不透明令牌。</param>
/// <param name="ExpectedVersion">上一次服务端响应中的任务版本。</param>
public sealed record RemarkTaskLeaseRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string WeChatInstanceId,
    [param: Required, StringLength(128, MinimumLength = 32), NonWhiteSpace] string LeaseToken,
    [param: Range(1, long.MaxValue)] long ExpectedVersion);

/// <summary>
/// 表示 Agent 在持有有效租约时提交的备注任务最终执行结果。
/// </summary>
/// <param name="WeChatInstanceId">租约绑定的微信实例标识。</param>
/// <param name="LeaseToken">认领响应中返回的不透明租约令牌。</param>
/// <param name="ExpectedVersion">上一次认领或续租响应中的任务版本。</param>
/// <param name="ResultId">调用方生成的最终结果幂等标识，同一标识重试必须保持相同载荷。</param>
/// <param name="Succeeded">指示外部备注操作是否已被确认成功。</param>
/// <param name="AppliedRemark">成功时 Agent 确认已应用的实际备注，必须与任务生成值完全一致。</param>
/// <param name="FailureReason">失败时的稳定原因，不得为空。</param>
public sealed record RemarkTaskLeaseCompleteRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string WeChatInstanceId,
    [param: Required, StringLength(128, MinimumLength = 32), NonWhiteSpace] string LeaseToken,
    [param: Range(1, long.MaxValue)] long ExpectedVersion,
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string ResultId,
    bool Succeeded,
    [param: StringLength(256)] string? AppliedRemark,
    [param: StringLength(1000)] string? FailureReason);

/// <summary>
/// 表示服务端成功认领或续租后的任务快照和明文租约凭据。
/// </summary>
/// <param name="TaskId">被认领的备注任务标识。</param>
/// <param name="TargetKind">备注目标类型。</param>
/// <param name="TargetId">备注目标标识。</param>
/// <param name="TargetExternalId">创建任务时解析到的目标外部稳定标识。</param>
/// <param name="GeneratedRemark">Agent 应在安全边界内应用的预期备注。</param>
/// <param name="ExpectedTargetDisplayName">创建任务时解析到的目标显示名称，供执行前身份确认。</param>
/// <param name="OriginalWeChatRemark">创建任务时读取到的微信备注快照，供执行前乐观检查。</param>
/// <param name="LeaseToken">仅在响应中返回的不透明令牌，服务端只保存其摘要。</param>
/// <param name="LeaseExpiresAt">当前租约的 UTC 到期时刻。</param>
/// <param name="AttemptCount">任务累计成功认领次数。</param>
/// <param name="Version">本次状态转换后的并发版本。</param>
public sealed record RemarkTaskLeaseResponse(
    Guid TaskId,
    ServiceTargetKind TargetKind,
    Guid TargetId,
    string TargetExternalId,
    string GeneratedRemark,
    string ExpectedTargetDisplayName,
    string? OriginalWeChatRemark,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    int AttemptCount,
    long Version);

/// <summary>
/// 表示 Agent 最终结果提交后的稳定响应，网络重试时可返回同一结果。
/// </summary>
/// <param name="TaskId">进入终态的任务标识。</param>
/// <param name="Status">最终任务状态。</param>
/// <param name="ResultId">已持久化的调用方结果幂等标识。</param>
/// <param name="CompletedAt">服务端确认结果的 UTC 时刻。</param>
/// <param name="Version">终态任务版本。</param>
/// <param name="Replayed">指示本次响应是否为同一完成请求的幂等重放。</param>
public sealed record RemarkTaskLeaseCompletionResponse(
    Guid TaskId,
    RemarkTaskStatus Status,
    string ResultId,
    DateTimeOffset CompletedAt,
    long Version,
    bool Replayed);

/// <summary>
/// 表示 Agent 成功释放备注任务租约后的最新任务状态。
/// </summary>
/// <param name="TaskId">被释放的任务标识。</param>
/// <param name="Status">释放后保持不变的待处理状态。</param>
/// <param name="Version">释放操作递增后的任务版本。</param>
public sealed record RemarkTaskLeaseReleaseResponse(
    Guid TaskId,
    RemarkTaskStatus Status,
    long Version);

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

/// <summary>
/// 表示管理员对 Agent 凭据执行轮换或吊销时必须提供的乐观并发版本。
/// </summary>
/// <param name="ExpectedVersion">管理员读取注册时看到的版本，必须为正数且与数据库当前值一致。</param>
public sealed record AgentCredentialVersionRequest(
    [param: Range(1, long.MaxValue)] long ExpectedVersion);

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class NonWhiteSpaceAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is not string text || !string.IsNullOrWhiteSpace(text);

    public override string FormatErrorMessage(string name) => $"{name} cannot contain only whitespace.";
}

/// <summary>表示服务端对 Agent 心跳的控制面决定。</summary>
/// <param name="Accepted">当前绑定是否具备健康 dry-run 在线资格。</param>
/// <param name="EmergencyStop">租户自动化是否处于急停状态。</param>
/// <param name="ConfigurationVersion">注册记录要求的配置版本。</param>
public sealed record AgentHeartbeatResponse(
    bool Accepted,
    bool EmergencyStop,
    string? ConfigurationVersion);

/// <summary>表示已认证 Agent 上报群提及事件时携带的实例绑定和事件正文。</summary>
/// <param name="WeChatInstanceId">必须与凭据绑定一致的微信实例标识。</param>
/// <param name="Event">待写入并执行权益判定的群提及事件。</param>
public sealed record AgentGroupMentionRequest(
    [param: Required, StringLength(128, MinimumLength = 1), NonWhiteSpace] string WeChatInstanceId,
    [param: Required] GroupMentionRequest Event);

/// <summary>表示不含任何凭据明文或摘要的 Agent 注册、遥测与凭据生命周期视图。</summary>
/// <param name="Id">Agent 注册主键。</param>
/// <param name="AgentId">展示用 Agent 标识。</param>
/// <param name="WeChatInstanceId">固定绑定的微信实例标识。</param>
/// <param name="IsEnabled">注册是否允许认证。</param>
/// <param name="ConfigurationVersion">服务端要求的配置版本。</param>
/// <param name="RegisteredAt">注册创建时间。</param>
/// <param name="UpdatedAt">注册或凭据状态最近更新时间。</param>
/// <param name="Version">管理员写操作使用的乐观并发版本。</param>
/// <param name="SentAt">最近心跳的 Agent 发送时间。</param>
/// <param name="ReceivedAt">最近心跳的服务端接收时间。</param>
/// <param name="RuntimeState">最近报告的 Agent 运行状态。</param>
/// <param name="ReasonCode">最近运行状态的稳定原因代码。</param>
/// <param name="Reason">最近运行状态说明。</param>
/// <param name="ChangedAt">Agent 最近运行状态变化时间。</param>
/// <param name="LastCommandCompletedAt">最近命令完成时间。</param>
/// <param name="LastCommandCode">最近命令结果代码。</param>
/// <param name="QueueDepth">最近报告的等待命令数量。</param>
/// <param name="ActiveExecutions">最近报告的执行中命令数量。</param>
/// <param name="DryRun">最近报告的 dry-run 状态。</param>
/// <param name="AgentVersion">最近报告的 Agent 软件版本。</param>
/// <param name="Online">当前凭据会话内是否具有新鲜心跳。</param>
/// <param name="HasCredential">当前是否存在可认证且未吊销的凭据摘要。</param>
/// <param name="CredentialIssuedAt">首次签发时间。</param>
/// <param name="CredentialRotatedAt">最近轮换时间。</param>
/// <param name="CredentialRevokedAt">最近吊销时间。</param>
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
    bool Online,
    bool HasCredential,
    DateTimeOffset? CredentialIssuedAt,
    DateTimeOffset? CredentialRotatedAt,
    DateTimeOffset? CredentialRevokedAt);

/// <summary>
/// 表示首次签发或轮换成功的单次响应；明文凭据不会出现在列表、数据库、备份或后续重放中。
/// </summary>
/// <param name="Agent">不含凭据摘要和明文的注册列表视图。</param>
/// <param name="Credential">只在本次成功响应中返回的高熵 Agent 独立凭据。</param>
public sealed record AgentCredentialIssueResponse(
    AgentListItem Agent,
    string Credential);
