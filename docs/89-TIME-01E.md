# 89｜TIME-01E：任务/occurrence 提醒调度 API

状态：代码已实现；本地 `dotnet build --warnaserror`、`dotnet format`、契约测试 **31 项**通过；[Server integration 44728339335](https://github.com/yml-qq/Planna/actions/runs/44728339335) 与 [Desktop core 44728339256](https://github.com/yml-qq/Planna/actions/runs/44728339256) 均 **success**（Desktop 首次失败为习惯测试日期依赖，已改为注入时钟）。

## 范围与验收

- **契约**（任务/occurrence 归属 owner）：
  - `POST /tasks/{taskId}/reminders`（`Idempotency-Key`）：`{occurrence_id?, scheduled_at, offset_minutes?, channel?}`；`scheduled_at` 必填且在 `now-1d..now+366d`、`offset_minutes` -10080..10080、`channel ∈ local|push|in_app`（缺省 local）；`occurrence_id` 必须属于该任务；任务/occurrence 跨 owner 404；响应 201/重放 200/同 key 不同体 409。
  - `GET /tasks/{taskId}/reminders?include_disabled`：仅返回未删除提醒，默认排除 disabled。
  - `PATCH /tasks/{taskId}/reminders/{reminderId}`（`expected_version` + patch：scheduled_at/offset_minutes/channel/enabled）：版本不符 409 `VERSION_CONFLICT`。
  - `DELETE /tasks/{taskId}/reminders/{reminderId}`（`If-Match: "vN"`）：软删除并禁用，重放 204。
  - `GET /reminders/upcoming?from&to&limit`：owner 范围内 enabled、未删除、任务未删的提醒，窗口 ≤31 天、limit 1..200，按 `scheduled_at,id` 排序。
- **语义**：`scheduled_at` 由客户端按任务/occurrence 的 due 时间与 offset 计算后显式提交，服务端不推断墙钟；`offset_minutes` 仅作审计/展示。提醒为在线服务端存储，本地通知/远程推送投递不在本切片。
- **OpenAPI**：3 条路径、5 个 schema（Reminder/ReminderList/CreateReminderRequest/ReminderPatch/UpdateReminderRequest），契约测试新增 `test_reminder_contract`（总数 **31 项**）。
- **冒烟** `HttpReminderSmoke`：创建/重放/同 key 复用、列表与 disabled 过滤、upcoming 窗口与跨 owner 隔离、改期并禁用、陈旧版本、非法 channel/过期时间/未知 occurrence/缺失 If-Match/非法 limit、删除后不再列出。

## 规格拆分（最小修正）

原 `TIME-01E` 行同时包含“法定工作日规则、提醒调度与编辑范围”。本轮只交付**提醒调度**，原因：

- `legal_workday`（中国法定工作日含补班）依赖节假日数据服务，仓库当前无数据源与产品策略，伪造静态日历会产生错误业务结果；需要单独的 `HOLIDAY-01` 数据源切片（含数据授权与更新机制）。
- occurrence 编辑范围（`only_this`/`entire_series`）需要 `task_occurrences` 增加覆盖字段与系列版本语义，属于新的数据模型变更，拆到后续切片。

因此路线图更新为：`TIME-01E` 交付提醒调度；`TIME-01F` 承接法定工作日数据服务与 occurrence 编辑范围。

## 边界与后续

- 提醒不会在任务改期时自动重算（客户端需重新 PATCH）；完成/删除任务只影响 upcoming 过滤，已存提醒保留审计。
- `channel=push` 仅存储，投递由 V1.1 worker/远程推送（`REM-02`）实现。
