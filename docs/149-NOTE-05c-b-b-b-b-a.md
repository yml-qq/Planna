# 149｜NOTE-05c-b-b-b-b-a：限流响应 Retry-After

状态：代码已实现；本地 build/format/契约 **36 项**通过；CI 见 `VALIDATION.md`。

## 范围与验收

- 附件上传触发限流（429 `ATTACHMENT_RATE_LIMITED`）时，响应同时返回 `Retry-After: 60`（秒）。
- OpenAPI 上传 429 改为内联响应并声明 `Retry-After` header；契约测试断言 header 存在。
- 冒烟在第三次上传 429 时校验 `Retry-After == "60"`。

## 语义与边界

- 固定 60 秒，等于固定窗口长度；未按剩余窗口时间动态计算。
- 仅附件上传限流返回该 header；其他 429（如 AI 限流）不变。
- 客户端（桌面附件面板）尚未读取/展示 `Retry-After`，仅服务端契约。
- 限流仍为进程内/固定窗口；多实例各自计时。

## 后续

- `NOTE-05c-b-b-b-b-b`：动态 Retry-After、分布式限流与客户端退避提示。
