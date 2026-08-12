# WeChatBot 管理端

React 19 + TypeScript + Vite 管理控制台，覆盖执行端状态、联系人/群、自动备注任务、群 `@` 事件、通用服务权益、激活、备份恢复和审计。

## 启动

先启动 `http://127.0.0.1:5188` 的后端，然后运行：

```powershell
npm install
npm run dev
```

打开 `http://127.0.0.1:5173`。开发服务器会代理 `/api` 和 `/health` 到后端；开发 API Key 由 `src/api.ts` 在 Vite 开发模式下提供。`VITE_API_KEY` 只能用于受控开发环境，因为 Vite 变量会进入静态资源。生产部署必须改用 OIDC/MFA 与服务端 BFF/session，不能向浏览器分发管理员 API Key。

后端不可用时页面会明确显示“本地演示”，演示操作不会写入数据库或微信。

## 验证

```powershell
npm run lint
npm run build
```

备份恢复页面对应后端当前库的受控合并恢复，不是隔离环境恢复。执行恢复会先创建恢复前备份，并在完成后保持自动化暂停。
