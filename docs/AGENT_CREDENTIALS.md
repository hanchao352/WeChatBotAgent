# Agent 独立凭据

## 目标与边界

每个 Windows Agent 由管理员预注册并获得一条独立、高熵的控制面凭据。后端只保存该凭据的 SHA-256 小写十六进制摘要，不保存明文，也不把摘要写入逻辑备份、列表响应、审计详情、日志或 Agent 本地状态。微信 `4.1.11.55` 的 UIA 兼容性仍未达标，Agent 继续强制 `dry-run`，本功能不会使真实备注或消息修改变为可用。

## 生命周期接口

| 操作 | 方法与路径 | 结果 |
| --- | --- | --- |
| 首次预注册并签发 | `POST /api/agents` | `201`，只返回一次 `credential` 和不含敏感字段的 Agent 视图 |
| 安全列表 | `GET /api/agents` | `200`，只返回 `hasCredential`、签发/轮换/吊销时间等状态 |
| 轮换 | `POST /api/agents/{registrationId}/credential/rotate` | `200`，要求 `expectedVersion`，只返回一次新凭据 |
| 吊销 | `POST /api/agents/{registrationId}/credential/revoke` | `200`，要求 `expectedVersion`，清除摘要并使凭据立即失效 |

重复完全相同的 `POST /api/agents` 不会重放旧凭据，而是返回 `agent_already_registered`。轮换和吊销使用 `AgentRegistration.Version` 乐观并发控制；并发请求最多一个成功，其他请求返回 `concurrency_conflict`。历史心跳作为遥测保留，但轮换时间会建立新的凭据会话边界，吊销或凭据为空会立即显示离线；新的凭据必须重新发送健康 `dry-run` 心跳。

## 认证绑定

Agent 通过 `X-Api-Key` 发送独立凭据。认证处理器计算摘要并在当前租户数据库中查询启用且未吊销的 `AgentRegistration`，随后写入以下 claim：

- `agent_registration_id`
- `agent_id`
- `wechat_instance_id`
- `tenant_id`

群消息和备注任务 `claim`、`renew`、`release`、`complete` 端点都调用统一身份绑定服务，要求认证 claim、路由 `agentId`、正文 `weChatInstanceId`、当前注册记录和租户完全一致。业务写事务先取得 SQLite 写锁再复核身份，因此轮换或吊销不能插入复核与写入之间；事务提交后旧凭据的新请求会在认证层返回 `401`。

## 兼容开关

`Auth__AgentApiKey` 只作为迁移期共享凭据；必须同时设置 `Auth__AllowLegacySharedAgentApiKey=true` 才会读取。该模式不能密码学绑定具体设备：任一共享 Key 持有者只要知道某条注册的 `AgentId` 和 `WeChatInstanceId`，就能以该注册提交心跳及兼容业务请求。此风险由自动化测试固定记录，仅允许 Development/Testing 的旧测试迁移。Production 启动时若开关为 `true` 会 fail-fast，生产默认值为 `false`。旧共享路径不得用于真实外部修改。

## 备份与恢复

逻辑备份 schema v5 使用无凭据的 Agent 注册 DTO；`CredentialHash`、`CredentialIssuedAt`、`CredentialRotatedAt` 和 `CredentialRevokedAt` 均不进入载荷。恢复旧 schema v1-v4 或 v5 时：

1. 先在恢复事务内清除当前租户所有现存注册摘要并写入吊销时间；旧心跳只作为遥测保留，不再证明在线或具备租约资格。
2. 恢复的注册只带元数据，凭据字段为空且标记为已吊销。
3. 管理员必须通过轮换接口重新签发，Agent 必须重新建立健康心跳。

备注任务的活动租约仍按 schema v4 规则剥离；schema v5 只是补充凭据脱敏约束。备份文件即使被解密检查，也不应出现明文凭据或 `credentialHash` 字段。

## Agent 配置

推荐使用：

```powershell
$env:WECHATBOT_AGENT_CREDENTIAL = '从密钥管理系统读取的单 Agent 凭据'
dotnet run --project src/agent/WeChatBot.Agent -- --run --agent-credential=$env:WECHATBOT_AGENT_CREDENTIAL --heartbeat-uri=https://control.example/api/agents/heartbeat
```

`--agent-credential` 和 `WECHATBOT_AGENT_CREDENTIAL` 是当前名称。旧的 `--control-plane-api-key` / `WECHATBOT_AGENT_CONTROL_PLANE_API_KEY` 仍可作为迁移别名，但已弃用；新名称同时存在时优先使用新名称。凭据只进入 `X-Api-Key` 请求头，不进入 JSON、URL、日志、异常文本或序列化对象。

## 运维检查

- 预注册响应离开安全通道后不可恢复；丢失凭据必须轮换。
- 轮换前确认 Agent 已停止或能处理短暂心跳拒绝；轮换后必须重新心跳。
- 吊销用于设备遗失、凭据泄露或退役；吊销后先核对审计，再决定是否重新启用和轮换。
- 不要把任何凭据写入 Git、示例配置、备份、截图或工单日志。
