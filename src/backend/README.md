# WeChatBot Backend Core

这是 .NET 10 + ASP.NET Core + EF Core 的首个商业化方向纵向切片，不代表已达到商业生产上线条件。当前数据库提供者为 SQLite，领域模型和 `DbContext` 未使用 SQLite 专属业务类型；切换 PostgreSQL 时需要替换提供者注册并生成独立迁移。

## 已实现范围

- 联系人、群聊资料和服务端乐观并发控制
- 联系人/群聊自动备注规则、预览、冲突保护、幂等任务与执行结果回报
- 群 `@机器人` 事件去重、防机器人自循环、权益检查和待激活提示
- `BASIC`、`ADVANCED_GENERAL` 两种通用服务包，不包含游戏包
- 30/60/90 天、半年、一年、永久权益；有效区间统一为 `[StartsAt, EndsAt)`
- 一次性激活码；仅保存带 pepper 的 HMAC-SHA256，明文只在签发响应中出现一次
- API Key 服务端鉴权、租户查询过滤、ProblemDetails、不可修改的审计/权益流水
- AES-256-GCM 逻辑备份、SHA-256 清单校验、恢复前自动备份和合并恢复
- OpenAPI/Swagger、开发种子数据、数据库迁移、存活/就绪检查

此项目只提供业务编排和管理 API。备注任务进入 `pending` 不代表微信已执行；只有外部执行端回报相同的实际备注后才会变为 `completed`。本项目不包含或宣称已实现微信 UI Automation。

## 本机启动

```powershell
dotnet run --project E:\WeChatBot\src\backend\WeChatBot.Backend\WeChatBot.Backend.csproj
```

默认地址：`http://localhost:5188/swagger`。

仅在 `Development`/`Testing` 环境提供以下本机管理员凭据：

```text
X-Api-Key: wechatbot-local-development-key-change-me
```

开发环境会创建一个租户、两个通用服务包、示例联系人、示例群和两条备注规则。SQLite 数据和加密备份默认位于项目工作目录的 `runtime/`。

## 控制面身份边界

后台使用管理员 Key 和每 Agent 独立凭据两类权限域：

- 管理员 Key（`Auth__ApiKey`）用于管理、审计、备份恢复和 Agent 列表等 Admin 接口，只能由可信管理端持有。
- Agent 独立凭据由 `POST /api/agents` 首次签发，后端只保存 SHA-256 摘要；认证 claim 绑定 `agent_registration_id`、`agent_id`、`wechat_instance_id` 和 `tenant_id`。
- `POST /api/agents/{registrationId}/credential/rotate` 和 `/revoke` 提供带版本并发控制的轮换与吊销；列表接口永远不返回明文或摘要。
- `Auth__AgentApiKey` 只作为显式 Development/Testing 迁移兼容项，必须配合 `Auth__AllowLegacySharedAgentApiKey=true`；Production 开启该项会 fail-fast。

详细字段、错误码、备份和恢复语义见 [Agent 独立凭据](../../docs/AGENT_CREDENTIALS.md)。

## 生产环境必填配置

生产环境没有回退密钥，以下配置缺失或过短时进程会拒绝启动：

```powershell
$env:Auth__ApiKey = '<至少 32 个字符的管理员随机密钥>'
$env:Auth__TenantId = '<非空 GUID>'
$env:Auth__ActorName = 'production-admin'
$env:Auth__AgentActorName = 'production-agent'
$env:Auth__AllowAgentAutoRegistration = 'false'
$env:Auth__AllowLegacySharedAgentApiKey = 'false'
$env:Activation__HashPepper = '<至少 32 个字符的随机密钥>'
$env:Audit__IntegrityKey = '<至少 32 个字符的随机密钥>'
$env:Pagination__ProtectionKey = '<至少 32 个字符的独立高熵随机密钥>'
$env:RemarkTaskLease__DurationSeconds = '60'
$env:Backup__EncryptionKeyBase64 = '<32 字节随机值的 Base64>'
$env:Backup__Directory = 'D:\SecureBackups\WeChatBot'
$env:ConnectionStrings__Database = 'Data Source=D:\WeChatBotData\wechatbot.db;Default Timeout=30;Pooling=True'
```

生产不需要配置共享 `Auth__AgentApiKey`；管理员完成预注册后，将每条响应中的 `credential` 通过安全密钥管理系统下发给对应 Agent。任何 Agent 设备都不得持有管理员 Key 或其他设备凭据。

可生成备份密钥：

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

生产部署还应在反向代理或入口网关启用 TLS、来源网络限制、密钥轮换和请求限流。

## 关键接口

| 能力 | 接口 |
| --- | --- |
| 联系人/群聊 | `/api/contacts`、`/api/groups` |
| 备注规则/任务 | `/api/remark-rules`、`/api/remark-tasks` |
| 群 @ 事件 | `/api/group-mentions` |
| Agent 心跳（Agent 角色） | `POST /api/agents/heartbeat` |
| Agent 群消息上报（Agent 角色） | `POST /api/agents/{agentId}/group-mentions` |
| Agent 备注任务租约（Agent 角色） | `POST /api/agents/{agentId}/remark-tasks/claim`、`renew`、`release`、`complete` |
| Agent 预注册/列表/凭据生命周期（Admin 角色） | `POST /api/agents`、`GET /api/agents`、`POST /api/agents/{registrationId}/credential/rotate`、`POST /api/agents/{registrationId}/credential/revoke` |
| 服务包/权益 | `/api/service-packages`、`/api/entitlements` |
| 激活码 | `/api/activation-codes`、`/api/activation-codes/redeem` |
| 审计 | `/api/audit-logs` |
| 备份/恢复 | `/api/backups`、`/api/backups/{id}/verify`、`/api/backups/{id}/restore` |
| 自动化总状态 | `/api/system-state`、`/api/system-state/automation` |
| 健康检查 | `/health/live`、`/health/ready` |

联系人和群列表支持游标分页：第一页使用 `?pageSize=100`，后续页原样回传响应中的 `nextCursor`。未传 `pageSize`/`cursor` 时仍返回旧版数组以兼容现有管理端；`take` 不能与游标参数混用。游标经过 AES-256-GCM 保护并绑定资源和租户，生产环境必须配置独立的 `Pagination__ProtectionKey`。完整响应、错误码、密钥轮换和并发数据语义见 [API 游标分页](../../docs/API_PAGINATION.md)。

激活兑换、备注任务创建、手工备份创建和备份恢复要求 `Idempotency-Key` 请求头。生产环境必须关闭 Agent 自动注册，由管理员先调用 `POST /api/agents` 绑定 AgentId 与微信实例，再允许该 Agent 建立心跳。恢复还要求正文中的 `confirmation` 精确为 `RESTORE`；恢复采用合并策略，不覆盖当前数据库中更可信的兑换、撤销和权益流水，并始终将自动化状态置为暂停，需人工核对后显式恢复。

备注任务已提供数据库原子租约协议，完整合同见 [备注任务租约协议](../../docs/REMARK_TASK_LEASES.md)。Agent 必须使用独立凭据先建立最近的健康 dry-run 心跳，然后通过 `claim` 获取只返回一次明文的不可猜测令牌；服务端只保存 SHA-256 摘要。逻辑备份模式版本 5 同时剥离活动租约和 Agent 凭据材料，恢复后所有 Agent 必须重新签发凭据并重新认领任务。

当前恢复接口的真实模式是 `in-place-merge`：它在同一租户数据库中先创建恢复前快照，再写入备份中的配置并补齐缺失历史事实。它不会创建隔离数据库、临时环境或生产切换任务，因此管理端不得将该接口标注为“隔离恢复”。真正的隔离恢复和切换编排属于后续灾备能力。

## 验证

```powershell
dotnet test E:\WeChatBot\tests\backend\WeChatBot.Backend.Tests\WeChatBot.Backend.Tests.csproj
dotnet list E:\WeChatBot\src\backend\WeChatBot.Backend\WeChatBot.Backend.csproj package --include-transitive --vulnerable
```
