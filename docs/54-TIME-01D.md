# 54｜TIME-01D：occurrence 撤销完成

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43077888174](https://github.com/yml-qq/Planna/actions/runs/43077888174) 已通过。

## 范围与验收

- `POST /tasks/{id}/reopen` 扩展 `occurrence_id`：RRULE 任务必须指定 `occurrence_id`（缺失返回 `422 RECURRING_TASK_REQUIRES_OCCURRENCE`），撤销该已完成 occurrence（`done → todo`、清空 `completed_at`、版本 +1）；非法/跨任务 occurrence 返回 `422 INVALID_OCCURRENCE`，未完成 occurrence 返回 `409 NOT_COMPLETED`。任务行本身不变（任务版本仅用于 `expected_version` 校验）。
- 非循环任务保持原有任务级 reopen（`done → todo`）；携带 `occurrence_id` 返回 `422 INVALID_OCCURRENCE`。`after_completion` 任务仍按任务级 reopen 处理。
- 响应：循环任务返回完成后的 `Occurrence`，普通任务返回 `Task`（OpenAPI 200 为 `oneOf [Task, Occurrence]`）。
- 同键重放返回原响应（按存储 JSON 形态区分 occurrence/task）；每次撤销推进 owner revision 并写 `sync_changes(entity_type='occurrence')` 与 `client_mutations`。
- 测试覆盖鉴权、规则→occurrence→完成→撤销端到端、重放、重复撤销、随机/缺失 occurrence、非循环任务与跨 owner、revision/变更记录一致性。

## 实现和部署

本切片无 schema 变更、无新迁移。撤销在 `TaskReopenService` 的既有事务内完成：owner 时钟锁 → 幂等检查 → 任务版本校验 → 规则类型判断 → occurrence 状态校验 → 更新与 revision/`sync_changes`/`client_mutations` 提交。

## 边界与后续

- `legal_workday`（需节假日数据）与 `TIME-01E` 的其余内容（提醒调度、编辑范围）未实现；occurrence 撤销不触发提醒重排。
- 撤销完成不会恢复任务级状态；循环任务始终以 occurrence 为完成单位。
