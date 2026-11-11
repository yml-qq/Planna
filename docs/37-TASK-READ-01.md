# 37｜TASK-READ-01：任务分页读取切片

状态：代码与迁移已实现；[PostgreSQL 18 隔离库 CI 运行 43022227391](https://github.com/yml-qq/Planna/actions/runs/43022227391) 已通过。

## 范围与验收

- 已认证用户可游标分页读取自己的未软删任务（`GET /tasks`），响应为 `items + next_cursor`，每项含完整任务投影（含 `version`、`completed_at`）。owner 只来自 JWT `sub`，不接受任何请求体 owner 字段。
- 排序固定为 `created_at,id` 降序；游标由服务器编码（`base64url(utc_ticks:uuid32)`，与设备列表同方案但类型独立），客户端只能原样传回。`limit` 默认 50、最大 200；非法 `limit`/`cursor`/`list_id`/`status` 返回 `400`。
- 可选 `list_id` 过滤只接受当前 owner 未删除的清单，跨 owner 或已删除清单返回 `404 LIST_NOT_FOUND`；可选 `status` 限 `todo/in_progress/done/archived`。软删任务与其他人任务绝不出现。
- 读取使用独立 Dapper 查询类，强制参数化与 owner 过滤，不经过 EF Core；写入路径（`CreateTaskService`）不变。
- 测试在隔离 PostgreSQL 18 库覆盖权限、分页边界（limit 1/200/默认/重复参数/非法游标）、过滤、排序与错误码。静态契约检查覆盖 OpenAPI 路径、参数、状态码与 `TaskPage` 结构。

## 实现和部署

`202611110008_TaskListReadIndex` 为任务读取增加部分索引 `idx_tasks_owner_created ON tasks(owner_id, created_at DESC, id DESC) WHERE deleted_at IS NULL`；审阅 SQL 为 `server/tasks-read-migrations.sql`。先应用迁移再发布 API。

`(created_at,id)` 行值比较保证同一时间戳下分页不重不漏。列表不是时间点快照；翻页期间的新写入可能落在更早的页。读取列表不锁 `owner_sync_clocks`，不影响写入串行化。

## 边界与后续

- 本切片不含 `GET /tasks/{id}`、`PATCH /tasks/{id}`、`DELETE /tasks/{id}`、`complete/reopen`、List CRUD、Tags/Steps 与客户端实现；详情/更新/软删属于 `TASK-UPDATE-01`（`38-TASK-UPDATE-01.md`），完成/撤销完成属于 `TASK-COMPLETE-01`，List CRUD 与 Tags/Steps 属于 `TASK-03`。
- 主分页走新索引；`status` 过滤可借用 `idx_tasks_owner_status`，更大数据量或复合过滤的性能以真实数据复核。
- AI 读工具接入、同步 pull 与本地 SQLite 读取语义不受本切片影响。
