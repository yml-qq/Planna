# 84｜FOCUS-01：专注会话服务端 API

状态：代码已实现；本地 `dotnet build --warnaserror`、`dotnet format`、契约测试 **29 项**通过；[Server integration 43175516165](https://github.com/yml-qq/Planna/actions/runs/43175516165) 与 [Desktop core 43175516258](https://github.com/yml-qq/Planna/actions/runs/43175516258) 均 **success**（含迁移与冒烟）。客户端计时 UI 与离线恢复属 `FOCUS-02`。

## 范围与验收

- **迁移** `202611140001_FocusSessions`：`focus_sessions(id, owner_id, task_id?, started_at, ended_at?, planned_seconds, actual_seconds, break_seconds, state, version, created_at, updated_at)`；约束 planned 60..86400、actual/break 0..86400、state ∈ active/paused/completed/abandoned、`ended_at >= started_at`；任务外键 `(owner_id, task_id) → tasks(owner_id,id) ON DELETE CASCADE`；部分唯一索引 `ux_focus_sessions_open(owner_id) WHERE state IN ('active','paused')` 保证单 owner 同时只有一个未结束会话。
- **契约**：
  - `POST /focus-sessions`（`Idempotency-Key`）：可选 `task_id/planned_seconds(默认1500)/started_at`；创作 active 会话；任务需属于 owner 且未删除（404 `TASK_NOT_FOUND`）；已有 active/paused 会话 → 409 `FOCUS_SESSION_ACTIVE`；同 key 重放 → 200，同 key 不同体 → 409 `IDEMPOTENCY_KEY_REUSED`。
  - `GET /focus-sessions?limit=1..100`：按 `started_at DESC, id DESC` 返回 `{items}`。
  - `GET /focus-sessions/{id}`：owner 隔离，跨 owner 404。
  - `PATCH /focus-sessions/{id}`（`Idempotency-Key` + `If-Match: "vN"`）：状态机 active→paused/completed/abandoned、paused→active/completed/abandoned；终态不可再变更（409 `FOCUS_SESSION_FINISHED`）；同状态/非法迁移 422 `INVALID_FOCUS_TRANSITION`；版本不符 409 `VERSION_CONFLICT` 并返回服务器版本；完成/放弃写 `ended_at`（缺省 now，显式值需 ≥ started_at）；actual/break 由客户端提供，服务端不从墙钟推断专注时长（F07）。
- **OpenAPI**：新增 `/focus-sessions`、`/focus-sessions/{id}` 与 `FocusSession/FocusSessionList/CreateFocusSessionRequest/UpdateFocusSessionRequest`，契约测试断言路径、响应码、If-Match pattern、枚举与默认值。
- **冒烟**（`HttpFocusSmoke`）：创建/重放/同 key 复用/开放会话冲突/列表/GET/跨 owner 404/暂停→恢复→完成/陈旧版本冲突/终态拒绝/完成后可新建/非法 planned、任务、状态、limit、缺失 If-Match。

## 实现和部署

服务端沿用 `TaskWriteContext` + `client_mutations` 幂等记录 + owner 级 advisory lock，全部写入事务化。专注尚未进入 `/sync` 变更流（FOCUS-02 的离线客户端再决定是否作为同步实体）。

## 边界与后续

- `FOCUS-02`：桌面/移动计时 UI、断电恢复（以客户端提供的 started_at/actual_seconds 恢复）、休息周期、与任务详情联动。
- 服务端不自动累计 `actual_seconds`；客户端必须显式提交，避免把后台退出时长误计为专注。
