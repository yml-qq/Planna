# 162｜NOTE-05c-b-b-b-b-b-b-b-b-a：图片附件预览

状态：代码已实现；本地前端 `pnpm test`（104 项）、`pnpm build`、Rust `cargo test`（3 项）/clippy/fmt 通过；CI 见 `VALIDATION.md`。

## 范围与验收

- **Tauri 命令** `note_attachment_bytes`：GET 附件内容；仅当响应 `Content-Type` 以 `image/` 开头才返回字节（否则 `400:UNSUPPORTED_ATTACHMENT_PREVIEW`）；字节数 >5 MiB → `413`。
- **桥接** `noteAttachmentBytes`（Memory 返回 8 字节 PNG 头）。
- **notes helper** `canPreviewAttachment`：仅 `image/png`、`image/jpeg` 显示预览按钮。
- **UI**：附件行新增“预览”按钮；按需拉取字节，生成 Blob URL 并显示 `<img class="attachment-preview">`；重复预览会 revoke 旧 URL；失败写入附件提示。
- 前端测试 103→**104 项**（预览类型判断）。

## 语义与边界

- 仅图片（PNG/JPEG）；PDF/文本无内嵌预览，需下载后用系统应用打开。
- 预览字节**不缓存/不持久化**，每次点击重新请求；无分页/缩放控件。
- Blob URL 只在同一附件再次预览时 revoke；**组件卸载/删除附件时未统一 revoke**（潜在内存泄漏）。
- 依赖响应头判断图片类型，未做魔数嗅探；服务端仍以白名单 Content-Type 存储。
- 无自动化 UI/DOM 测试；预览在真实 Tauri WebView 中的 Blob 行为未做设备验证。

## 后续

- `NOTE-05c-b-b-b-b-b-b-b-b-b`：PDF/视频预览、URL 生命周期清理、缓存与设备验证。
