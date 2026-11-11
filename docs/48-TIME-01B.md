# 48｜TIME-01B：按 occurrence 完成循环任务

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43065790621](https://github.com/yml-qq/Planna/actions/runs/43065790621) 已通过。

## 范围与验收

- `POST /tasks/{id}/complete` 对存在 RRULE 规则的任务要求 `occurrence_id`：缺失仍返回 `422 RECURRING_TASK_REQUIRES_OCCURRENCE`；occurrence 不属于该任务/owner 或不存在返回 `422 INVALID_OCCURRENCE`；已完成 occurrence 返回 `409 ALREADY_COMPLETED`。
- 完成写 `task_occurrences.status='done'`、`completed_at`（受信时间钳制到 `[任务创建时间, 当前时间]`）与 `version+1`；响应返回完成后的 `Occurrence`（普通任务仍返回 `Task`，OpenAPI 200 为 `oneOf [Task, Occurrence]`）。
- 幂等：同键相同请求重放原响应；同键不同请求 `409 IDEMPOTENCY_KEY_REUSED`。每次完成推进 owner revision 并写 `sync_changes(entity_type='occurrence')` 与 `client_mutations`。
- 非循环任务携带 `occurrence_id` 仍返回 `422 INVALID_OCCURRENCE`；跨 owner 任务 `404 TASK_NOT_FOUND`。测试覆盖鉴权、规则→occurrence→完成端到端、重放、重复完成、跨 owner、非法/随机 occurrence、非循环任务、受信时间与 revision/变更记录原子性。

## 实现和部署

本切片无 schema 变更、无新迁移。occurrence 完成在 `TaskCompleteService` 的既有事务内完成：owner 时钟锁 → 幂等检查 → 任务版本校验 → occurrence 读取与状态检查 → 更新与 revision/`sync_changes`/`client_mutations` 提交。

## 边界与后续

- `after_completion`（完成后 N 天）与 `legal_workday` 规则仍返回 `422 UNSUPPORTED_RECURRENCE`，需产品策略与节假日数据，留待 `TIME-01C`。
- occurrence 撤销完成（reopen occurrence）、`only_this`/`entire_series` 编辑范围与提醒调度不在本切片；客户端 UI 后续实现。
