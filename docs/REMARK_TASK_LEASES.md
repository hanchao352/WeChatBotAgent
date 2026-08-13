# 备注任务租约协议

## 范围与安全边界

该协议解决多个 Agent 对同一 `pending` 备注任务的重复领取、过期回收、非持有者续租/完成以及完成结果网络重试问题。它只负责后端任务编排和服务端备注镜像，不实现或证明微信 UI 已被修改。

当前 Windows Agent 仍强制 dry-run，未实现真实联系人或群备注写入。配置 `WECHATBOT_AGENT_REMARK_TASK_LEASE_URI` 只会启用租约客户端和常驻后台组件，后台组件在该构建中保持空闲，不会自动领取或释放生产队列任务。否则，服务端按创建时间领取最早任务时，dry-run 的反复领取/释放会持续命中同一任务，既饿死后续任务，又产生无业务结果的写放大。受控测试或诊断可显式执行单次领取、串行安全预检/预览和释放，但不得提交成功结果或将预览伪装成真实微信修改。游戏包功能仍不在本协议或当前项目范围内。

租约客户端将未由宿主取消令牌触发的 `HttpClient` 请求超时规范化为可恢复的 HTTP 传输失败，使调用方可以沿用控制面故障处理和退避语义；宿主主动取消仍保持取消语义并用于正常关闭。只有经过单独评审、能够执行并验证真实 UI 修改的适配器上线后，才可以启用生产队列自动领取。

## 前置条件

- 所有端点要求与注册记录绑定的 Agent 独立凭据；凭据生命周期和兼容开关见 [Agent 独立凭据](AGENT_CREDENTIALS.md)。
- `agentId` 必须已注册并绑定请求中的 `weChatInstanceId`。
- Agent 必须有最近 60 秒内、晚于最近一次自动化状态切换的 `Healthy + DryRun=true` 心跳。
- 租户自动化暂停时，所有 Agent 租约操作由绑定门禁拒绝。
- 租约时长由 `RemarkTaskLease__DurationSeconds` 配置，允许 15 到 300 秒，默认 60 秒。

认证处理器通过凭据摘要查询当前租户的 `AgentRegistration`，并将 `agent_registration_id`、`agent_id`、`wechat_instance_id` 和 `tenant_id` 写入 claim。租约服务在每个状态转换前重新验证 claim、路由、正文和当前注册状态，因此轮换或吊销会立即阻断旧会话。旧 `Auth__AgentApiKey` 仅可在 Development/Testing 显式兼容开关下使用，Production 会拒绝启动。

## 端点

| 操作 | 方法与路径 | 成功响应 |
| --- | --- | --- |
| 领取 | `POST /api/agents/{agentId}/remark-tasks/claim` | `200` + 租约；无任务为 `204` |
| 续租 | `POST /api/agents/{agentId}/remark-tasks/{taskId}/renew` | `200` + 新到期时间和版本 |
| 释放 | `POST /api/agents/{agentId}/remark-tasks/{taskId}/release` | `200` + 待处理状态和版本 |
| 完成 | `POST /api/agents/{agentId}/remark-tasks/{taskId}/complete` | `200` + 稳定终态结果 |

领取正文：

```json
{
  "weChatInstanceId": "wx-instance-01"
}
```

领取响应中的 `leaseToken` 是 32 字节随机值的 Base64Url 表示，只在响应中返回；数据库仅保存 SHA-256 摘要。调用方不得记录令牌、放入 URL、审计详情或遥测标签。响应还包含创建任务时固化的 `targetExternalId`、`expectedTargetDisplayName` 和 `originalWeChatRemark`，用于 Agent 定位目标并在执行前确认身份和当前值；`version` 必须原样用于下一次状态转换。

续租和释放正文：

```json
{
  "weChatInstanceId": "wx-instance-01",
  "leaseToken": "opaque-token-from-claim",
  "expectedVersion": 2
}
```

完成正文：

```json
{
  "weChatInstanceId": "wx-instance-01",
  "leaseToken": "opaque-token-from-claim",
  "expectedVersion": 2,
  "resultId": "agent-command-result-uuid",
  "succeeded": false,
  "appliedRemark": null,
  "failureReason": "UI confirmation unavailable"
}
```

成功结果要求 `appliedRemark` 与任务的 `generatedRemark` 完全一致且 `failureReason` 为空；失败结果要求 `failureReason` 非空且 `appliedRemark` 为空。服务端会裁剪 `resultId` 和失败原因的首尾空白，并将空白可选字段规范为空值，但不会裁剪成功备注。`resultId` 在租户内唯一；只有任务标识以及规范化后的 `succeeded`、`appliedRemark`、`failureReason` 三项结果字段全部一致时，重试才返回 `replayed=true`。同一结果标识复用于不同任务或不同规范结果载荷，以及同一任务再次绑定其他结果标识时，均返回 `remark_task_result_conflict`。并发提交由数据库事务串行决定唯一终态：完全相同的并发结果即使在重放查询期间才完成提交，也会重新读取任务终态并收敛为一次提交和幂等重放；不同结果只能有一个获胜。

## 并发与过期语义

领取使用条件更新，条件显式包含租户、任务 ID、`Pending` 状态、无租约或已过期，以及读取到的 `Version`。只有受影响行数为 1 的调用方获得租约；成功后 `AttemptCount` 和 `Version` 各递增一次。

续租和释放额外要求 Agent、微信实例、令牌摘要、未过期时间和期望版本全部匹配。旧租约一旦到期便不能续租、释放或完成，任务可被其他 Agent 重新领取。释放清除全部租约字段但保留 `Pending` 和累计尝试次数。

完成在数据库事务中写入任务终态、目标备注镜像和审计。成功结果会重新校验自动化开关、`auto-remark` 权益、外部 ID/显示名称身份快照、人工备注保护及系统/微信备注快照。终态任务清除全部租约字段，保存 `CompletionResultId`，旧令牌不再有效。管理员完成接口同样先取得备注任务表对应的 SQLite 写锁，再在事务内读取任务、校验版本和租约并写入终态；因此 Agent 认领、自动化暂停或权益状态写入不能插入管理员的门禁校验与提交之间。管理员不能绕过尚未过期的 Agent 租约；租约过期后允许接管，但写入成功或失败终态时同样会清除 Agent 标识、微信实例、令牌摘要和到期时间。管理员与 Agent 完成都强制结果字段互斥：成功结果不得携带 `failureReason`，失败结果不得携带 `appliedRemark` 且必须提供非空 `failureReason`。

管理员创建任务也在取得 SQLite 写锁后重新读取幂等记录、生成预览并校验自动化暂停和 `auto-remark` 权益，随后在同一事务中写入任务与审计。因此并发暂停或权益撤销只能排在本次创建提交之前或之后，不存在校验通过后再穿透门禁写入任务的窗口。

## 备份与恢复

逻辑备份模式版本 5 在版本 4 规则基础上，使用不含 `CredentialHash` 及签发/轮换/吊销时间的 Agent 注册 DTO；序列化备注任务前清除 `ClaimedByAgentId`、`ClaimedWeChatInstanceId`、`LeaseTokenHash` 和 `LeaseExpiresAt`，但保留 `AttemptCount` 与已经终态化的 `CompletionResultId`。恢复任意旧版本或 v5 时都会清除当前凭据并将恢复注册标记为吊销，避免恢复后旧 Agent 继续使用灾备前授权。

数据库从租约协议之前的版本升级时，迁移会按任务的 `TenantId + TargetKind + TargetId` 从 `Contacts` 或 `Groups` 回填 `TargetExternalId` 和 `ExpectedTargetDisplayName`。目标类型和租户都必须匹配，迁移不会借用其他租户或其他目标类型的同标识记录。

schema v1-v3 备份尚未定义这两项身份快照。恢复服务会在联系人和群合并后，只为缺失字段的旧任务补值：优先使用备份集合中的目标身份；备份未携带该目标时，回退到当前租户数据库中的对应联系人或群。旧载荷已经提供的非空字段不会被覆盖。目标在两处都不存在时返回 `backup_reference_integrity_failed`；目标存在但外部 ID 或显示名称不完整时返回 `backup_remark_task_identity_invalid`，不会写入永久不可领取的任务。

schema v4 不使用上述兼容补值。其任务必须在载荷校验阶段自带非空 `TargetExternalId` 和 `ExpectedTargetDisplayName`，否则恢复在创建恢复前备份或修改业务数据之前拒绝执行。

恢复始终暂停自动化。历史心跳仅作为遥测保留，凭据为空或已吊销时不计入在线状态；重新签发后，心跳还必须晚于本次凭据轮换时间。管理员核对并恢复自动化后，Agent 需要重新发送健康 dry-run 心跳，并重新领取待处理任务。

租约状态转换和 Agent 群提及上报在 SQLite 写事务取得写锁后复核当前凭据，再执行同一事务内的业务写入，从而阻止轮换、吊销或恢复吊销插入复核与写入之间。当前依赖版本的零行更新写锁语义及升级约束见 `docs/SQLITE_WRITE_LOCKS.md`。

并发回归测试使用仅在测试宿主中注入的同步观察点，把认领请求稳定停在身份复核之后；测试客户端确认轮换请求已发出，并由第二 SQLite 连接直接验证写锁仍被持有，再断言轮换尚未完成。释放后再验证旧凭据心跳返回 `401`。生产默认实现直接返回已完成任务，不保存状态、不改变时序。

## 主要错误码

| 错误码 | 含义 |
| --- | --- |
| `agent_lease_unavailable` | Agent 注册、实例、心跳、健康状态或 dry-run 门禁不满足 |
| `automation_paused` | 租户自动化已暂停 |
| `remark_task_claim_busy` | SQLite 写竞争超过有限重试，可安全重新领取 |
| `remark_task_lease_expired` | 租约已到期，不得继续使用 |
| `remark_task_lease_not_owned` | Agent、实例或令牌不匹配 |
| `concurrency_conflict` | 任务版本已变化，必须使用最新响应 |
| `remark_task_result_conflict` | 任务已有其他结果，或结果 ID 已绑定不同载荷 |
| `remark_task_leased` | 管理员完成接口遇到仍有效的 Agent 租约 |
| `remark_target_changed` | 目标备注已偏离创建任务时的快照 |
| `remark_target_identity_changed` | 目标外部 ID 或显示名称已偏离创建任务时的快照 |
| `auto_remark_feature_required` | 目标当前无有效自动备注权益 |
| `backup_reference_integrity_failed` | 旧备份任务引用的目标在备份和当前租户数据库中均不存在 |
| `backup_remark_task_identity_invalid` | 备份任务或其目标无法提供完整稳定身份快照 |
