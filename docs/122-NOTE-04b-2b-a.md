# 122｜NOTE-04b-2b-a：桌面本地笔记存储与同步 outbox

状态：代码已实现；本地 core `cargo test`（笔记 4 项）与 clippy/fmt 通过；[Server integration 45026244524](https://github.com/yml-qq/Planna/actions/runs/45026244524) 与 [Desktop core 45026244527](https://github.com/yml-qq/Planna/actions/runs/45026244527) 均 **success**。拉取应用与 UI 切换到本地笔记属 `NOTE-04b-2b-b`。

## 范围与验收

- **SQLite 表** `notes`：`id/title/content/task_id/version/created_at/updated_at/deleted_at`；无外键（task 可在之后同步），删除为软删。
- **Store API**：
  - `create_note(NewNote, mutation_id, now)`：任务+outbox 同一事务；`entity_type="note"`、`base_version=0`、payload `{title,content,task_id}`；
  - `update_note(note_id, NotePatch, expected_version, mutation_id, now)`：版本守卫，合并标题/正文，payload 发送完整标题/正文/关联；
  - `delete_note(note_id, expected_version, mutation_id, now)`：软删 + `op="delete"`、`base_version=旧版本`、payload `{}`；
  - `notes()`：未删除、按 `updated_at DESC`。
- **校验**：标题 ≤200、正文 ≤20000、两者不能同时为空；mutation_id/now 必填；重复 mutation_id 幂等返回 `false`。
- **测试**（新增 4 项）：创建行+outbox 原子性与 payload、重复 mutation 幂等、更新版本冲突/成功与 base_version、删除 tombstone、空/超长拒绝且不写 outbox。

## 语义与边界

- 这些本地笔记**尚未接入 UI**：桌面笔记页仍走在线 API；本切片只保证本地存储与 outbox 可为后续离线编辑/同步使用。
- 拉取仍只应用 task/habit/habit_log，**忽略 note 变更**；服务器笔记不会进入本地表（`NOTE-04b-2b-b`）。
- 删除/更新都不支持改 `task_id`（NotePatch 只有标题/正文）；关联在创建时固定。
- SQLite 无 task 外键，任务不存在时仅由服务端同步时拒绝。

## 后续

- `NOTE-04b-2b-b`：pull 应用 note 变更、桌面笔记 UI 切换到本地 store/outbox、离线编辑与关联任务展示。
