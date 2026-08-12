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

后台使用相互独立的两类 API Key，禁止混用或向另一角色下放：

- 管理员 Key（`Auth__ApiKey`）用于管理、审计、备份恢复和 Agent 列表等 Admin 接口，只能由可信管理端持有。
- Agent Key（`Auth__AgentApiKey`）仅用于 Agent 向控制面提交心跳，以及在匹配的健康 dry-run 绑定下上报群消息；不授予任何 Admin 接口权限。
- `Auth__ActorName` 和 `Auth__AgentActorName` 分别标识管理员与 Agent 的审计主体，不能用同一角色名掩盖调用来源。

当前版本的 Agent Key 是所有 Agent 共享的基线凭据，只适合受控网络内的初始部署。正式商业生产环境必须升级为设备预注册的独立凭据或客户端证书，使每台设备可单独识别、轮换和吊销；共享 Agent Key 不能作为最终生产身份方案。

## 生产环境必填配置

生产环境没有回退密钥，以下配置缺失或过短时进程会拒绝启动：

```powershell
$env:Auth__ApiKey = '<至少 32 个字符的管理员随机密钥>'
$env:Auth__AgentApiKey = '<至少 32 个字符的 Agent 随机密钥>'
$env:Auth__TenantId = '<非空 GUID>'
$env:Auth__ActorName = 'production-admin'
$env:Auth__AgentActorName = 'production-agent'
$env:Activation__HashPepper = '<至少 32 个字符的随机密钥>'
$env:Audit__IntegrityKey = '<至少 32 个字符的随机密钥>'
$env:Backup__EncryptionKeyBase64 = '<32 字节随机值的 Base64>'
$env:Backup__Directory = 'D:\SecureBackups\WeChatBot'
$env:ConnectionStrings__Database = 'Data Source=D:\WeChatBotData\wechatbot.db;Default Timeout=30;Pooling=True'
```

`Auth__ApiKey` 与 `Auth__AgentApiKey` 必须使用不同的随机值，并按不同权限域分别存储、轮换和吊销。任何 Agent 设备都不得持有管理员 Key。

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
| Agent 列表（Admin 角色） | `GET /api/agents` |
| 服务包/权益 | `/api/service-packages`、`/api/entitlements` |
| 激活码 | `/api/activation-codes`、`/api/activation-codes/redeem` |
| 审计 | `/api/audit-logs` |
| 备份/恢复 | `/api/backups`、`/api/backups/{id}/verify`、`/api/backups/{id}/restore` |
| 自动化总状态 | `/api/system-state`、`/api/system-state/automation` |
| 健康检查 | `/health/live`、`/health/ready` |

激活兑换、备注任务创建和备份恢复要求 `Idempotency-Key` 请求头。恢复还要求正文中的 `confirmation` 精确为 `RESTORE`；恢复采用合并策略，不覆盖当前数据库中更可信的兑换、撤销和权益流水，并始终将自动化状态置为暂停，需人工核对后显式恢复。

当前没有向 Agent 角色开放备注任务领取或结果回报。`RemarkTask` 还缺少认领 Agent、不可猜测的租约令牌、租约到期时间、尝试次数和结果去重标识；在这些字段及原子认领/续租/完成协议通过数据库迁移落地前，多 Agent 消费会存在重复执行和越权回报风险。现有 `/api/remark-tasks/{id}/complete` 是管理员业务接口，不是 Agent 执行协议。

当前恢复接口的真实模式是 `in-place-merge`：它在同一租户数据库中先创建恢复前快照，再写入备份中的配置并补齐缺失历史事实。它不会创建隔离数据库、临时环境或生产切换任务，因此管理端不得将该接口标注为“隔离恢复”。真正的隔离恢复和切换编排属于后续灾备能力。

## 验证

```powershell
dotnet test E:\WeChatBot\tests\backend\WeChatBot.Backend.Tests\WeChatBot.Backend.Tests.csproj
dotnet list E:\WeChatBot\src\backend\WeChatBot.Backend\WeChatBot.Backend.csproj package --include-transitive --vulnerable
```
