# 63｜DESK-01D：离线同步引擎与桌面 HTTP 传输

状态：代码已实现；本地 `cargo test`（core 16 项）、`cargo fmt/clippy/check`（core 与 shell）通过；[Desktop core 43105576627](https://github.com/yml-qq/Planna/actions/runs/43105576627)（三个作业）与 [Server integration 43105576757](https://github.com/yml-qq/Planna/actions/runs/43105576757) 均 **success**。登录流程、自动重试调度与安装包签名属 `DESK-01E`。

## 范围与验收

- **core `sync` 模块**（无网络依赖，传输可注入）：
  - `SyncTransport` 的 `push`/`pull` 由外壳实现；`sync_once(store, transport, device_id, max_pull_pages)` 先取最多 100 条 outbox → push → 按结果处理（`accepted` 删除、`duplicate` 删除并计数、`conflict`/`rejected` 保留并 `record_failure`）→ 逐页拉取（`cursor` 不透明字符串、`has_more` 上限保护）→ `apply_pull`（版本单调/墓碑）→ 持久化 `next_cursor`。
  - outbox 增加 `base_version` 列（本地 SQLite 旧库启动时幂等补列），push payload 使用 `base_version` 与本地版本一致。
  - 传输失败时立即返回且**不丢 outbox**；非 task 实体的拉取变更被忽略。
- **src-tauri**：新增 `HttpTransport`（reqwest blocking + rustls；`POST /sync/push` 带 Bearer、`GET /sync/pull?limit&cursor`）与命令 `run_sync(base_url, access_token, device_id)`，返回 `SyncOutcome`（pushed/duplicates/conflicts/rejected/pulled/skipped_stale/cursor）。
- 测试（7 项）：push 成功后清空 outbox 并应用 pull；duplicate 删除不重试；conflict 保留并计次；传输失败不丢 outbox 且不 pull；多页游标推进与旧版本跳过；墓碑应用与非 task 忽略；空 outbox 仍 pull 且遵守页预算。
- 验证：本地 `cargo test`（store 9 + sync 7）、`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、shell `cargo check` 均通过。

## 实现和部署

无服务端契约变更（复用既有 `/sync/push`、`/sync/pull`）。访问 token 由调用方（后续登录流程）传入，不落盘；base URL 由命令参数提供，便于本地/测试环境。

## 边界与后续

- `DESK-01E`：登录/refresh 与 token 安全存储、outbox 自动重试调度（指数退避/前台触发）、冲突 UI、`tauri build` 安装包与签名。
- `run_sync` 目前为手动命令；服务器 `CURSOR_EXPIRED` 的重新 bootstrap 流程待接入。
