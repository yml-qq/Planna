# 46｜SYNC-BOOTSTRAP-01：一致快照引导切片

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43058072416](https://github.com/yml-qq/Planna/actions/runs/43058072416) 已通过。

## 范围与验收

- `POST /sync/bootstrap` 在单个 `REPEATABLE READ` 只读事务内读取 owner 的 `owner_sync_clocks.revision` 与全部未软删 `tasks`/`lists`，返回 `{snapshot_cursor, next_page_token, tasks, lists}`；`snapshot_cursor` 编码快照时的 committed revision（`revision:int.MaxValue`），可直接交给 `GET /sync/pull` 继续增量。
- 快照只返回当前 owner 数据；软删任务与清单被排除；无数据 owner 返回空数组与 revision 0 游标。`next_page_token` 预留，当前恒为 `null`（单页快照）。
- 快照为只读：不推进 revision、不写 `sync_changes`/`client_mutations`。
- 测试在隔离 PostgreSQL 18 库覆盖鉴权、数据过滤与排序、owner 隔离、空 owner、快照游标与 pull 的衔接（快照后提交的变更可由 pull 增量获取）、只读无副作用。

## 实现和部署

本切片无 schema 变更、无新迁移。`SyncBootstrapQuery` 使用独立 Dapper 查询与 `IsolationLevel.RepeatableRead` 事务保证 revision 与数据来自同一快照；`spec/openapi.yaml` 补充语义与 `401` 响应，契约测试增至 18 项。

## 边界与后续

- 单页快照对个人数据量足够；大数据集的分页快照（`next_page_token` 实现）与快照内存上限留待性能切片。
- 冲突合并策略（客户端冲突队列 UI）与变更保留窗口清理仍属后续切片；本切片只保证快照与增量衔接的语义。
