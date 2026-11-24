# 164｜NOTE-05c-b-b-b-b-b-b-b-b-b-b-a：文本/Markdown 附件预览

状态：代码已实现；本地前端 `pnpm test`（104 项）、`pnpm build`、Rust `cargo check/clippy/fmt` 通过；CI 见 `VALIDATION.md`。

## 范围与验收

- Tauri `note_attachment_bytes` 的允许类型从仅 `image/*` 扩展为 `text/plain`、`text/markdown`（仍限 5 MiB）。
- `canPreviewAttachment` 覆盖 PNG/JPEG/text/markdown；新增 `isTextAttachment`。
- UI：文本类型走字节读取 → `TextDecoder` 解码 → `<pre class="attachment-preview-text">` 展示；图片仍走 Blob URL。
- 预览清理（再次预览/删除附件/删除笔记/卸载）同时清理文本缓存。
- 前端测试仍 **104 项**（预览类型断言更新）。

## 语义与边界

- 文本按 UTF-8 解码；非 UTF-8 文件可能乱码（不检测编码）。
- 以纯文本渲染（Vue 插值转义），**不解析 Markdown/HTML**，避免 XSS；无语法高亮。
- 仅 text/plain、text/markdown；PDF/Office 仍不可内嵌预览。
- 文本内容保留在内存 map 直到清理；5 MiB 上限，无分页/搜索。
- 无 UI/DOM 测试和真实 Tauri WebView 验证。

## 后续

- `NOTE-05c…-b-b-b`：PDF 预览、编码检测、Markdown 渲染（安全）与设备验证。
