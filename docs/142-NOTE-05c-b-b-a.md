# 142｜NOTE-05c-b-b-a：并发上传配额验证与单文件上限配置

状态：代码已实现；本地 build/format/契约 **36 项**通过；并发冒烟在 CI 中执行，结果见 `VALIDATION.md`。

## 范围与验收

- **单文件上限可配**：`NOTE_ATTACHMENT_MAX_BYTES`（默认 5 MiB，1..100 MiB）；非法值回退默认；原 5 MiB 常量移除。
- **并发配额冒烟**：
  - 新 owner + 笔记，设置 `NOTE_ATTACHMENT_MAX_PER_NOTE=5`，并发发起 10 个上传；
  - 断言恰好 5 个 201、5 个 422，DB 活跃附件恰好 5 条（验证 advisory lock 串行化配额检查）。
- **可配置大小冒烟**：设置 `NOTE_ATTACHMENT_MAX_BYTES=3`，上传 5 字节文件 → 413。
- 原配额测试（20 条/200 MiB）保持通过；env 在用例后清理。

## 语义与边界

- 并发测试依赖同一进程内多个 WebApplicationFactory 与真实 PostgreSQL；未覆盖多进程/多实例竞争（依赖 DB advisory lock，理论上仍生效）。
- 文件大小上限仍按请求 `Content-Length`/IFormFile.Length 检查，不流式截断；配置回退不告警。
- 未做速率限制、病毒扫描、预览或上传进度。
- 并发请求可能导致测试耗时增加（锁排队），CI 观察实际时长。

## 后续

- `NOTE-05c-b-b-b`：上传进度/预览、病毒扫描、速率限制与多实例并发验证。
