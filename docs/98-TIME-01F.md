# 98｜TIME-01F：单次 occurrence 编辑（跳过/改期）

状态：代码已实现；本地 build/format/契约测试 **33 项**通过，PostgreSQL 18 CI 见 `VALIDATION.md` 的 TIME-01F 小节。法定工作日与数据源属 `TIME-01G`。

## 范围与验收

- **契约**：`PATCH /tasks/{id}/occurrences/{occurrenceId}`（`Idempotency-Key`）：
  - body `{expected_version, patch:{due_at?, status?}}`；`status ∈ todo|skipped`，`patch` 至少一项；
  - 改期只改 `due_at`，不改 occurrence 身份（`original_local_start/time_zone` 不变）；
  - 已完成 occurrence 返回 409 `OCCURRENCE_COMPLETED`；跳过/改期均推进版本并写 `sync_changes(entity_type='occurrence')` 与 owner revision；
  - 版本不符 409 `VERSION_CONFLICT`；跨 owner/任务 404 `TASK_NOT_FOUND`/`OCCURRENCE_NOT_FOUND`；非法状态/空 patch 422。
- **完成保护**：`POST /tasks/{id}/complete` 对 `skipped` occurrence 返回 409 `OCCURRENCE_SKIPPED`，避免跳过项被误完成。
- **同步**：不改 schema；复用既有 `task_occurrences.status/due_at/version` 与 `sync_changes`，客户端拉取可感知单次跳过/改期（桌面 core 仍按未知实体跳过，待客户端消费）。
- **OpenAPI**：新增路径与 `OccurrencePatch/UpdateOccurrenceRequest`，契约测试新增 `test_occurrence_patch_contract`（**33 项**）。
- **冒烟** `HttpOccurrenceEditSmoke`：规则→occurrence 物化→跳过→完成 skipped 被拒→取消跳过→改期→陈旧版本→幂等重放/同 key 复用→完成后编辑被拒→跨 owner 404→非法状态/空 patch/缺失 key→revision=6。

## 语义与边界

- 仅支持单次 `only_this` 编辑；`entire_series` 继续通过 `PUT /tasks/{id}/recurrence` 修改规则（既有实现）。
- 跳过不删除行，也不生成 exdate；列表返回 `status=skipped`，客户端应据此隐藏/标记。
- 不自动重算提醒：已存 reminders 的 `scheduled_at` 不会随改期变化（客户端需重新调度）。
- `TIME-01G`（法定工作日）仍依赖节假日数据服务与政策，未在本轮伪造数据。

## 后续

- `TIME-01G`：节假日数据源（授权/更新/缓存）、`legal_workday` 规则与 occurrence 生成、提醒重排。
- 客户端 occurrence 跳过/改期 UI 与 outbox 消费。
