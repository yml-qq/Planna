# 66｜DESK-01G：后台同步调度与服务端登出撤销

状态：代码已实现；本地 `pnpm test`（14 项）、`pnpm build`、`cargo fmt/clippy/check` 通过，桌面 CI 见 `VALIDATION.md` 的 DESK-01G 小节。安装包/签名、真机冒烟与网络恢复触发属 `DESK-01H`。

## 范围与验收

- **后台同步调度**（`src/session.ts`）：登录/恢复成功后按 `syncIntervalMs`（默认 5 分钟，可注入 `scheduleInterval`）启动周期同步，登出/恢复失败时取消；`sync()` 在 busy 时跳过，避免与手动同步重叠；可用 `autoSync: false` 关闭。
- **服务端登出撤销**（`src-tauri`）：新增 `sign_out_server(base_url, access_token)`：`GET /auth/devices?limit=50` 找到 `is_current` 会话 → `POST /auth/devices/{id}/revoke`（`If-Match: "v{version}"` + 新幂等键）→ 成功后清除 OS 凭据库 refresh token；无当前设备时仅清除本地 token。前端 `signOut(baseUrl, accessToken)` 有 token 走服务端撤销，无 token 退回 `sign_out_local`。
- 测试（14 项，新增 2 项）：登录后创建 300s 后台同步并触发一次同步、登出取消定时器；`autoSync:false` 不创建定时器。既有会话/任务测试保持通过。
- 验证：`pnpm test` 14 passed、`pnpm build`（vue-tsc + vite）通过；`cargo check`/`clippy -D warnings`/`fmt` 通过。

## 实现和部署

登出撤销复用既有 `/auth/devices`、`/auth/devices/{id}/revoke` 契约，无服务端变更。周期同步仅在 access token 存在时运行；access token 仍由既有提前刷新定时器轮换。

## 边界与后续

- `DESK-01H`：`tauri build` 安装包（NSIS/WiX）与签名/自动更新、网络恢复（online 事件）触发同步、后台节流策略、真机窗口与安装冒烟；安装包需要发布密钥，不能提交仓库。
- 后台同步为前台进程内定时器（应用退出即停止），非 OS 级后台任务。
