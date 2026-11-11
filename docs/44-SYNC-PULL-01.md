# 44｜SYNC-PULL-01：增量拉取切片

状态：代码已实现；PostgreSQL 18 隔离库 CI 验证见 `VALIDATION.md` 的 SYNC-PULL-01 小节。

## 范围与验收

- `GET /sync/pull` 按 owner 已提交 revision 与 `change_index` 升序返回 `sync_changes`：`changes`（`revision/change_index/entity_type/entity_id/op/version/payload`）、`next_cursor`、`has_more`、`server_time`。游标不透明（`base64url(revision:change_index)`），客户端原样回传；游标不基于 `updated_at`。
- 默认每页 100、最大 200；非法 `limit` 返回 `400 INVALID_LIMIT`，非法游标 `400 INVALID_CURSOR`。首次拉取（无游标）从保留窗口起点开始。
- `owner_sync_clocks.min_available_revision` 大于请求游标的 revision 时返回 `410 CURSOR_EXPIRED`，客户端应改用安全 bootstrap；本切片实现到期判定，bootstrap 端点留待 `SYNC-BOOTSTRAP-01`。
- 只返回 JWT `sub` 对应的变更，跨 owner 的 revision 不可见；拉取不推进 revision、不写 `sync_changes`/`client_mutations`。
- 测试在隔离 PostgreSQL 18 库覆盖鉴权、排序、多变更 revision 的分页边界（`limit=2` 跨页不重不漏）、结束游标重复拉取、`has_more`、非法参数、`410` 到期与 owner 隔离。

## 实现和部署

本切片无 schema 变更、无新迁移。`sync_changes` 与 `owner_sync_clocks` 由既有写入路径维护；拉取使用独立 Dapper 查询类，强制 owner 过滤与参数化。`spec/openapi.yaml` 的 `/sync/pull` 补充语义与 `400/401` 响应，契约测试增至 16 项。

## 边界与后续

- `sync/push` 批量写入、`sync/bootstrap` 快照与冲突策略（含客户端断网重试/数据丢失测试）留待 `SYNC-PUSH-01` 与 `SYNC-BOOTSTRAP-01`。
- 变更保留窗口与清理任务尚未实现；`min_available_revision` 目前由维护脚本/后续切片推进，未做自动清理。
