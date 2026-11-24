# 165｜NOTE-05c-b-b-b-b-b-b-b-b-b-b-b-a：PDF 附件预览

状态：代码已实现；本地前端 `pnpm test`（104 项）、`pnpm build`、Rust `cargo check/clippy/fmt` 通过；[Server integration 45413640500](https://github.com/yml-qq/Planna/actions/runs/45413640500) 与 [Desktop core 45413640205](https://github.com/yml-qq/Planna/actions/runs/45413640205) 均 **success**。

## 范围与验收

- 字节通道允许 `application/pdf`（≤5 MiB），与图片/文本共用 `note_attachment_bytes`。
- `canPreviewAttachment` 增加 PDF；新增 `isPdfAttachment`。
- UI：PDF 生成 Blob URL 后用 `<embed type="application/pdf">` 预览（480px 高）；图片仍用 `<img>`，文本仍用 `<pre>`。
- 预览清理逻辑不变（再次预览/删除/卸载）。
- 前端测试保持 **104 项**（类型断言更新）。

## 语义与边界

- PDF 渲染依赖 **WebView 的 PDF 支持**：Windows WebView2 通常内置，Linux WebKitGTK 可能不显示；未提供降级提示/外部打开按钮（仍可下载）。
- 仅按响应 `Content-Type` 判断；未做魔数嗅探。
- 无 PDF 页码/缩放/搜索控制；Blob URL 生命周期与图片一致。
- 无自动化 UI/DOM 测试；未在真实 Tauri 设备验证 PDF 渲染。
- 5 MiB 上限，无分页流式加载。

## 后续

- `NOTE-05c…-b-b-b-b`：PDF 渲染降级/外开、编码检测、安全 Markdown、自动退避/倒计时与病毒扫描。
