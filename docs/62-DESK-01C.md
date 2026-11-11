# 62｜DESK-01C：Tauri 2 外壳与 Rust 命令层

状态：代码已实现；本地 `cargo check`、`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings` 通过；[Desktop core 43103429108](https://github.com/yml-qq/Planna/actions/runs/43103429108) 的 `Windows Tauri shell` 作业 **success**（同次 Rust core 与 Web UI 作业亦通过），[Server integration 43103429062](https://github.com/yml-qq/Planna/actions/runs/43103429062) **success**。登录/同步调度与安装包签名属 `DESK-01D`。

## 范围与验收

- `apps/desktop/src-tauri` 为 Tauri 2 外壳（独立 Cargo workspace，`[workspace]` 空表）：`tauri.conf.json`（窗口 880×640、CSP `default-src 'self'`、`frontendDist=../dist`、`beforeDevCommand=pnpm dev`/`beforeBuildCommand=pnpm build`）、`capabilities/default.json`（仅 `core:default`，不使用 FS/shell 插件权限）、生成的 PNG/ICO 图标。
- **Rust 命令层**（`src/lib.rs`）：`AppState { Mutex<Store> }` 在 `setup` 中打开 app data 目录下的 `planna.db` 并确保 `list-inbox` 存在；命令：
  - `list_tasks(listId)` → `TaskDto[]`（snake_case 字段与前端 `TauriBridge` 对齐）；
  - `create_task(id, listId, title)` → `TaskDto`（生成 `mutation_id`，本地任务与 outbox 同事务写入，重复 id 返回 `DUPLICATE_MUTATION`）；
  - `pending_mutations(limit)` → `OutboxDto[]`（供后续同步调度读取）。
- 前端 `TauriBridge` 与命令参数/返回字段已对齐；浏览器模式仍走 `MemoryBridge`。
- CI：`.github/workflows/desktop.yml` 新增 `desktop-shell`（windows-latest：fmt/clippy/check + rust-cache），与 core/web 作业并行。
- 测试/验证：本地 `cargo check` 2m08s 完成（Tauri 2.12、webview2-com 0.39），`cargo fmt/clippy` 0 问题；未在本机执行 `tauri build`/安装包与真机窗口冒烟。

## 实现和部署

图标由 Pillow 生成的 32/128/256/512 PNG 与多尺寸 ICO；`src-tauri/gen/` 为构建产物已忽略。应用数据目录由 Tauri `app_data_dir` 决定（测试/开发环境不写生产路径）。

## 边界与后续

- `DESK-01D`：登录/刷新 token 与同步客户端（调用服务端 auth/sync API）、outbox 重试调度与冲突 UI、`tauri build` 安装包、签名与自动更新。
- 当前命令层不做网络调用；`create_task` 固定 `time_zone=Asia/Shanghai`/`all_day=false`，编辑/完成/删除命令与列表管理待后续切片。
