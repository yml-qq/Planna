# 65｜DESK-01F：桌面登录 UI 与会话生命周期

状态：代码已实现；本地 `pnpm test`（12 项）与 `pnpm build`（vue-tsc + vite）通过，桌面 CI 见 `VALIDATION.md` 的 DESK-01F 小节。安装包/签名、后台调度与服务端登出撤销属 `DESK-01G`。

## 范围与验收

- **桥接层扩展**（`src/api.ts`）：`DesktopBridge` 增加 `requestLoginCode`、`login`、`restoreSession`、`signOut`、`runSync`；`MemoryBridge` 提供浏览器/单测实现（验证码 `123456`、内存会话、空同步结果），`TauriBridge` 映射到既有/新增 Rust 命令（snake_case 字段转换）。
- **会话 store**（`src/session.ts`）：
  - 状态 `signed_out/signed_in`、内存 access token、`busy/error/requested/lastSync`；
  - `requestCode`（邮箱格式校验）、`signIn`（6 位数字码校验，成功按 `expiresIn` 提前 60s 调度刷新）、`restore`（启动时用 OS 凭据库 refresh token 换新 access token，失败回落未登录）、`signOut`（取消定时器 + 本地清除）、`sync`（未登录返回 `NOT_SIGNED_IN`，成功写 `lastSync`）；
  - 定时器可注入（测试不真实等待），autoRefresh 可关闭。
- **页面**（`src/App.vue`）：未登录显示服务器地址/邮箱/验证码登录面板；登录后显示「立即同步」「退出」与同步摘要；继续支持新增/查看任务；服务器地址与设备 ID 存 `localStorage`（非机密），access token 只在内存。
- 测试（12 项，新增 6 项会话）：邮箱校验与验证码请求、登录校验与提前刷新调度（`3_540_000ms`）、定时触发 `restoreSession` 轮换 token、登出取消定时器、启动恢复成功/失败、未登录/失败同步错误映射。

## 实现和部署

`DESK-01E` 的 Rust 命令已满足需要，本切片未改 Rust 与服务端契约。`localStorage` 仅存 base URL 与随机设备 ID；refresh token 仍只在 OS 凭据库。

## 边界与后续

- `DESK-01G`：后台/网络恢复自动同步调度（当前为手动按钮 + access token 到期刷新）、服务端会话撤销登出（需设备版本）、`tauri build` 安装包、签名与自动更新、真机窗口冒烟。
- 浏览器模式使用内存桥，不代表已连服务端；HTTP 失败提示为通用文案。
