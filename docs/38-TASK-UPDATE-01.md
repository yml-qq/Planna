# 38｜TASK-UPDATE-01：任务详情、版本化更新与软删

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43034113302](https://github.com/yml-qq/Planna/actions/runs/43034113302) 已通过。

## 范围与验收

- `GET /tasks/{id}` 只返回当前 owner 未软删任务的完整投影（含 `completed_at`、`version`）；跨 owner 或已软删一律 `404 TASK_NOT_FOUND`，不泄露他人资源存在性。读取走独立 Dapper 查询。
- `PATCH /tasks/{id}` 必须带 UUID `Idempotency-Key` 与 `{expected_version, patch}`。patch 只允许 `title/description/priority/due_at/due_local_date/time_zone/estimate_minutes`，未列字段保持原值，可空字段 `null` 清空；未知字段返回 `400`。合并后按创建规则校验（标题、优先级、描述、预估分钟、IANA 时区、all_day 与 due 互斥、due≥start），非法返回 `422 INVALID_TASK/INVALID_TIME_ZONE`。
- 版本不符返回 `409 VERSION_CONFLICT`，`error.details` 含 `entity_id`、`server_version` 和可安全披露的 `changed_fields`。同一键相同请求重放原 `200` 响应；同一键不同请求返回 `409 IDEMPOTENCY_KEY_REUSED`。每次更新审计 `updated_at/version`。
- `DELETE /tasks/{id}` 必须带 `Idempotency-Key` 与 `If-Match: "vN"`。软删为 `deleted_at + version+1` 并在同一事务写入 `sync_changes(op='delete')` tombstone；相同键重试返回 `204`，旧版本返回 `409`。不做硬删除。
- 写路径沿用 owner 同步时钟行锁串行化：先锁 `owner_sync_clocks`（同时拒绝已删除账号），再查幂等结果，然后按版本校验并更新任务、推进 revision、写 `sync_changes` 与 `client_mutations`，全部同一事务。
- 测试在隔离 PostgreSQL 18 库覆盖鉴权、id 与参数错误、跨 owner 404、已软删 404、版本冲突与字段差异、null 清空、422 校验、幂等重放与换键冲突、并发更新串行化、软删 tombstone 与 revision 完整性。静态契约检查覆盖 OpenAPI 路径、参数、状态码与 patch 字段集。

## 实现和部署

本切片无 schema 变更、无新迁移。`PATCH` 的投影与 `GET /tasks` 一致（`TaskReadView`），同步变更负载也使用该投影；`DELETE` 的 tombstone 负载为 `{id, version, deleted_at}`。版本化更新通过 `UPDATE ... WHERE id=...` 在 owner 锁内执行，0 行时按「不存在/无权 → 404、版本不符 → 409」区分，不泄露他人资源存在性。

## 边界与后续

- 本切片不含 `complete/reopen`（`TASK-COMPLETE-01`）、回收站恢复端点、List CRUD、Tags/Steps 与客户端实现。
- 恢复已删除任务（US-012 的恢复期）依赖回收站界面与保留策略，后续切片实现；tombstone 保留期由隐私策略定义。
- 更新不允许改 `status`、`position`、`list_id`（移动清单）与 `all_day` 开关；若产品需要这些字段进入 patch，先修订契约与契约测试。
