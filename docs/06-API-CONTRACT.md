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
| POST | `/auth/login/challenges` | 持久化排队登录邮箱验证码 | V1.0 |
| POST | `/auth/refresh` | 轮换刷新 token | V1.0 |
| POST | `/auth/step-up` | 敏感操作的近期身份复核 | V1.0 |
| POST | `/auth/step-up/challenges` | 向当前账号发送敏感操作验证码 | V1.0 |
| POST | `/auth/logout` | 使当前会话失效 | V1.0 |
| GET | `/auth/devices` | 查看自己的活跃设备会话，游标分页 | V1.0 |
| POST | `/auth/devices/{id}/revoke` | 撤销自己的指定设备会话 | V1.0 |
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

`AUTH-01` 将邮箱验证码申请定义为 `POST /auth/login/challenges`，已验证验证码的首次登录自动建立个人账号和 Inbox；重复申请使用 `Idempotency-Key`，同邮箱同键在 24 小时保留窗口内不重复排队。`POST /auth/refresh` 单次轮换，旧令牌再次提交会撤销对应设备会话。`AUTH-02` 将验证码及加密邮件队列原子提交；`202` 表示已排队，不保证已投递。独立 Worker 使用 SMTP STARTTLS 异步发送和有限重试；客户端按邮件到达情况输入验证码，未收到可在限流范围内使用新的幂等键重新申请。`AUTH-04` 要求申请携带非空 `X-Device-Id` UUID，按 15 分钟窗口限制每邮箱 3 次、每设备 5 次、每源 IP 20 次；同邮箱同幂等键重试不增加计数。`429 RATE_LIMITED` 携带秒数形式的 `Retry-After`，客户端应等待，不应换设备 ID 绕过限制。注册页面可复用验证码登录流程，独立 `/auth/register` 尚未实现。

`AUTH-05` 要求 `POST /auth/login` 同样携带非空 `X-Device-Id` UUID。合法格式但验证失败的请求按 15 分钟窗口跨邮箱累计：每设备 10 次、每源 IP 40 次；达到阈值后返回 `429 RATE_LIMITED` 和秒数形式的 `Retry-After`，即使本次验证码正确也需等待。未触发来源限额的失败仍返回统一的 `401 INVALID_CODE`，成功登录不计入失败次数。每条挑战自身的五次错误码上限继续生效。

`AUTH-06` 实现 `GET /auth/devices`、`POST /auth/devices/{id}/revoke` 与 `POST /auth/logout`。列表只返回 JWT `sub` 对应的活跃会话，按 `created_at,id` 降序游标分页，默认 50、最多 200，包含 `version` 和 `is_current`；无效游标返回 `400`。撤销和退出必须带稳定 UUID `Idempotency-Key` 与强格式 `If-Match: "<version>"`，版本来自列表。跨 owner 的目标返回 `404`；旧版本或不同键重复撤销返回 `409`；相同键重复撤销指定设备返回 `204`。当前设备退出后其 JWT 立即失效，因此同一 JWT 重试退出返回 `401`；客户端应将 `204` 或随后 `401` 都视为本地退出完成，并清除本机凭据。服务端不接受请求体 `user_id`。详见 `36-AUTH-06.md`。

`TASK-READ-01` 实现 `GET /tasks`。列表只返回 JWT `sub` 对应的未软删任务，按 `created_at,id` 降序游标分页，默认 50、最多 200；游标不透明，客户端原样传回，非法 `limit`/`cursor`/`list_id`/`status` 返回 `400`。可选 `list_id` 只接受当前 owner 未删除的清单，否则 `404 LIST_NOT_FOUND`；可选 `status` 限 `todo/in_progress/done/archived`。响应为 `items + next_cursor`，`next_cursor` 为 `null` 表示没有更多页；列表不是时间点快照，翻页期间的新写入可能出现在更早的页。详见 `37-TASK-READ-01.md`。

`TASK-UPDATE-01` 实现 `GET/PATCH/DELETE /tasks/{id}`。`GET` 只返回当前 owner 未软删任务的完整投影（含 `completed_at`/`version`），跨 owner 或已软删一律 `404 TASK_NOT_FOUND`。`PATCH` 必须带 UUID `Idempotency-Key`，body 为 `{expected_version, patch}`：patch 只允许 `title/description/priority/due_at/due_local_date/time_zone/estimate_minutes`，未列字段保持原值，可空字段 `null` 表示清空；合并后按创建规则校验，非法返回 `422`。版本不符返回 `409 VERSION_CONFLICT`，`error.details` 含 `entity_id`、`server_version` 与可安全披露的 `changed_fields`；同一键相同请求重放原响应，同一键不同请求返回 `409 IDEMPOTENCY_KEY_REUSED`。`DELETE` 必须带 `Idempotency-Key` 与 `If-Match: "vN"`，软删为 `deleted_at + version+1` 并写 `sync_changes(op='delete')` tombstone，相同键重试返回 `204`。详见 `38-TASK-UPDATE-01.md`。

`TASK-COMPLETE-01` 实现 `POST /tasks/{id}/complete` 与 `POST /tasks/{id}/reopen`。两者必须带 UUID `Idempotency-Key`，body 为 `{expected_version}`（complete 另有可空 `occurrence_id` 与 `completed_at`）。普通任务 complete 由 `todo/in_progress` 变为 `done`；`completed_at` 缺失取服务器时间，早于创建时间或晚于当前时间会被受信时间修正（钳制）。已完成任务再次 complete 返回 `409 ALREADY_COMPLETED`，归档任务返回 `422 INVALID_TASK_STATE`；循环任务必须按 TIME-01 的 occurrence 语义完成，本切片返回 `422 RECURRING_TASK_REQUIRES_OCCURRENCE`，普通任务携带 `occurrence_id` 返回 `422 INVALID_OCCURRENCE`。reopen 由 `done` 回到 `todo` 并清空 `completed_at`，未完成任务返回 `409 NOT_COMPLETED`。两者均版本化（`409 VERSION_CONFLICT`）且同键重放原响应。详见 `39-TASK-COMPLETE-01.md`。

`TASK-LIST-01` 实现 `GET/POST /lists` 与 `GET/PATCH/DELETE /lists/{id}`。列表只返回 JWT `sub` 对应的未软删清单（含 inbox 与归档清单），响应为 `items` 数组（`id/name/kind/version`）。`POST` 必须带 UUID `Idempotency-Key`，body 为 `{id, name}`，服务端固定 `kind=custom`（inbox 只由注册流程建立），重名允许；同键重放原 `201`，换键复用或 ID 冲突返回 `409`。`PATCH` 只允许 `{expected_version, patch:{name}}` 重命名（含归档清单），版本不符返回 `409 VERSION_CONFLICT`。`DELETE` 必须带 `Idempotency-Key` 与 `If-Match: "vN"`，软删写 tombstone；清单内仍有未软删任务时返回 `409 LIST_NOT_EMPTY`，inbox 返回 `422 LIST_INBOX_PROTECTED`，相同键重试返回 `204`。所有清单写入按 owner 时钟行锁串行化并写 `sync_changes(entity_type='list')`。详见 `40-TASK-LIST-01.md`。

`TASK-TAG-01A` 实现 `GET/POST /tags` 与 `GET/PATCH/DELETE /tags/{id}`（原机器契约缺失，按 docs/02 F02 的 name/color 定义补齐）。`GET /tags` 只返回 JWT `sub` 的未软删标签，按名称排序。`POST` 带 UUID `Idempotency-Key` 与 `{id, name, color?}`；名称在 owner 内大小写不敏感唯一，软删后可重建同名；同键重放原 `201`，ID/名称冲突 `409 TAG_ID_CONFLICT/TAG_NAME_CONFLICT`，非法 `422 INVALID_TAG`。`PATCH` 为 `{expected_version, patch:{name?, color?}}`，至少一项，`null` 清空 color，版本不符 `409 VERSION_CONFLICT`。`DELETE` 需 `Idempotency-Key` 与 `If-Match: "vN"`，软删写 `sync_changes(entity_type='tag')` tombstone；任务标签关联的清理由客户端按 tombstone 处理。任务打标签与按标签过滤留待 `TASK-TAG-01B`。详见 `41-TAG-01A.md`。

`TASK-TAG-01B` 实现任务标签关联：`PUT /tasks/{id}/tags` 以 `{expected_version, tag_ids}` 整份替换（最多 20 个唯一标签、空数组清空），标签必须属于 owner 且未软删；版本冲突 `409 VERSION_CONFLICT`、非法集合 `422 INVALID_TAG_SET`，任务版本 +1 并写含 `tag_ids` 的 `sync_changes`，同键重放原响应。`GET /tasks/{id}/tags` 返回任务标签；`GET /tasks?tag_id=` 在游标分页上追加标签过滤，非法/跨 owner/已软删标签分别返回 `400 INVALID_TAG_ID`/`404 TAG_NOT_FOUND`。详见 `42-TAG-01B.md`。

`TASK-STEP-01` 实现任务步骤：`GET /tasks/{id}/steps` 返回未软删步骤（按 `position,id`），`POST /tasks/{id}/steps` 带 `Idempotency-Key` 与 `{id, title, position?, estimate_minutes?}`，`PATCH /steps/{id}` 以 `{expected_version, patch}` 更新标题/完成态/位置/估算（`null` 清空估算），`DELETE /steps/{id}` 带 `If-Match: "vN"` 软删并写 `sync_changes(entity_type='step')` tombstone。步骤写入独立推进 revision 与版本，不改变所属任务版本；跨 owner 或已软删任务/步骤一律 `404`。详见 `43-STEP-01.md`。

`SYNC-PULL-01` 实现 `GET /sync/pull`：按 owner 已提交 revision 与 `change_index` 升序返回 `changes/next_cursor/has_more/server_time`；游标不透明且不基于 `updated_at`，默认 100、最大 200，非法参数 `400`。请求游标早于 `min_available_revision` 时返回 `410 CURSOR_EXPIRED`，客户端应走安全 bootstrap。拉取只返回当前 owner 的变更且不写任何状态。详见 `44-SYNC-PULL-01.md`。`sync/push` 与 `sync/bootstrap` 留待 `SYNC-PUSH-01` 与 `SYNC-BOOTSTRAP-01`。

`SYNC-PUSH-01` 实现 `POST /sync/push`：`{device_id, mutations[1..100]}` 逐条独立原子处理，返回 `accepted/duplicate/conflict/rejected` 与 `entity_version`/`server_entity`/`retryable`；`(owner, mutation_id)` 幂等，`base_version` 不符返回 `conflict`，已删除返回 `duplicate`，非法/跨 owner/未支持 entity_type 返回 `rejected`。本切片覆盖 task 的 upsert/delete；每次 accepted 推进 revision 并写 `sync_changes`/`client_mutations`。详见 `45-SYNC-PUSH-01.md`。

`SYNC-BOOTSTRAP-01` 实现 `POST /sync/bootstrap`：在 `REPEATABLE READ` 只读事务内返回 `{snapshot_cursor, next_page_token, tasks, lists}`，快照游标编码 committed revision 并可直接作为 `GET /sync/pull` 的 cursor；软删数据排除，`next_page_token` 预留为 null，快照只读。详见 `46-SYNC-BOOTSTRAP-01.md`。

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
