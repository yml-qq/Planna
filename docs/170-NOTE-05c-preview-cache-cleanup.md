# 170｜NOTE-05c-preview-cache-cleanup：预览复用缓存与临时文件清理

状态：代码已实现；本地前端 `pnpm test`（105 项）、`pnpm build`、Rust `cargo check/clippy/fmt` 通过；CI 见 `VALIDATION.md`。

## 范围与验收

- **预览缓存**：若该附件已有 Blob URL 或文本缓存，`previewNoteAttachment` 直接返回，不再请求字节；删除/卸载仍清理。
- **临时文件清理**：`note_attachment_open` 写入前扫描 `temp/planna-attachments`，删除修改时间超过 **24 小时** 的文件；清理失败忽略，不阻断打开。
- 外部打开仍复用签名校验与安全文件名。

## 语义与边界

- 缓存期内无法主动刷新预览；附件内容在服务端变更（同 ID 理论上不可变）时仍显示旧缓存，直到删除/重启。
- 文本/图片 Blob URL 缓存只在内存；重启后重新拉取。
- 临时文件最多保留 24 小时；仅在**下次外部打开**时清理，不打开就不清理；不清理其他进程/目录文件。
- 清理基于修改时间，系统时钟异常可能误删/保留；无文件系统测试。
- 未做 LRU/大小上限，缓存随查看附件数增长。

## 后续

- `NOTE-05c-preview-lru`：LRU/大小上限、手动刷新、启动时清理与设备验证。
