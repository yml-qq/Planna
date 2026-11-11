# 45｜SYNC-PUSH-01：批量 mutation 推送切片

状态：代码已实现；PostgreSQL 18 隔离库 CI 验证见 `VALIDATION.md` 的 SYNC-PUSH-01 小节。

## 范围与验收

- `POST /sync/push` 接收 `{device_id, mutations[1..100]}`，逐条处理并返回 `results[{mutation_id, status, entity_version, server_entity, retryable}]`；每条 mutation 独立事务提交，批次非全有或全无。`device_id`、`mutation_id`、`entity_id` 为空或条数越界时整批 `400 INVALID_SYNC_REQUEST`。
- 幂等：以 `(owner, mutation_id)` 记录已处理结果；重复提交返回 `duplicate` 并携带原 `entity_version`/`server_entity`。客户端可移除 `accepted`/`duplicate` 的 outbox 项。
- 本切片支持 `entity_type=task` 的 `upsert`/`delete`：
  - `upsert`，`base_version=0`：payload 必须含 `list_id` 与 `title`，按创建规则校验（标题/描述/优先级/时区/全天与 due 互斥/估算）；清单必须属于 owner 且未软删。
  - `upsert`，`base_version≥1`：payload 中出现的字段覆盖当前值（`null` 清空可空字段），合并后校验；版本不符返回 `conflict`（含当前 `entity_version` 与 `server_entity`，`retryable=true`）；已软删任务返回 `conflict`（`retryable=false`）。
  - `delete`：`base_version≥1`；版本不符同样 `conflict`；已删除任务返回 `duplicate`；非法 payload/跨 owner/未知实体返回 `rejected`（`retryable=false`）。
  - 其他 `entity_type` 或操作返回 `rejected`，不静默丢弃。
- 每次 `accepted` 写入推进 owner revision，并在同一事务写 `sync_changes` 与 `client_mutations`；`delete` 写 `op='delete'` tombstone。测试在隔离 PostgreSQL 18 库覆盖鉴权、批内重复、重放、冲突/拒绝矩阵、批次独立性、owner 隔离、revision 与变更记录原子性。

## 实现和部署

本切片无 schema 变更、无新迁移。`SyncPushService` 在独立事务中复用既有 owner 时钟行锁与版本化写入语义；请求/响应按 `spec/openapi.yaml` 的 `SyncPushRequest`/`SyncPushResponse` 严格绑定（未知字段拒绝）。`client_mutations.device_id` 暂不写入，设备审计留待后续切片。

## 边界与后续

- 本切片仅覆盖 task；list/tag/step 的 push 与客户端 outbox 策略留待后续切片（返回 `rejected` 明确不静默丢弃）。
- 冲突合并 UI、`sync/bootstrap` 快照与变更清理窗口不在本切片。
- 批内 mutation 顺序按请求顺序执行；同批对同一实体的多次修改按序生效或冲突。
