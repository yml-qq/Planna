# 40｜TASK-LIST-01：清单 CRUD 切片

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43047563939](https://github.com/yml-qq/Planna/actions/runs/43047563939) 已通过。

## 范围与验收

- `GET /lists` 返回 JWT `sub` 对应的未软删清单（含 inbox 与归档清单），响应 `items` 数组（`id/name/kind/version`），按 `sort_order,id` 升序。`GET /lists/{id}` 只返回当前 owner 未软删清单，跨 owner 或已软删一律 `404 LIST_NOT_FOUND`。读取走独立 Dapper 查询。
- `POST /lists` 必须带 UUID `Idempotency-Key`，body `{id, name}`；服务端固定 `kind=custom`（inbox 只由注册流程建立），名称 1~120 字符去首尾空白；同键重放原 `201`，同键不同内容 `409 IDEMPOTENCY_KEY_REUSED`，ID 冲突 `409 LIST_ID_CONFLICT`，非法输入 `422 INVALID_LIST`。
- `PATCH /lists/{id}` 只允许 `{expected_version, patch:{name}}` 重命名（含归档清单）；版本不符 `409 VERSION_CONFLICT`（details 含 `entity_id`/`server_version`/`changed_fields`），非法名称 `422`，同键重放原 `200`。
- `DELETE /lists/{id}` 必须带 `Idempotency-Key` 与 `If-Match: "vN"`；清单内仍有未软删任务时 `409 LIST_NOT_EMPTY`，inbox 返回 `422 LIST_INBOX_PROTECTED`；软删写 `deleted_at + version+1` 与 `sync_changes(op='delete', entity_type='list')` tombstone，相同键重试 `204`。
- 所有清单写入沿用 owner 同步时钟行锁串行化：时钟锁（拒绝已删除账号）→ 幂等检查 → 读取与校验 → 更新/软删 → revision 推进 → `sync_changes` 与 `client_mutations` 同一事务。清单变更负载使用 `TaskListView` 投影。
- 测试在隔离 PostgreSQL 18 库覆盖鉴权、id/键/If-Match/名称错误、跨 owner 404、归档清单重命名、非空清单与 inbox 删除保护、幂等重放与换键冲突、版本冲突、软删 tombstone 与 revision 完整性。静态契约检查覆盖 OpenAPI 的 lists 路径、参数、状态码与请求 schema。

## 实现和部署

本切片无 schema 变更、无新迁移。`spec/openapi.yaml` 补齐 `docs/06` 总览中已列但缺失的 `/lists/{id}` get/patch/delete 路径，并为 `POST /lists` 补充 `400/401` 与 `additionalProperties: false`。

## 边界与后续

- 归档/恢复与清单排序（`sort_order` 编辑）不在本切片；归档清单仍出现在列表响应中，恢复与排序界面属后续 UX/API 切片。
- Tags/Steps/查询（`TASK-TAG-01`）与移动任务到其他清单（`list_id` 变更）不在此列；移动任务需先修订 PATCH 契约。
- 清单删除保留 tombstone；恢复端点与回收站保留策略由后续切片与隐私策略定义。
