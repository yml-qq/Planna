# 167｜NOTE-05c-preview-sniffing：预览内容签名校验

状态：代码已实现；本地前端 `pnpm test`（105 项）、`pnpm build`、Rust `cargo test`（4 项）/clippy/fmt 通过；[Server integration 45422298194](https://github.com/yml-qq/Planna/actions/runs/45422298194) 与 [Desktop core 45422304209](https://github.com/yml-qq/Planna/actions/runs/45422304209) 均 **success**。

## 范围与验收

- `note_attachment_bytes` 在返回字节前做**内容签名**校验：
  - PNG：`89 50 4E 47 0D 0A 1A 0A`；JPEG：`FF D8 FF`；PDF：`%PDF-`；
  - text/plain、text/markdown：必须是合法 UTF-8；
  - 其他类型拒绝。
- 不匹配返回 `415:ATTACHMENT_CONTENT_MISMATCH`；客户端提示“文件内容与类型不符，已阻止预览”。
- 顺带修复 `sanitize_report_filename` 对长 `.json` 名称截断后错误补 `.csv` 的问题（现在保留扩展名）。
- Rust 单测 3→**4 项**（签名/UTF-8/未知类型 + JSON 文件名截断）；前端仍 105 项。

## 语义与边界

- 仅**预览路径**校验；服务端上传仍按声明 Content-Type 白名单存储，下载/其他客户端不受此保护。
- 魔数只验证头部，不证明文件安全/完整；文本仅验证 UTF-8，不扫描恶意内容。
- PDF 通过签名后仍用 WebView 内嵌渲染，PDF 内脚本/漏洞依赖 WebView 缓解，未沙箱化。
- 无病毒扫描/AV、无深度解析、无服务端签名校验。
- 无 UI/设备测试。

## 后续

- `NOTE-05c-server-content-check`：服务端上传魔数校验、病毒扫描与 PDF 渲染沙箱/外开降级。
