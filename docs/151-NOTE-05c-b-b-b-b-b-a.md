# 151｜NOTE-05c-b-b-b-b-b-a：动态 Retry-After

状态：代码已实现；本地 build/format/契约 **36 项**通过；CI 见 `VALIDATION.md`。

## 范围与验收

- 429 的 `Retry-After` 从固定 60 改为**当前窗口剩余秒数**（向上取整，至少 1 秒）。
- `NoteAttachmentResult` 增加可选 `RetryAfterSeconds`；服务在限流时返回剩余时间；端点写入 header。
- 冒烟断言 `Retry-After` 为 1..60 的整数。
- OpenAPI header 描述改为“固定一分钟窗口的剩余秒数”。

## 语义与边界

- 仍是**固定窗口**（非滑动/令牌桶），窗口重置后剩余时间回到 60。
- 进程内窗口：多实例各自计算剩余时间；时钟回拨可能产生异常值（`Math.Max(1, ...)` 兜底）。
- 仅附件上传；客户端尚未消费该 header。
- 无服务端 `Date`/`Retry-After` 时区语义问题（秒数）。

## 后续

- `NOTE-05c-b-b-b-b-b-b`：分布式限流、滑动窗口、客户端退避与告警。
