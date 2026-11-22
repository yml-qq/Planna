# 103｜FOCUS-09a：按任务的专注历史

状态：代码已实现；本地 build/format/契约 **33 项**、前端 **69 项**与 `pnpm build` 通过；[Server integration 44981202340](https://github.com/yml-qq/Planna/actions/runs/44981202340) 与 [Desktop core 44981202399](https://github.com/yml-qq/Planna/actions/runs/44981202399) 均 **success**。分页/报表/后台通知属 `FOCUS-09b`。

## 范围与验收

- **服务端**：`GET /focus-sessions?task_id=<uuid>&limit=` 支持按任务过滤，按 `started_at DESC, id DESC` 返回；任务不存在/跨 owner/已软删 → 404 `TASK_NOT_FOUND`；非法 `task_id` → 400 `INVALID_TASK_ID`；无过滤时行为不变。
- **OpenAPI/契约**：列表参数新增 `task_id`、响应新增 404；契约测试断言参数与响应集（保持 **33 项**）。
- **冒烟**：`HttpFocusSmoke` 增加 owner/foreign 任务夹具与一条直插的 task-linked completed 会话，断言过滤只命中该任务、跨 owner/不存在任务 404、非法 task_id 400。
- **桌面**：`focus_list` 支持 `task_id`；桥接 `focusList(..., taskId)`（Memory 过滤）；focus store 新增 `taskHistory`/`loadTaskHistory`；任务详情展开时按任务请求历史（limit 50），不再依赖“最近 30 条全局会话”的客户端过滤。
- 测试（前端 **69 项**，新增 1 项）：按任务过滤会话 + 每任务历史缓存。

## 语义与边界

- 历史仍为单次请求（≤100 条），**无游标分页**；任务详情取 50 条，更早记录需后续分页。
- 任务被软删后其专注历史不可按任务查询（当前 404）；若需保留可查，需额外审计策略。
- 仍为在线查询，不做本地缓存/离线历史。

## 后续

- `FOCUS-09b`：游标分页、周/月报表导出、后台/系统通知真机验证与独立任务详情页。
