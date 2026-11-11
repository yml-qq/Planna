# 64｜DESK-01E：同步重试/游标恢复与桌面登录

状态：代码已实现；本地 `cargo test`（core 20 项）、`cargo fmt/clippy/check`（core 与 shell）通过，桌面 CI 见 `VALIDATION.md` 的 DESK-01E 小节。登录界面、自动重试调度、服务端会话撤销与安装包签名属 `DESK-01F`。

## 范围与验收

- **core 同步可靠性**：
  - `TransportError { Retryable, CursorExpired, Fatal }`：网络/429/5xx 可重试，401/403 与协议错误终止，410 游标过期；
  - `sync_with_retry(..., max_attempts, backoff)`：仅重试 `Retryable`，退避由调用方注入（测试不 sleep）；
  - `CursorExpired` 时清空本地 `pull_cursor` 并从无游标重新拉取一次；连续过期返回错误；
  - `Store::clear_cursor` 暴露给外壳。
- **src-tauri 外壳**：
  - `HttpTransport` 按状态码分类（410→CursorExpired、429/5xx→Retryable、其他→Fatal）；`run_sync` 使用最多 3 次尝试与 0.5s/1s 退避；
  - 登录命令：`request_login_code(base_url, email, device_id)`（`POST /auth/login/challenges` + 幂等键 + `X-Device-Id`）、`login_with_code`（`POST /auth/login`，refresh token 写入 OS 凭据库 `keyring`，只向 UI 返回 access token 与 `expires_in`）、`restore_session`（读取凭据库并 `POST /auth/refresh`，401 时清除本地 refresh 并返回 `SESSION_EXPIRED`）、`sign_out_local`（清除本地 refresh）。
- 测试（core 新增 4 项，合计 20 项）：可重试失败两次后成功且退避序列 `[0,1]`；Fatal 不重试且保留 outbox；游标过期清空后重新拉取；连续过期返回错误。
- 验证：`cargo test`（store 9 + sync 11）、`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、shell `cargo check` 通过。

## 实现和部署

refresh token 仅存 OS 凭据库（`app.planna.desktop`/`refresh-token`），access token 只在内存；服务端播放/轮换语义沿用既有 `/auth/refresh`。`SIGN_OUT_LOCAL` 不撤销服务端会话（需设备列表版本信息），完整登出属 `DESK-01F`。

## 边界与后续

- `DESK-01F`：登录界面、access token 过期自动刷新、后台同步调度与网络恢复重试、`/auth/devices` 撤销/登出、`tauri build` 安装包与签名。
- `run_sync` 的阻塞退避在 Tauri 命令线程执行（MVP），后续改为异步调度器。
