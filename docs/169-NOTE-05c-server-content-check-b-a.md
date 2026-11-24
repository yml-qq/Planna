# 169｜NOTE-05c-server-content-check-b-a：附件外部打开（PDF 降级）

状态：代码已实现；本地前端 `pnpm test`（105 项）、`pnpm build`、Rust `cargo check/clippy/fmt` 通过；CI 见 `VALIDATION.md`。

## 范围与验收

- 新增 Tauri `note_attachment_open`：
  - 复用 `fetch_attachment`（鉴权、类型白名单、5 MiB、魔数/UTF-8 校验）；
  - 写入系统临时目录 `planna-attachments/<安全附件ID>.<ext>`（ID 仅保留 ASCII 字母数字/-）；
  - 按平台调用：Windows `cmd /C start`、macOS `open`、Linux `xdg-open`；返回本地路径。
- UI：PDF 附件行新增“外部打开”按钮，成功显示“已用系统应用打开：路径”，失败显示错误。
- PDF 预览仍保留 `<embed>`，外部打开作为 WebKitGTK 不渲染时的降级。

## 语义与边界

- 临时文件**不自动清理**，重复打开覆盖同名 UUID；敏感内容可能留在 temp。
- 用系统默认应用打开，外部应用的安全/沙箱不受 Planna 控制；不验证应用关联。
- 只对 PDF 显示按钮；图片/文本无外部打开入口。
- 异步 `spawn` 不等待查看器退出，无法确认打开成功（仅确认进程启动）。
- 无自动化 UI/设备测试；Linux/macOS 分支未在本机验证。
- 仍不替代病毒扫描/沙箱。

## 后续

- `NOTE-05c-server-content-check-b-b`：临时文件清理、全类型外开、打开结果探测与设备验证。
