# WeChatBot

Windows 微信 UI Automation 机器人首个可运行纵向切片。当前实现不含游戏包，范围包括联系人/群资料、自动备注任务、群 `@机器人` 事件、通用服务包与激活期限、管理后台、审计、备份和受控合并恢复。

## 当前状态

- 后端业务 API、SQLite 开发库、幂等与审计链路可运行。
- 管理端可连接真实 API；后端不可用时进入离线只读，保留最后一次成功快照并禁用所有写操作。
- Windows Agent 已具备环境诊断、安全门禁、串行命令、幂等日志、独立凭据和心跳客户端。
- 微信 `4.1.11.55` 的标准 UIA 树不暴露联系人和消息控件，因此当前 Agent 强制 `dry-run`，不会发送消息或修改备注。
- 这是一套开发基线，不应被描述为已达到商业生产上线条件。正式上线仍需完成混合识别适配、生产认证/RBAC、队列与 PostgreSQL、监控告警、压力测试和灰度验证。

详细范围见 [产品需求](docs/PRODUCT_REQUIREMENTS.md)，UIA 结论见 [兼容性报告](docs/UIA_COMPATIBILITY.md)，高突发设计见 [高突发架构](docs/HIGH_BURST_ARCHITECTURE.md)。

## 环境要求

- Windows 10/11 交互式桌面
- .NET SDK 10
- Node.js 20 或更高版本
- 已安装并人工登录的微信 Windows 客户端（只用于只读诊断）

## 启动

在第一个 PowerShell 窗口启动后端：

```powershell
dotnet run --project E:\WeChatBot\src\backend\WeChatBot.Backend\WeChatBot.Backend.csproj --launch-profile http
```

开发地址为 `http://127.0.0.1:5188/swagger`，开发 API Key 为：

```text
wechatbot-local-development-key-change-me
```

Agent 凭据由管理员调用 `POST /api/agents` 首次签发；明文只在该响应出现一次，不与管理员凭据或其他 Agent 共用。完整流程见 [Agent 独立凭据](docs/AGENT_CREDENTIALS.md)。

在第二个 PowerShell 窗口启动管理端：

```powershell
Set-Location E:\WeChatBot\src\admin-web
npm install
npm run dev
```

打开 `http://127.0.0.1:5173`。Vite 会把 `/api` 和 `/health` 代理到后端。

运行 Agent 只读诊断：

```powershell
dotnet run --project E:\WeChatBot\src\agent\WeChatBot.Agent -- --diagnose
```

兼容性自检通过后，可用环境变量接入本地心跳接口：

```powershell
$env:WECHATBOT_AGENT_CREDENTIAL = '<POST /api/agents 返回的一次性凭据>'
dotnet run --project E:\WeChatBot\src\agent\WeChatBot.Agent -- --run --dry-run --heartbeat-uri=http://127.0.0.1:5188/api/agents/heartbeat --supported-version-prefixes='4.x.y-tested' --required-automation-id-fingerprints='structural:sha256=FULL_64_HEX_1,structural:sha256=FULL_64_HEX_2'
```

不要将微信版本加入执行白名单，除非它已完成受控兼容性验收。当前构建拒绝 `--dry-run=false`。

## 验证

```powershell
dotnet test E:\WeChatBot\tests\backend\WeChatBot.Backend.Tests\WeChatBot.Backend.Tests.csproj -c Release
dotnet test E:\WeChatBot\tests\agent\WeChatBot.Agent.Tests\WeChatBot.Agent.Tests.csproj -c Release

Set-Location E:\WeChatBot\src\admin-web
npm run lint
npm run build
```

## 恢复语义

当前 `/api/backups/{id}/restore` 不是隔离环境恢复。它会在当前租户数据库中执行受控合并：先校验并创建恢复前备份，覆盖联系人、群和备注规则配置，只补充缺失的权益/兑换/流水事实，并强制暂停自动化。生产环境应在独立数据库完成恢复演练后再批准切换。
