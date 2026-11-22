# 123｜NOTE-04b-2b-b-a：桌面拉取应用笔记变更

状态：代码已实现；本地 core `cargo test`（笔记 6 项）与 clippy/fmt 通过。UI 切换到本地笔记/离线编辑属 `NOTE-04b-2b-b-b`。

## 范围与验收

- **同步拉取**：`sync.rs` 的 pull 过滤加入 `note`；`Store::apply_pull` 新增 `apply_note_pull`：
  - upsert 解码服务端 note 视图（`id/title/content/task_id/version/created_at/updated_at`），按 `(id)` upsert；`task_id` 缺失或 null 安全；
  - delete 使用 `deleted_at`（缺失回退 `updated_at`）写 tombstone；本地无该行时也能插入 tombstone；
  - 本地版本 ≥ 变更版本 → `skipped_stale`，不覆盖本地较新数据。
- **模型**：`StoredNote` 增加 `created_at`，`task_id/created_at/deleted_at` 带 serde 默认值，兼容旧 payload。
- **测试**（新增 2 项，合计笔记 6 项）：远端 upsert 应用 + 旧版本跳过；upsert→delete tombstone 后 `notes()` 为空。

## 语义与边界

- 桌面笔记 UI **仍走在线 API**；本切片只让本地表接收服务端 note 变更，尚未切换读写路径。
- 本地存在未推送的 note 编辑时，拉取按版本比较；与服务端同版本冲突由后续 UI 冲突处理（当前无 note 冲突 UI）。
- `created_at` 仅用于存储，未在 UI 展示；附件/模板未实现。

## 后续

- `NOTE-04b-2b-b-b`：笔记 UI 切换到本地 store/outbox、冲突提示、离线编辑与关联任务展示。
