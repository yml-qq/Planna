# 47｜TIME-01A：循环规则与 occurrence 窗口

状态：代码已实现；PostgreSQL 18 隔离库 CI 验证见 `VALIDATION.md` 的 TIME-01A 小节。

## 范围与验收

- `GET/PUT/DELETE /tasks/{id}/recurrence`：为 owner 未软删任务维护一条 RRULE 规则。`PUT` 带 UUID `Idempotency-Key` 与 `{expected_version, kind:"rrule", rrule, time_zone, dtstart_local}`：`expected_version=0` 创建、`≥1` 更新；已存在时创建 `409 RECURRENCE_EXISTS`，不存在时更新 `404 RECURRENCE_NOT_FOUND`，版本不符 `409 VERSION_CONFLICT`；`after_completion`/`legal_workday` 返回 `422 UNSUPPORTED_RECURRENCE`。`DELETE` 带 `If-Match: "vN"`，移除规则但保留 occurrence 历史。
- RRULE 子集：`FREQ=DAILY|WEEKLY|MONTHLY` 配 `INTERVAL`、`COUNT` 或 `UNTIL`（互斥）、`WEEKLY BYDAY`；其余部件（`BYMONTHDAY`/`BYSETPOS`/`WKST` 等）与未知键返回 `422 INVALID_RECURRENCE`，不静默归一化。
- `GET /tasks/{id}/occurrences?from&to`：本地日期窗口（默认未来 30 天，最大 90 天），幂等物化 `task_occurrences`（`series_id + original_local_start` 唯一）并返回 `items`。DST 策略：不存在的本地时刻跳过；歧义时刻只生成一个 occurrence，取最早 instant。物化为派生状态，不推进 revision。
- 规则写入推进 owner revision 并写 `sync_changes(entity_type='recurrence')`；测试覆盖鉴权、规则创建/更新/删除/幂等/版本冲突、非法 RRULE 与 kind、窗口边界、周规则计数与排序、DST 春季跳过与秋季歧义、owner 隔离与 revision 完整性。

## 实现和部署

本切片无 schema 变更、无新迁移（`recurrences`/`task_occurrences` 既有）。`RecurrenceExpander` 为纯函数并限制候选上限，避免无界展开；`RecurrenceQuery` 在窗口内先物化再读取，规则删除后保留历史 occurrence 行。

## 边界与后续

- occurrence 完成（`POST /tasks/{id}/complete` 带 `occurrence_id`）、`after_completion`/`legal_workday` 规则、`only_this`/`entire_series` 编辑范围与提醒调度属 `TIME-01B` 及后续切片；当前循环任务完成仍返回 `422 RECURRING_TASK_REQUIRES_OCCURRENCE`。
- 月规则按起始日重复，缺日月份跳过；`BYMONTHDAY` 等高级规则与闰年策略需在 TIME-01B 按业务策略实现。
