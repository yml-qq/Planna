# 124｜NOTE-04b-2b-b-b：桌面笔记 UI 切换到本地存储

状态：代码已实现；本地 `pnpm test`（88 项）、`pnpm build`、shell `cargo check/clippy/fmt` 通过。CI 待网络恢复，见 `VALIDATION.md`。

## 范围与验收

- **Tauri 命令**：`notes_local`、`note_local_create`、`note_local_update`、`note_local_delete`，使用本地 SQLite Store 与 outbox（不访问 `/notes` API）。
- **桥接切换**：`TauriBridge.noteList/Create/Update/Delete` 改为调用本地命令；列表搜索/任务过滤在本地列表上客户端完成。
- **离线可用**：创建/编辑/删除只写本地并排队 note mutation；`syncNow` 后刷新任务/习惯/目标/笔记/专注历史，拉取会把服务端 note 变更应用到本地表。
- **测试**：前端 **88 项**保持通过（Memory 桥接行为不变）；core 笔记 6 项测试覆盖本地存储、outbox 与拉取应用。

## 语义与边界

- **仅登录后可见**：笔记 UI 仍要求已登录；未登录不展示，但本地数据保留。
- 笔记在线 API 仍存在（其他客户端/测试用）；桌面不再直接调用，服务端变更经 sync pull 到达。
- 本地 note 推送冲突暂无专门 UI：走现有 outbox 失败/冲突提示，未提供“以服务端为准/重试”的 note 专用处理。
- 关联任务在本地仅存 id；任务被删/未同步时不显示标题（列表逻辑沿用）。
- 附件/模板、离线删除恢复未实现；搜索为本地子串匹配（非全文索引）。

## 后续

- `NOTE-04b-2b-b-c`：note 冲突 UI、附件/模板、全文搜索与任务详情内笔记。
