# 87｜FOCUS-04：专注统计聚合 API

状态：代码已实现；本地 `dotnet build --warnaserror`、`dotnet format`、契约测试 29 项通过，PostgreSQL 18 CI 见 `VALIDATION.md` 的 FOCUS-04 小节。统计 UI 与休息提醒属 `FOCUS-05`。

## 范围与验收

- **契约**：`GET /focus-sessions/stats?from=YYYY-MM-DD&to=YYYY-MM-DD&time_zone=IANA`（owner 鉴权）：
  - from/to 必填、闭区间，跨度 ≤ 92 天，否则 422 `INVALID_FOCUS_RANGE`；缺少/格式错误 400 `INVALID_DATE`；
  - `time_zone` 缺省 `Asia/Shanghai`，非法 IANA → 422 `INVALID_TIME_ZONE`；
  - 响应 `{from,to,time_zone,total_sessions,completed_sessions,abandoned_sessions,actual_seconds,days[]}`；`days[].{date,completed_sessions,actual_seconds}` 按**时区本地日**聚合、只含范围内有会话的日期；
  - `total_sessions` 含 active/paused/completed/abandoned；`completed_sessions`/`abandoned_sessions` 分别计数；`actual_seconds` 只累计 completed/abandoned 的 `actual_seconds`。
- **实现**：`FocusSessionService.StatsAsync` 使用 IANA 时区把本地日起止换算成 UTC 边界，Dapper 一条聚合查询（`started_at AT TIME ZONE @Zone::date`），无迁移（复用 `idx_focus_sessions_owner_started`）。
- **OpenAPI**：新增路径与 `FocusStats/FocusDayStat` schema；契约测试在既有 FOCUS 用例中追加路径参数、必填、默认时区与 days 字段断言。
- **冒烟** `HttpFocusStatsSmoke`：直接插入 UTC 10:00/23:30、次日 01:00 的 completed/abandoned 行，断言 UTC 分组（2/900、1/120）、`Asia/Shanghai` 分组（1/600、1/420）、总计数、跨 owner 隔离、反向区间/93 天范围/非法时区/缺 from 的状态码。

## 语义与边界

- 统计以 `started_at` 归属日期，不按 ended_at；跨午夜会话归入开始日。
- `actual_seconds` 不包含 active/paused 会话（未结束的时长不猜算，与 F07 一致）。
- 服务端不做分页（≤92 天、按日聚合结果最多 93 项）。

## 后续

- `FOCUS-05`：统计 UI（日/周视图）、休息周期提醒、与任务详情联动。
