# 06｜HTTP API 详细约定（OpenAPI 基线）

## 1. 全局约定

- Base URL: `https://api.example.com/v1`（占位符，部署时替换）。
- TLS 必须；JSON UTF-8；时间统一 RFC3339 UTC，LocalDate 为 `YYYY-MM-DD`，时区为 IANA 名称。
- `Authorization: Bearer <access_token>`；绝不把 token 塞 URL 或写入日志。
- `X-Device-Id: uuid` 和写入时的 `Idempotency-Key: uuid`；同步 push 的每条 mutation 额外有 `mutation_id`。
- POST 创建允许客户端生成 `id`；后端必须校验 owner 与引用关系。
- `request_id` 服务端生成并回传。`429` 包含 `Retry-After`；分页 cursor opaque。
- 默认分页 `limit=50`、最大 `200`；服务器对大数据集使用 seek/cursor 而不是无界 OFFSET。
- 服务端 API 不能通过 HTTP 200 伪装业务失败；错误一律 `4xx/5xx` + 标准结构。

## 2. 核心端点总览

| 方法 | 路径 | 作用 | 版本 |
|---|---|---|---|
| POST | `/auth/register` | 注册 | V1.0 |
| POST | `/auth/login` | 登录 | V1.0 |
| POST | `/auth/login/challenges` | 发送登录邮箱验证码 | V1.0 |
| POST | `/auth/refresh` | 轮换刷新 token | V1.0 |
| POST | `/auth/step-up` | 敏感操作的近期身份复核 | V1.0 |
| POST | `/auth/step-up/challenges` | 向当前账号发送敏感操作验证码 | V1.0 |
| POST | `/auth/logout` | 使当前会话失效 | V1.0 |
| GET | `/me` | 账户/设置 | V1.0 |
| POST/GET | `/me/exports` | 创建/列出云端导出任务 | V1.0 |
| GET | `/me/exports/{id}` | 查看导出状态 | V1.0 |
| GET | `/me/exports/{id}/download` | 授权下载 JSON | V1.0 |
| POST | `/me/deletion-requests` | 身份复核后申请删除账号 | V1.0 |
| PATCH | `/me/preferences` | 时区/作息/默认清单 | V1.0 |
| GET/POST | `/lists` | 获取/新建清单 | V1.0 |
| GET/PATCH/DELETE | `/lists/{id}` | 清单详情/编辑/删除 | V1.0 |
| GET/POST | `/tasks` | 分页获取/新建任务 | V1.0 |
| GET/PATCH/DELETE | `/tasks/{id}` | 获取/修改/软删任务 | V1.0 |
| POST | `/tasks/{id}/complete` | 完成普通任务或指定 occurrence | V1.0 |
| POST | `/tasks/{id}/reopen` | 撤销完成 | V1.0 |
| GET/POST | `/tags` | 标签 | V1.0 |
| POST | `/sync/push` | 批量提交本地 mutation | V1.0 |
| GET | `/sync/pull?cursor=...` | 拉取增量变更 | V1.0 |
| POST | `/sync/bootstrap` | 首次/过期后的重建快照 | V1.0 |
| POST | `/ai/parse` | 自然语言转草案 | V1.0 |
| POST | `/ai/tasks/{id}/breakdowns` | 持久化任务拆解草案 | V1.0 |
| PUT | `/ai/breakdowns/{id}` | 编辑并重新校验草案 | V1.0 |
| POST | `/ai/breakdowns/{id}/apply` | 确认后整组创建步骤 | V1.0 |
| POST | `/ai/plans` | 获取计划预览 | V1.0 |
| POST | `/ai/plans/{id}/apply` | 用户确认执行 | V1.0 |
| GET | `/ai/consent` | 查看授权 | V1.0 |
| PUT | `/ai/consent` | 更新 AI 数据许可 | V1.0 |
| GET/POST | `/habits` | 习惯 | V1.1 |
| POST | `/habits/{id}/check-in` | 打卡 | V1.1 |
| GET/POST | `/goals` | 目标 | V1.1 |
| GET/POST | `/focus-sessions` | 专注记录 | V1.1 |
| GET/POST | `/notes` | 轻笔记 | V1.1 |
| GET | `/stats/weekly` | 周统计 | V1.1 |

`spec/openapi.yaml` 已包含 MVP 核心路径的机器可读初稿；本文件定义更多语义，开发前对齐二者。

`SPEC-01` 已定义任务拆解、导出和账号删除的 V1.0 契约，详见 `28-SPEC-01.md`、`../spec/openapi.yaml` 及对应 JSON Schema；这些仍是设计契约，未部署。

`AUTH-01` 将邮箱验证码申请定义为 `POST /auth/login/challenges`，已验证验证码的首次登录自动建立个人账号和 Inbox；重复申请使用 `Idempotency-Key`。`POST /auth/refresh` 单次轮换，旧令牌再次提交会撤销对应设备会话。邮箱发送需配置 SMTP STARTTLS；未配置时返回可重试服务错误。注册页面可复用验证码登录流程，独立 `/auth/register` 尚未实现。

## 3. Task 示例

### POST /tasks

```json
{
  "id": "018f4400-1234-7000-8000-000000000001",
  "list_id": "018f4400-1234-7000-8000-000000000002",
  "title": "交项目报告",
  "description": "附上技术图",
  "priority": 3,
  "due_at": "2026-11-06T07:00:00Z",
  "due_local_date": null,
  "time_zone": "Asia/Shanghai",
  "estimate_minutes": 60
}
```

响应 `201` 为 OpenAPI `Task` 对象本身；`400` 幂等键格式错误，`401` 未认证或账号已删除，`404` 清单不属于当前用户或已删除，`409` 相同幂等键对应不同请求或任务 ID 冲突，`422` 任务字段非法。错误响应使用 `error/request_id` 结构。创建任务要求有效 JWT 的 `sub`、UUID 格式 `Idempotency-Key`；owner 不从请求体读取。

### PATCH /tasks/{id}

```json
{"expected_version":7,"patch":{"title":"提交报告","priority":2}}
```

只修改明确列出的字段；对可空字段 `null` 表示清空；响应带新 version；基线已改变返回 `409 CONFLICT`，附服务器版本和可安全披露的字段差异。

## 4. 任务生命周期

完成 API：`POST /tasks/{id}/complete` body `{"expected_version":7,"occurrence_id":null,"completed_at":"2026-11-03T10:00:00Z"}`。服务端以受信时间约束进行修正，普通任务使用 status=done，循环任务必须指定发生实例或先明确 occurrence。

软删：`DELETE /tasks/{id}` 携带 `If-Match` 或 body/查询中的版本不建议混用，**统一建议 `If-Match: "v7"`**（或 `expected_version` 的专用 delete POST，如客户端不方便）。实际 OpenAPI 实施时统一方式，不允许多个端各用一个。

## 5. 同步 API

### POST /sync/push

```json
{
  "device_id": "device-uuid",
  "mutations": [
    {"mutation_id":"m1-uuid", "entity_type":"task", "entity_id":"task-uuid", "op":"upsert", "base_version":0, "payload":{"title":"买水果","list_id":"inbox-uuid"}}
  ]
}
```

响应逐项 `accepted/duplicate/conflict/rejected`，含 `entity_version/server_entity/retryable`。服务端每条 mutation 独立原子，批次不保证全部原子；客户端只能移除 `accepted/duplicate` 的 outbox 项；对冲突放入冲突队列。

### GET /sync/pull?cursor=X&limit=200

```json
{
  "changes": [{"revision":123,"change_index":0,"entity_type":"task","entity_id":"uuid","op":"upsert","version":8,"payload":{"...":"完整投影"}}],
  "next_cursor":"opaque-abc",
  "has_more":false,
  "server_time":"2026-11-03T10:00:00Z"
}
```

cursor 只作为服务端生成的 token，客户端不得自造或按时间戳求下一个；无权用户看不到其他用户数据。超过保留窗口返回 `410 CURSOR_EXPIRED`，需引导安全 bootstrap。

## 6. AI API

### POST /ai/parse

```json
{"input":"下周五下午3点提交报告，提前一天提醒", "context":{"time_zone":"Asia/Shanghai","now":"2026-11-03T06:00:00Z"},"allowed_scopes":["task_drafts"]}
```

响应 `{"intent":"create_task","drafts":[{"title":"提交报告","due_at":"...","time_zone":"Asia/Shanghai","reminders":[...]}],"assumptions":[],"unresolved_fields":[]}`。模型输出必须先 schema 校验。用户修改草稿后需以人工值为准。

### POST /ai/plans

body `{"task_ids":[...],"range_start":"...","range_end":"...","consent_scope":"tasks.calendar"}`；响应 `plan_id/expires_at/snapshot_hash/operations/conflicts/unplanned/explanation`。

### POST /ai/plans/{id}/apply

body `{"snapshot_hash":"...","accepted_operation_ids":[...],"confirmation_token":"short-lived-bound-to-plan"}`；服务端从已存储计划校验白名单操作，不信任客户端重新提交任意操作；**V1.0 要求 accepted_operation_ids 与草案全部操作一致**。若未来支持选择子集，须先重新校验并签发新的子集快照；已过期/对象版本变化返回 `409 PLAN_STALE`，不部分执行。

### SPEC-01 扩展端点

- 任务拆解：`POST /ai/tasks/{id}/breakdowns` 读取已授权任务并持久化最多 20 步草案；`PUT /ai/breakdowns/{id}` 用原 `snapshot_hash` 整份替换步骤并返回新快照；`POST /ai/breakdowns/{id}/apply` 以短期确认 token 整组写步骤。所有写端点有幂等键；任务 owner、`expected_version`、草案过期和重复提交在服务端验证，失败不产生部分步骤。
- 云端导出：`POST /me/exports` 幂等启动；状态 `pending/ready/failed/expired`；`GET /me/exports/{id}/download` 仅在 ready 且 24 小时有效期内可用。返回 `account-export-v1` JSON，`snapshot_at` 表明已提交数据边界。本地离线导出由客户端实现并标记 pending mutation。
- 账号删除：`POST /me/deletion-requests` 需要近期身份复核的 token、`acknowledge_offline_devices=true` 和幂等键。返回 `202 accepted` 后当前及其他设备会话均失效。旧设备认证或同步返回不可重试 `ACCOUNT_DELETED`；不自动清空其本地数据。备份/日志清理期限由发布前版本化政策定义。

## 7. 统一错误响应

```json
{
  "error": {"code":"VERSION_CONFLICT", "message":"任务已在其他设备修改", "details":{"entity_id":"...","server_version":8}, "retryable":false},
  "request_id":"req-uuid"
}
```

建议错误码：`VALIDATION_FAILED`, `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`, `VERSION_CONFLICT`, `IDEMPOTENCY_MISMATCH`, `CURSOR_EXPIRED`, `PLAN_STALE`, `BREAKDOWN_STALE`, `ACCOUNT_DELETED`, `EXPORT_NOT_READY`, `EXPORT_EXPIRED`, `AI_UNAVAILABLE`, `AI_SCOPE_DENIED`, `QUOTA_EXCEEDED`, `RATE_LIMITED`, `INTERNAL_ERROR`。API 错误不返回内部 SQL/密钥/堆栈。

## 8. API 质量门禁

- OpenAPI lint 通过；客户端模型由契约生成或严格手写对齐。
- 所有修改端点有 idempotency/expected_version/owner_id 约束测试。
- 统一时间与 null 语义；每次改 schema 必须更新 contract test 和 migration。
- AI API 强制最小授权并且返回可重放、可校验的草案/计划，不用自由文本作为执行业务参数。
