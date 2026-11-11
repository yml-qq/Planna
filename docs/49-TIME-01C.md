# 49｜TIME-01C：完成后 N 天规则

状态：代码已实现；PostgreSQL 18 隔离库 CI 验证见 `VALIDATION.md` 的 TIME-01C 小节。

## 范围与验收

- `PUT /tasks/{id}/recurrence` 新增 `kind="after_completion"`：要求 `interval_days` 1..3650，`rrule` 必须为空；`kind="rrule"` 要求合法 RRULE 且 `interval_days` 为空。混用或非法组合返回 `422 INVALID_RECURRENCE`，`legal_workday` 等仍返回 `422 UNSUPPORTED_RECURRENCE`。
- `POST /tasks/{id}/complete` 对 `after_completion` 规则的任务按任务完成（不接受 `occurrence_id`，否则 `422 INVALID_OCCURRENCE`）：标记任务 `done`/`completed_at`（受信时间钳制），并在同一事务内生成下一次 occurrence：本地日期 = 完成时刻在规则时区的本地日期 + `interval_days`，时刻取 `dtstart_local` 的 time-of-day；`due_at` 按 DST 策略换算（不存在时刻跳过生成）。
- occurrence 生成幂等（`series_id + original_local_start` 唯一），同键重放不重复生成；重复完成（任务已是 done）返回 `409 ALREADY_COMPLETED`。
- 测试覆盖规则校验、完成生成下一次 occurrence（固定完成时刻断言本地日期与 UTC instant）、重放不重复、reopen 后再次完成追加 occurrence、跨 owner/删除账号、revision 完整性。

## 实现和部署

本切片无 schema 变更、无新迁移（`recurrences.interval_days` 与 `ck_recur_fields` 既有）。`after_completion` 的 occurrence 为派生历史，完成写入只产生任务 `sync_changes`；`GET /tasks/{id}/occurrences` 对 `after_completion` 规则只读取已有行、不再按 RRULE 展开。

## 边界与后续

- `legal_workday`（法定工作日）仍返回 `422 UNSUPPORTED_RECURRENCE`，需要节假日数据服务与产品策略。
- occurrence 撤销完成、`only_this`/`entire_series` 编辑范围、提醒调度与客户端 UI 不在本切片；reopen 任务不会回收已生成的未来 occurrence（按历史保留，切片文档与 VALIDATION 已记录）。
