# 60｜DESK-01A：桌面端离线核心（SQLite + outbox）

状态：代码已实现；本地 `cargo fmt/clippy/test` 通过，桌面 CI 见 `VALIDATION.md` 的 DESK-01A 小节。Tauri 外壳与 Vue UI 仍未实现（`DESK-01B`）。

## 范围与验收

- 新增 `apps/desktop/core`（Rust crate `planna_desktop_core`，rusqlite `bundled`）：本地 SQLite 作为 UI 单一事实源，schema 覆盖 `task_lists/tasks/outbox/sync_state/local_meta`，启用 `foreign_keys` 与 WAL。
- **原子写**：`create_task`/`update_task` 在同一事务内写本地任务与 outbox（`INSERT ... ON CONFLICT` 任务 + outbox 行）；相同 `mutation_id` 重放幂等（返回 `false`，不重复写）；非法标题、非法 all-day/时间组合、未知列表（FK）或版本冲突时整体回滚，不留下 outbox。
- **版本与冲突**：本地更新要求 `expected_version`，成功推进本地版本并把新版本写入 outbox payload；过期版本返回 `Conflict { server_version }`。
- **拉取应用**：`apply_pull` 按实体版本单调应用任务 upsert/delete（旧版本跳过并计入 `skipped_stale`），删除写墓碑；`sync_state` 持久化 `pull_cursor`（revision/change_index），文件库重开后仍保留。
- 测试（9 项）：原子写、幂等重放、校验回滚、FK 回滚、版本冲突、outbox 失败计数与清理、拉取新旧版本/墓碑、游标往返、文件库重开持久化。
- CI：`.github/workflows/desktop.yml` 在 `windows-latest` 运行 `cargo fmt --check`、`cargo clippy -D warnings`、`cargo test`。

## 实现和部署

`apps/desktop/Cargo.toml` 为 workspace（当前仅 `core` 成员）；`.gitignore` 排除 `target/`、本地 `*.db*`、`node_modules/`、`dist/`。不引入 Tauri/Node 依赖，本切片可在无 WebView2/Node 的环境独立验证。

## 边界与后续

- `DESK-01B`：Tauri 2 外壳（Rust commands 暴露 Store）、Vue 3 + TypeScript + Vite UI、登录/同步客户端与 outbox 重试调度。
- 本切片不做冲突合并策略（服务端 `VERSION_CONFLICT` 时由 UI 选择重试/覆盖）；不做本地加密（SQLCipher 为评估项）。
