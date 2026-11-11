# 39｜TASK-COMPLETE-01：任务完成与撤销完成

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43039556958](https://github.com/yml-qq/Planna/actions/runs/43039556958) 已通过。

## 范围与验收

- `POST /tasks/{id}/complete` 与 `POST /tasks/{id}/reopen` 均要求 UUID `Idempotency-Key` 与 body `{expected_version}`；complete 另有可空 `occurrence_id`/`completed_at`。owner 只来自 JWT `sub`。
- 状态机：complete 只允许 `todo/in_progress → done`（写入 `completed_at`），已完成的再次 complete 返回 `409 ALREADY_COMPLETED`，归档任务返回 `422 INVALID_TASK_STATE`；reopen 只允许 `done → todo`（清空 `completed_at`），未完成任务返回 `409 NOT_COMPLETED`。跨 owner 或已软删一律 `404 TASK_NOT_FOUND`。
- `completed_at` 受信时间修正：缺失取服务器当前时间；早于任务创建时间钳制到创建时间，晚于当前时间钳制到当前时间。
- 循环任务完成需要 occurrence 语义，属 TIME-01；本切片检测到 `recurrences` 记录返回 `422 RECURRING_TASK_REQUIRES_OCCURRENCE`，普通任务携带 `occurrence_id` 返回 `422 INVALID_OCCURRENCE`。
- 版本不符返回 `409 VERSION_CONFLICT`（`error.details` 含 `entity_id`/`server_version`）；同键相同请求重放原 `200` 响应，同键不同请求返回 `409 IDEMPOTENCY_KEY_REUSED`。
- 写路径沿用 owner 同步时钟行锁串行化：时钟锁（同时拒绝已删除账号）→ 幂等检查 → 行读取与状态/版本校验 → `UPDATE` 状态与 `version+1` → revision 推进 → `sync_changes(upsert)` 与 `client_mutations` 同一事务提交。
- 测试在隔离 PostgreSQL 18 库覆盖状态机各分支、受信时间钳制、幂等重放与换键冲突、版本冲突、并发完成串行化、跨 owner/软删/循环任务错误、reopen 清空 `completed_at` 与 revision 完整性。静态契约检查覆盖 OpenAPI complete/reopen 路径、参数、状态码与请求 schema。

## 实现和部署

本切片无 schema 变更、无新迁移。投影沿用 `TaskReadView`，同步变更负载使用同一投影。`reopen` 路径为新增契约（`spec/openapi.yaml` 与 `docs/06` 端点总览已含），实现与 complete 对称。

## 边界与后续

- 循环任务与 occurrence（`task_occurrences` 写入、RRULE 实例、完成历史）留待 TIME-01；届时 complete 将接受有效 `occurrence_id` 并更新对应实例，同时保持普通任务路径不变。
- 归档任务与完成互斥在本切片固定；如产品允许「归档即完成」需先修订契约与状态机测试。
- 回收站恢复、移动清单与客户端 UI 不在本切片范围。
