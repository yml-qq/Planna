# 157｜NOTE-05c-b-b-b-b-b-b-b-a：附件上传客户端退避提示

状态：代码已实现；本地前端 `pnpm test`（103 项）、`pnpm build`、shell `cargo check/clippy/fmt` 通过；[Server integration 45210236777](https://github.com/yml-qq/Planna/actions/runs/45210236777) 与 [Desktop core 45210236861](https://github.com/yml-qq/Planna/actions/runs/45210236861) 均 **success**。

## 范围与验收

- **Tauri 上传错误**：429 时读取 `Retry-After`，返回 `429:ATTACHMENT_RATE_LIMITED:<seconds>`（缺失/非法回退 60）；其他错误仍用 `focus_error`。
- **notes store/UI**：新增 `attachmentErrorMessage`：
  - `ATTACHMENT_RATE_LIMITED:42` → “上传过于频繁，请 42 秒后重试”；
  - 不支持类型/过大/空文件/数量或容量配额/笔记未同步 → 中文提示；
  - 未知 code 保留原文。
- 上传/下载/删除失败提示统一走该 helper。
- 前端测试 102→**103 项**（限流秒数、无秒数、类型、空、未知）。

## 语义与边界

- 仅**提示**，不自动重试、不启动倒计时/禁用按钮；用户需自行稍后重试。
- 服务端 `Retry-After` 为固定窗口剩余秒数；客户端只解析上传命令，下载/删除不受限流。
- 错误码仍以字符串传递（`429:CODE:SECONDS`），未改为结构化错误对象。
- 后台仍在单进程/DB 窗口限流；无客户端持久退避状态。

## 后续

- `NOTE-05c-b-b-b-b-b-b-b-b`：自动退避/倒计时、结构化错误对象与上传进度。
