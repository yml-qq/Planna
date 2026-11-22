# 109｜FOCUS-09b-a：专注历史游标分页 API

状态：代码已实现；本地 build/format/契约 **34 项**通过，CI 见 `VALIDATION.md` 的 FOCUS-09b-a 小节。桌面分页消费与报表属 `FOCUS-09b-b`。

## 范围与验收

- **契约**：`GET /focus-sessions?limit&task_id&cursor`：
  - `cursor` 为不透明 base64url（`started_at|id`），由上一页 `next_cursor` 返回；客户端不得解析；
  - 排序 `started_at DESC, id DESC`，按 `(started_at,id)` 严格小于游标继续取；`limit` 1..100；
  - 响应 `{items, next_cursor?}`：仅当本次返回满 `limit` 时给 `next_cursor`（可能下一页为空，客户端以空页终止）；
  - 非法 cursor → 400 `INVALID_CURSOR`；非法 limit → 400 `INVALID_LIMIT`；任务过滤与 404 语义沿用 FOCUS-09a。
- **实现**：`FocusSessionService.ListAsync` 增加游标解码/编码与条件 SQL；`FocusSessionList` 新增可选 `next_cursor`。
- **OpenAPI/契约**：列表新增 `cursor` 参数、`FocusSessionList.next_cursor`（nullable，≤256）；契约测试断言参数与 schema。
- **冒烟** `HttpFocusPageSmoke`：3 条会话 limit=2 → 2 条 + cursor；续页 1 条 + `next_cursor=null`；非法 cursor 400 `INVALID_CURSOR`；limit=0 400；未认证 401。

## 语义与边界

- 游标只编码时间与 id，不含 owner；跨 owner 使用他人游标只会按时间过滤自己的数据，不泄露内容。
- 无快照隔离：分页期间新写入的较新会话会在后续页之前出现（keyset 分页的常见语义）。
- 若恰好最后一页条数等于 limit，仍会返回 cursor，下一次请求返回空页；客户端需以空页停止。

## 后续

- `FOCUS-09b-b`：桌面“加载更多”接入 `next_cursor`、周/月报表导出、后台通知真机验证。
