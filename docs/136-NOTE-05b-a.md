# 136｜NOTE-05b-a：Tauri 附件命令层

状态：代码已实现；本地前端 `pnpm test`（90 项）、`pnpm build`、shell `cargo check/clippy/fmt` 通过。CI 见 `VALIDATION.md`。附件 UI 面板与同步策略属 `NOTE-05b-b`。

## 范围与验收

- **Tauri 命令**（调用现有 `/notes/{id}/attachments` 在线 API）：
  - `note_attachment_list`：GET 列表；
  - `note_attachment_upload`：选择本地文件路径 → 按扩展名映射 MIME（txt/md/png/jpg/jpeg/pdf）→ multipart 上传（`Idempotency-Key` 随机 UUID）；
  - `note_attachment_download`：GET 原字节 → 原生“另存为”对话框 → 写文件，取消返回 `null`；
  - `note_attachment_delete`：DELETE（软删）。
- `reqwest` 增加 `multipart` feature；复用 dialog 插件。
- **桥接**：`NoteAttachment` 类型 + 4 个方法；Memory 实现模拟上传/列表/下载/删除与不支持类型 415。
- 测试：前端新增 1 项（上传/列表/下载/删除/不支持类型），合计 **90 项**。

## 语义与边界

- 附件**只走在线 API**，不进入本地 SQLite/同步；离线或笔记尚未 push 到服务端时会得到 401/404。
- 上传前不校验文件大小/真实 MIME，仅按扩展名映射；最终由服务端白名单/5MiB 限制拒绝。
- 下载文件名固定 `attachment.<ext>`，不使用服务端 `Content-Disposition` 文件名。
- 无 UI 面板（下一片）；无病毒扫描/配额；无附件缓存。

## 后续

- `NOTE-05b-b`：笔记卡片附件面板（列表/上传/下载/删除）、离线/未同步提示与同步策略。
