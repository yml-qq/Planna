# 文档包检查结果

检查日期：2026-11-03。2026-11-03 已按 Planna/.NET 10 决策更新文档；下列原始检查记录仅描述文档包生成时的结果，当前改动须以本次执行的校验结果为准。

已在当前环境实际完成：
- 27 份核心 Markdown 文档已生成且可 UTF-8 读取。
- OpenAPI YAML 可解析，版本声明为 3.1.0；全部本地 `$ref` 路径解析成功。
- 任务与 AI 计划 JSON Schema 通过 Draft 2020-12 自身结构校验。
- AI 计划示例通过 `ai-plan.schema.json` 数据校验。
- 已生成 PostgreSQL 初始化 DDL 草案、项目 AGENTS.md、8 个 AI 编码 Prompt 与同步/计划示例。
- `MANIFEST.json` 记录本目录文档和契约文件的 SHA-256；本次未验证压缩包。

未能验证、上线前必须执行：
- **未在运行中的 PostgreSQL 执行 SQL DDL/EF Core migrations**；目前只有 DDL 草案，需创建 .NET 工程后生成并测试迁移。
- **未用 OpenAPI 生成器生成客户端或服务端代码**；SPEC-01 后已用 `openapi-spec-validator` 校验 OpenAPI 3.1 文档，但运行时兼容性仍待实现期验证。
- **未编译 .NET 后端、Tauri、Android、iOS 源码**，因为本包是文档与契约，不是完整项目代码。
- **未进行 Android/iOS 真机通知、DST 跨端和商店合规审核**。
- 账号登录、真实 AI Provider、推送、支付服务均应在独立工单中落实。

文档为可开发设计基线而不是已交付应用；建议从 `prompts/00-START-PROJECT.md` 开始实施。

## 历史讨论复核后的文档检查（2026-11-03）

- 已将历史讨论中的“入口轻量、功能按需显示、AI 贯穿记录与规划”对照 PRD、UX 和路线图；以当前 .NET 10 决策覆盖历史 Rust 服务端建议。
- V1.0 AI 计划只支持整份确认；任务拆解草案/确认契约已在 `SPEC-01` 补齐。远程推送和可选模块入口列为 V1.1。
- 数据导出与账号删除为 V1.0 必交；相应 API 和离线设备回连契约已在 `SPEC-01` 冻结，备份/日志保留期限仍待隐私政策审定，功能尚未实现。
- 当前目录仍无可编译的服务端或客户端源码；静态校验结果以实际执行记录为准，未运行构建、数据库迁移或设备测试。

## SPEC-01 契约检查（2026-11-03）

- 28 份专题文档；新增任务拆解、导出和删除的 OpenAPI 路径、3 个 JSON Schema、SQL 增量草案与合成样例。
- 使用 `python -m unittest discover -s tests/contract -p 'test_*.py' -v`，运行 7 项静态契约测试并通过；覆盖 Schema 草案格式、样例、OpenAPI 引用、写入幂等参数和关键负例。
- 使用 `python -m openapi_spec_validator spec/openapi.yaml`，OpenAPI 3.1 文档通过规范校验。
- 尚未执行 EF Core migration、PostgreSQL 事务测试、服务端/三端构建或设备端验证；相关行为保持 `planned`。

## DATA-01 迁移工程检查（2026-11-04）

- 新增 .NET 10 迁移工程，将基线 SQL 纳入首个 EF Core migration，并将 SPEC-01 增量改为 `MigrationBuilder` 操作；成功生成 `server/spec01-migrations.sql`。
- `dotnet build server/Planna.slnx --no-restore` 已通过；数据库冒烟检查程序已编译。
- Docker Desktop 进程已启动，但 Linux 引擎不可连接；本机未发现 PostgreSQL 服务。因此这些数据库检查未在开发机运行，改由下述 CI 隔离库验证。
- GitHub Actions `Database migrations` [运行 41670993447](https://github.com/yml-qq/Planna/actions/runs/41670993447) 已在 PostgreSQL 16 隔离库通过：空库迁移及重复运行、跨 owner 外键、字段约束、事务回滚、重复 mutation/删除请求；`dotnet build --warnaserror` 和 `dotnet format --verify-no-changes` 也通过。此结果不覆盖尚未实现的 API 层鉴权与同步行为。

## TASK-01 创建任务写入检查（2026-11-04）

- 已实现 `POST /tasks` 与 `CreateTaskService`，使用 JWT `sub`、服务端 owner 校验、幂等结果、账户级 revision 锁及同一事务的任务/同步变更写入。
- PostgreSQL 隔离库集成检查覆盖创建、重放、同键改内容、跨 owner 清单、删除账号、并发与冲突回滚；最终 CI 运行链接见提交状态。
- [CI 运行 41761975744](https://github.com/yml-qq/Planna/actions/runs/41761975744) 已通过真实 HTTP 路由和 JWT 中间件的集成检查，覆盖鉴权、跨 owner 清单、重放和账号删除后的拒绝。登录/令牌签发与任务其他端点仍未实现。

## 开发库迁移复核（2026-11-04）

- 只读检查 `planna_db_dev` 在迁移前的 `public` 无业务表；随后执行 `dotnet ef database update --context PlannaSchemaContext`，两条迁移均完成。
- 迁移后只读检查确认 PostgreSQL 服务端版本为 18.6，`public` 有 22 张表，`__EFMigrationsHistory` 包含 `202611030001_BaselineSchema` 与 `202611030002_Spec01Contracts`。未查询或导出业务数据，也未在该库运行插入合成数据的冒烟程序。
- 当前仅支持 PostgreSQL 18，CI 已改为 18 单版本验证；早期 PostgreSQL 16 运行记录仅为历史证据。

## AUTH-01 认证切片检查（2026-11-04）

- 新增登录验证码申请、验证码登录和刷新令牌轮换端点；新增 `AuthSessions` EF Core migration、契约和隔离库 HTTP 测试。
- 验证范围包含验证码发送接口、一次性消费、错误码、自动建立个人账号、刷新轮换与旧令牌重放撤销；PostgreSQL 18 验证结果见下述 CI 记录。
- 真实 SMTP 供应商配置、投递率、邮件 outbox 与历史清理仍未验证或实现；生产凭据未加入仓库。
- [CI 运行 41794248660](https://github.com/yml-qq/Planna/actions/runs/41794248660) 在 PostgreSQL 18 上通过迁移、构建、格式与 HTTP 认证检查，覆盖邮件失败重试、五次错误码和访问令牌撤销。当前 CI 仅保留 PostgreSQL 18。
- `planna_db_dev` 已应用 `202611040003_AuthSessions`；`dotnet ef migrations list` 只读复核显示三条迁移均已应用，未运行插入合成用户的测试。

## AUTH-02 持久化登录邮件队列检查（2026-11-04）

- 新增 `202611040004_LoginEmailOutbox` EF Core migration、独立 `Planna.Worker` 和生成的 `server/outbox-migrations.sql`；挑战与加密邮件负载同一事务提交。
- 本地 `dotnet build server/Planna.slnx --no-restore --warnaserror`、`dotnet format server/Planna.slnx --verify-no-changes --no-restore` 已通过；7 项 Python 契约测试及 OpenAPI 3.1 校验已通过。
- [PostgreSQL 18 CI 运行 41851558589](https://github.com/yml-qq/Planna/actions/runs/41851558589) 已通过迁移重放、构建、格式和 HTTP 冒烟，覆盖发送故障、重试上限、租约恢复、并发领取、过期与密文篡改。
- `planna_db_dev` 已应用 `202611040004_LoginEmailOutbox`，`dotnet ef migrations list` 只读核对显示四条迁移均已应用；未在开发库插入合成账号或发送邮件。真实 SMTP 服务未配置，投递率与凭据可用性未验证。

## AUTH-03 队列信号和历史清理检查（2026-11-05）

- Worker 每分钟输出无个人信息的队列心跳；积压或终止投递失败输出错误级事件。外部告警接收方尚未配置，部署时需按 `docs/33-AUTH-03.md` 接入。
- 验证码挑战及队列记录保留 24 小时，撤销超过 30 天的设备会话才清理其刷新令牌历史；活跃会话历史保留以继续识别旧令牌重放。
- 本地 .NET 构建零警告、`dotnet format --verify-no-changes`、7 项静态契约测试和 OpenAPI 3.1 校验通过。[PostgreSQL 18 CI 运行 41855739984](https://github.com/yml-qq/Planna/actions/runs/41855739984) 已通过迁移、构建、格式、契约和隔离库冒烟，覆盖清理边界、重复运行与积压阈值。

## AUTH-04 登录申请来源限流检查（2026-11-05）

- 新增 `202611050005_AuthChallengeRateLimits` 迁移和完整审阅 SQL；只在挑战记录保存 IP/设备 HMAC 摘要，不保存明文来源。
- 本地 `dotnet build server/Planna.slnx --no-restore --warnaserror` 零警告；`dotnet format server/Planna.slnx --verify-no-changes --no-restore`、7 项 Python 契约测试和 OpenAPI 3.1 校验通过。
- [PostgreSQL 18 CI 运行 41949028398](https://github.com/yml-qq/Planna/actions/runs/41949028398) 通过迁移重放、构建、格式、契约与 HTTP 冒烟，覆盖邮箱、设备和 IP 限额、并发抢限额、伪造转发头及清理回归。`planna_db_dev` 迁移前只读核对显示第五条待应用；执行 `dotnet ef database update` 后只读复核显示五条迁移均已应用。未在该库插入合成数据或查询业务记录。

## AUTH-05 登录校验失败来源限流检查（2026-11-05）

- 新增 `202611050006_LoginFailureRateLimits` 迁移、完整审阅 SQL、Worker 的 24 小时分批清理与 `POST /auth/login` 的 `X-Device-Id`/`429 Retry-After` 契约。
- 本地 `dotnet build server/Planna.slnx --no-restore --warnaserror` 零警告，`dotnet format server/Planna.slnx --verify-no-changes --no-restore`、7 项 Python 契约测试和 OpenAPI 3.1 校验通过。[PostgreSQL 18 CI 运行 41951346325](https://github.com/yml-qq/Planna/actions/runs/41951346325) 通过迁移重放、构建、格式、契约及 HTTP 冒烟，覆盖跨邮箱设备/IP 限额、并发抢限额、正确码保留与 24 小时清理。
- `planna_db_dev` 在迁移前只读核对显示第六条待应用；执行 `dotnet ef database update` 后再次只读复核，六条迁移均已应用。未在该库插入合成数据或查询业务记录。

## AUTH-06 设备会话管理检查（2026-11-05）

- 新增 `202611050007_DeviceSessionManagement` 迁移和完整审阅 SQL；设备列表使用独立 Dapper 只读查询，撤销和退出使用 EF Core 事务写入。
- 本地 `dotnet build server/Planna.slnx --no-restore --warnaserror` 零警告，`dotnet format server/Planna.slnx --verify-no-changes --no-restore` 通过；8 项 Python 静态契约测试及 OpenAPI 3.1 校验通过。
- 本机 Docker Linux 引擎不可连接；[PostgreSQL 18 CI 运行 41984550425](https://github.com/yml-qq/Planna/actions/runs/41984550425) 已通过完整迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖 Dapper 分页、owner 隔离、版本/幂等/并发撤销和退出后访问及刷新令牌拒绝。
- `planna_db_dev` 迁移前只读核对显示第七条待应用；执行 `dotnet ef database update` 后再次只读复核，七条迁移均已应用。未在该库插入合成数据或查询业务记录。

## TASK-READ-01 任务分页读取检查（2026-11-11）

- 实现 `GET /tasks`：owner 只来自 JWT `sub`，按 `created_at,id` 降序游标分页（默认 50、最多 200、游标不透明），可选 `list_id`/`status` 过滤；读取走独立 Dapper 查询类，任务写入仍为 EF Core `CreateTaskService`，两者不混合。
- 新增 `202611110008_TaskListReadIndex` 迁移（`idx_tasks_owner_created ON tasks(owner_id, created_at DESC, id DESC) WHERE deleted_at IS NULL`）与完整审阅 SQL `server/tasks-read-migrations.sql`；`spec/openapi.yaml` 的 `GET /tasks` 增加 `400`/`404` 响应与 `TaskPage` schema，契约测试增至 9 项。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地 `dotnet build server/Planna.slnx --no-restore --warnaserror` 零警告，`dotnet format server/Planna.slnx --verify-no-changes --no-restore` 通过，9 项 Python 静态契约测试与 OpenAPI 3.1 校验通过。
- [PostgreSQL 18 CI 运行 43022227391](https://github.com/yml-qq/Planna/actions/runs/43022227391) 已通过迁移、构建、格式、静态契约与 HTTP 冒烟：覆盖未认证与已删除账号 401、非法 limit/cursor/list_id/status 与重复参数的 400、跨 owner 与已删除清单过滤的 404、软删与他人任务不泄漏、`limit=1` 全量遍历不重不漏且严格降序、默认与 `limit=200` 边界、status/list_id 过滤与过滤+游标组合、`version/completed_at/due_at/due_local_date/estimate_minutes` 投影。
- 本会话没有 `planna_db_dev` 连接信息，第八条迁移未在开发库应用；上线前需按 `docs/37-TASK-READ-01.md` 先迁移后发布。未在开发库插入合成数据或查询业务记录。

## TASK-UPDATE-01 任务详情、更新与软删检查（2026-11-11）

- 实现 `GET/PATCH/DELETE /tasks/{id}`：单条读取走 Dapper；`PATCH` 支持 `{expected_version, patch}` 版本化更新（未列字段保持、可空字段 `null` 清空、合并后按创建规则校验），版本不符返回 `409 VERSION_CONFLICT` 且 `error.details` 含 `entity_id`/`server_version`/`changed_fields`，同键重放原响应、换键复用返回 `409 IDEMPOTENCY_KEY_REUSED`；`DELETE` 要求 `Idempotency-Key` 与 `If-Match: "vN"`，软删写 `deleted_at + version+1` 与 `sync_changes(op='delete')` tombstone。所有写入沿用 owner 同步时钟行锁串行化并原子写 revision/`sync_changes`/`client_mutations`。本切片无 schema 变更、无新迁移。
- 冒烟测试首两轮 CI 因测试辅助方法的资源生命周期缺陷失败（发送请求后过早释放 `HttpRequestMessage` 内容、返回 `JsonDocument` 已释放的 `JsonElement`），已修复并推送；最终 [PostgreSQL 18 CI 运行 43034113302](https://github.com/yml-qq/Planna/actions/runs/43034113302) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、id/参数/键/If-Match 错误、跨 owner 与已软删 404、null 清空、422 校验、幂等重放与换键冲突、并发更新串行化、软删 tombstone 与 revision/变更/幂等记录的原子性、删除账号后 401。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地 `dotnet build server/Planna.slnx --no-restore --warnaserror` 零警告，`dotnet format server/Planna.slnx --verify-no-changes --no-restore` 通过，10 项 Python 静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库；本会话没有 `planna_db_dev` 连接信息，开发库未应用变更（本切片无迁移）。

## TASK-COMPLETE-01 任务完成与撤销完成检查（2026-11-11）

- 实现 `POST /tasks/{id}/complete` 与 `POST /tasks/{id}/reopen`：owner 只来自 JWT `sub`；均要求 UUID `Idempotency-Key` 与 body `{expected_version}`，complete 另有可空 `occurrence_id`/`completed_at`。状态机 `todo/in_progress → done`（写入 `completed_at`：缺失取服务器时间，早于创建时间或晚于当前时间被受信时间钳制），已完成的再次 complete 返回 `409 ALREADY_COMPLETED`，归档任务返回 `422 INVALID_TASK_STATE`；reopen 由 `done → todo` 并清空 `completed_at`，未完成任务返回 `409 NOT_COMPLETED`。循环任务返回 `422 RECURRING_TASK_REQUIRES_OCCURRENCE`（occurrence 语义留待 TIME-01），普通任务携带 `occurrence_id` 返回 `422 INVALID_OCCURRENCE`。版本不符返回 `409 VERSION_CONFLICT`（details 含 `entity_id`/`server_version`）；同键重放原 `200`，换键复用返回 `409 IDEMPOTENCY_KEY_REUSED`。写入沿用 owner 同步时钟行锁串行化并原子写 revision/`sync_changes`/`client_mutations`；本切片无 schema 变更、无新迁移。
- 首轮 CI 因冒烟对缺失 `expected_version` 期望 `400` 而失败（实际与 PATCH/POST 惯例一致返回 `422 INVALID_TASK`），修正断言后通过；最终 [PostgreSQL 18 CI 运行 43039556958](https://github.com/yml-qq/Planna/actions/runs/43039556958) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖状态机各分支、受信时间钳制（缺失/未来）、幂等重放与换键冲突、版本冲突、并发完成串行化、跨 owner/软删/循环任务/occurrence 错误、reopen 清空 `completed_at`、删除账号 401 与 revision/变更/幂等记录原子性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地 `dotnet build server/Planna.slnx --no-restore --warnaserror` 零警告，`dotnet format server/Planna.slnx --verify-no-changes --no-restore` 通过，11 项 Python 静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库；本切片无迁移。

## TASK-LIST-01 清单 CRUD 检查（2026-11-11）

- 实现 `GET/POST /lists` 与 `GET/PATCH/DELETE /lists/{id}`：列表只返回 JWT `sub` 对应的未软删清单（含 inbox 与归档清单）；`POST` 服务端固定 `kind=custom`，同键重放原 `201`、ID 冲突 `409 LIST_ID_CONFLICT`；`PATCH` 仅重命名（含归档清单）且版本化；`DELETE` 软删写 tombstone，非空清单 `409 LIST_NOT_EMPTY`、inbox `422 LIST_INBOX_PROTECTED`。清单变更写入 `sync_changes(entity_type='list')` 并沿用 owner 时钟行锁串行化。读取走独立 Dapper 查询，写入为 EF Core 服务。
- `spec/openapi.yaml` 补齐 `docs/06` 总览中缺失的 `/lists/{id}` get/patch/delete 路径，并为 `POST /lists` 增加 `400/401` 与 `additionalProperties: false`；契约测试增至 12 项。
- 首轮 CI 因冒烟在「缺幂等键」用例误传有效 key 而失败，修正后通过；最终 [PostgreSQL 18 CI 运行 43047563939](https://github.com/yml-qq/Planna/actions/runs/43047563939) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、id/键/If-Match/名称错误、跨 owner 404、归档清单重命名、非空清单与 inbox 删除保护、幂等重放与换键冲突、版本冲突 details、软删 tombstone 与 revision/变更/幂等记录原子性、删除账号 401。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地 `dotnet build server/Planna.slnx --no-restore --warnaserror` 零警告，`dotnet format server/Planna.slnx --verify-no-changes --no-restore` 通过，12 项 Python 静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库；本切片无 schema 变更、无新迁移。

## TASK-TAG-01A 标签 CRUD 检查（2026-11-11）

- 实现 `GET/POST /tags` 与 `GET/PATCH/DELETE /tags/{id}`：只返回 JWT `sub` 的未软删标签；`POST` 名称在 owner 内大小写不敏感唯一（软删后可重建同名），同键重放原 `201`、ID/名称冲突 `409 TAG_ID_CONFLICT`/`TAG_NAME_CONFLICT`；`PATCH` 支持改名/改色与 `null` 清空 color，版本化；`DELETE` 软删写 `sync_changes(entity_type='tag')` tombstone。所有标签写入沿用 owner 时钟行锁串行化并原子写 revision/`sync_changes`/`client_mutations`；读取走独立 Dapper 查询，写入为 EF Core 服务。
- `spec/openapi.yaml` 新增 `/tags` 与 `/tags/{id}` 路径及 `Tag` schema（原机器契约缺失，按 `docs/02` F02 的 name/color 语义补齐）；契约测试增至 13 项。本切片无 schema 变更、无新迁移。
- 首轮 CI 在应用启动阶段失败：`TagPatch` 同时使用自定义 `JsonConverter` 与 `[JsonUnmappedMemberHandling]`，STJ 在 kind=None 的转换器类型上抛 `Invalid JsonTypeInfo operation`；移除该特性（转换器已拒绝未知字段）后通过。最终 [PostgreSQL 18 CI 运行 43050992171](https://github.com/yml-qq/Planna/actions/runs/43050992171) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、id/键/If-Match/名称/颜色错误、跨 owner 404、大小写不敏感重名冲突、幂等重放与换键冲突、版本冲突 details、颜色清空、软删 tombstone 与 revision 完整性、删除账号 401。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、13 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。任务标签关联与按标签过滤留待 `TASK-TAG-01B`。

## TASK-TAG-01B 任务标签关联与过滤检查（2026-11-11）

- 实现 `PUT /tasks/{id}/tags`（`{expected_version, tag_ids}` 整份替换、最多 20 个唯一标签、空数组清空、标签须 owner 且未软删、任务版本 +1、同步负载含 `tag_ids`）、`GET /tasks/{id}/tags`，并在 `GET /tasks` 增加 `tag_id` 过滤（跨 owner/已软删标签 404、非法 400）。读取走 Dapper，替换走 EF Core 事务服务。
- 无 schema 变更、无新迁移；契约测试增至 14 项。
- [PostgreSQL 18 CI 运行 43052396833](https://github.com/yml-qq/Planna/actions/runs/43052396833) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、空集合、替换/子集/清空、幂等重放与换键冲突、版本冲突 details（`changed_fields=tag_ids`）、非法/跨 owner/已软删标签与 21 个标签上限、`tag_id` 过滤与错误、任务/关联/revision/mutations 原子性、删除账号 401。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、14 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。

## TASK-STEP-01 任务步骤 CRUD 检查（2026-11-11）

- 实现 `GET/POST /tasks/{id}/steps` 与 `PATCH/DELETE /steps/{id}`：步骤按 `position,id` 排序；创建/更新/软删均带幂等键，`PATCH` 版本化并支持 `is_done`/`position`/`estimate_minutes`（`null` 清空估算）；`DELETE` 带 `If-Match: "vN"` 并写 `sync_changes(entity_type='step')` tombstone。步骤写入独立推进 revision 与步骤版本，不改变所属任务版本；读取走 Dapper，写入走 EF Core 事务服务。
- `spec/openapi.yaml` 新增 `/tasks/{id}/steps` 与 `/steps/{id}` 路径及 `TaskStep` schema（原机器契约未定义步骤端点，按 `docs/02` 任务步骤语义补齐）；契约测试增至 15 项。无 schema 变更、无新迁移。
- [PostgreSQL 18 CI 运行 43053807500](https://github.com/yml-qq/Planna/actions/runs/43053807500) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、排序、创建重放/换键冲突/ID 冲突、版本冲突 details、估算清空、软删 tombstone、跨 owner/已软删任务与步骤 404、删除账号 401、revision/步骤版本/任务版本不变与幂等记录原子性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、15 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。

## SYNC-PULL-01 增量拉取检查（2026-11-11）

- 实现 `GET /sync/pull`：按 owner 已提交 revision 与 `change_index` 升序返回 `changes/next_cursor/has_more/server_time`；游标不透明（`base64url(revision:change_index)`），默认 100、最大 200，非法参数 `400`；请求游标早于 `min_available_revision` 时 `410 CURSOR_EXPIRED`。只返回当前 owner 的变更，拉取不写任何状态（不推进 revision、不写 `sync_changes`/`client_mutations`）。读取走独立 Dapper 查询。
- `spec/openapi.yaml` 的 `/sync/pull` 补充语义与 `400/401` 响应；契约测试增至 16 项。无 schema 变更、无新迁移。
- [PostgreSQL 18 CI 运行 43055217937](https://github.com/yml-qq/Planna/actions/runs/43055217937) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、按 revision/change_index 排序、跨多变更 revision 的分页不重不漏（limit=2）、结束游标重复拉取、`has_more`、非法 limit/cursor、`410` 到期窗口与 owner 隔离、只读无副作用。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、16 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。`sync/push` 与 `sync/bootstrap` 留待后续切片。

## SYNC-PUSH-01 批量 mutation 推送检查（2026-11-12）

- 实现 `POST /sync/push`：`{device_id, mutations[1..100]}` 逐条独立事务处理，返回 `accepted/duplicate/conflict/rejected` 与 `entity_version`/`server_entity`/`retryable`；以 `(owner, mutation_id)` 幂等，`base_version` 不符返回 `conflict`（含当前实体与版本，可重试），已删任务 `duplicate`，跨 owner/非法 payload/未支持 entity_type `rejected`。本切片覆盖 task upsert/delete；每次 accepted 推进 revision 并原子写 `sync_changes`/`client_mutations`。
- `spec/openapi.yaml` 的 `/sync/push` 补充语义与 `400` 响应；契约测试增至 17 项。无 schema 变更、无新迁移。
- [PostgreSQL 18 CI 运行 43056737863](https://github.com/yml-qq/Planna/actions/runs/43056737863) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖批内 accepted/duplicate/conflict/rejected 矩阵、跨 mutation 幂等重放、冲突 server_entity/retryable、删除重复、非法整批（空 device/空数组/101 条/空 mutation_id）、owner 隔离、revision/变更/幂等记录原子性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、17 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。

## SYNC-BOOTSTRAP-01 一致快照引导检查（2026-11-12）

- 实现 `POST /sync/bootstrap`：在 `REPEATABLE READ` 只读事务内读取 committed revision 与全部未软删 `tasks`/`lists`，返回 `{snapshot_cursor, next_page_token, tasks, lists}`；快照游标编码 `(revision, int.MaxValue)`，可直接交给 `GET /sync/pull` 继续增量，快照后提交的变更由 pull 返回。软删数据排除、`next_page_token` 预留为 null、快照只读。
- `spec/openapi.yaml` 补充语义与 `401` 响应；契约测试增至 18 项。无 schema 变更、无新迁移。
- [PostgreSQL 18 CI 运行 43058072416](https://github.com/yml-qq/Planna/actions/runs/43058072416) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、快照过滤与投影、owner 隔离、空 owner、快照游标与 pull 衔接（快照后 revision 7 变更可由游标增量获取）、只读无副作用。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、18 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。

## TIME-01A 循环规则与 occurrence 窗口检查（2026-11-12）

- 实现 `GET/PUT/DELETE /tasks/{id}/recurrence` 与 `GET /tasks/{id}/occurrences`：RRULE 子集（DAILY/WEEKLY/MONTHLY + INTERVAL + COUNT|UNTIL + WEEKLY BYDAY），`dtstart_local` 为规则时区本地时间并以 `timestamp without time zone` 显式绑定；`PUT` 以 `expected_version=0` 创建、`≥1` 更新，幂等重放与 `409 RECURRENCE_EXISTS`/`VERSION_CONFLICT`；`DELETE` 保留 occurrence 历史。occurrence 窗口默认 30 天、最大 90 天，幂等物化（`ON CONFLICT DO NOTHING`），DST 策略为跳过不存在本地时刻、歧义取最早 instant，物化为派生状态不推进 revision。
- `spec/openapi.yaml` 新增 `/tasks/{id}/recurrence`、`/tasks/{id}/occurrences` 与 `Recurrence`/`Occurrence` schema；契约测试增至 19 项。无 schema 变更、无新迁移。
- 迭代修复：首轮 CI 因 Npgsql 将 Unspecified DateTime 绑定为 `timestamptz` 而 500（`dtstart_local`/窗口参数），改为显式 `NpgsqlDbType.Timestamp`；随后两轮修正 DST 窗口与账目断言（dtstart 之后的候选数、保留的 fall 规则）。最终 [PostgreSQL 18 CI 运行 43063331390](https://github.com/yml-qq/Planna/actions/runs/43063331390) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、规则校验/创建/更新/删除/幂等/版本冲突、周规则窗口计数与排序、春季 DST 跳过、秋季歧义单一最早 instant、窗口边界、owner 隔离与 revision/变更/幂等记录原子性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、19 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。occurrence 完成与 after_completion 规则留待 `TIME-01B`。

## TIME-01B 按 occurrence 完成检查（2026-11-12）

- 实现 `POST /tasks/{id}/complete` 的循环任务分支：存在 RRULE 规则时要求 `occurrence_id`，完成写 `task_occurrences.status='done'`、`completed_at`（受信时间钳制到 `[任务创建时间, 当前时间]`）与 `version+1`；响应返回 `Occurrence`（普通任务仍返回 `Task`，OpenAPI 200 为 `oneOf [Task, Occurrence]`）；每次完成推进 revision 并写 `sync_changes(entity_type='occurrence')` 与 `client_mutations`。缺失 occurrence 返回 `422 RECURRING_TASK_REQUIRES_OCCURRENCE`，非法/跨任务 occurrence 返回 `422 INVALID_OCCURRENCE`，已完成返回 `409 ALREADY_COMPLETED`。
- 迭代修复：首轮 CI 因重放分支固定按 `TaskReadView` 反序列化、无法重放 occurrence 完成而失败；改为按存储 JSON 形态（`original_local_start` 判定）分派后通过。最终 [PostgreSQL 18 CI 运行 43065790621](https://github.com/yml-qq/Planna/actions/runs/43065790621) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖规则创建→occurrence 物化→完成端到端、幂等重放、重复完成、受信时间、随机/缺失 occurrence、非循环任务、跨 owner 与删除账号、revision/变更/幂等记录原子性。
- `spec/openapi.yaml` 的 complete 200 响应改为 `oneOf [Task, Occurrence]` 并补充说明；契约测试保持 19 项并通过。无 schema 变更、无新迁移。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、19 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。`after_completion`/法定工作日规则与 occurrence 撤销留待 `TIME-01C`。

## TIME-01C after_completion 规则检查（2026-11-12）

- 实现 `kind="after_completion"` 规则（`interval_days` 1..3650、`rrule` 必空；与 rrule 混用 `422 INVALID_RECURRENCE`）：任务按任务完成（不接受 occurrence_id），完成时在同一事务内生成下一次 occurrence（完成时刻在规则时区的本地日期 + N 天，时刻取 `dtstart_local`，DST 不存在时刻跳过，唯一键幂等）。`legal_workday` 仍 `422 UNSUPPORTED_RECURRENCE`。
- `spec/openapi.yaml` 的 Recurrence/put 请求更新为双 kind 并加入 `interval_days`；契约测试保持 19 项并通过。无 schema 变更、无新迁移。
- 迭代修复：旧递归冒烟把 `after_completion` 当 unsupported（改测 `legal_workday`）；新冒烟重复完成未使用当前任务版本（改为 v2）。最终 [PostgreSQL 18 CI 运行 43069324140](https://github.com/yml-qq/Planna/actions/runs/43069324140) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖规则校验/双 kind、完成生成下一次 occurrence（固定本地日期与 UTC instant）、重放不重复、reopen 后再完成追加、跨 owner、删除账号与 revision/变更记录一致性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、19 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。法定工作日、occurrence 撤销与编辑范围留待 `TIME-01D`。

## AI-01 自然语言解析检查（2026-11-12）

- 实现 `POST /ai/parse`：`IAiParseProvider` 抽象 + 确定性规则 Provider（docs/09「先规则解析」），支持 `今天/明天/后天`、`下周X`、`YYYY-MM-DD` 与常用时间短语，输出草稿（不写库）；缺少/非法 context、超长输入 `400`，scope 不含 `task_drafts` `403 AI_SCOPE_DENIED`，`AI_PROVIDER=none` `503 AI_UNAVAILABLE`；提示注入文本仅作为标题文本处理。
- `spec/openapi.yaml` 的 `/ai/parse` 补充 400/401 与语义描述；契约测试增至 20 项。无 schema 变更、无新迁移、无 AI 凭据进入仓库。
- [PostgreSQL 18 CI 运行 43070714045](https://github.com/yml-qq/Planna/actions/runs/43070714045) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、校验/时区/scope 错误、provider 关闭 503、相对/绝对/下周日期、仅有日期/无日期的 assumptions 与 unresolved、提示注入不改数据（任务数、revision、sync_changes、client_mutations 均不变）。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、20 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。真实 LLM Provider（超时/重试/限流/预算/审计）、AI-02 拆解草案与 AI-03/04 计划留待后续切片。

## AI-02 任务拆解草案检查（2026-11-12）

- 实现 `POST /ai/tasks/{id}/breakdowns`（规则拆解 1..20 步并持久化草案，`expected_version` 校验任务版本）、`PUT /ai/breakdowns/{id}`（整份替换步骤、重校验并轮换 `snapshot_hash`/确认 token）、`POST /ai/breakdowns/{id}/apply`（校验 owner/状态/1 小时过期/快照/token 哈希/任务版本后，同一事务创建全部步骤、推进一次 revision、按步骤写 `sync_changes`、写 `ai_action_audits` 与幂等结果；失败不产生部分步骤）。草案与 token 仅存哈希；同键重放返回原结果。
- `spec/openapi.yaml` 三个端点补充语义、400/401 与 apply 幂等说明；契约测试增至 21 项。无 schema 变更、无新迁移、无凭据入库。
- 迭代修复：首轮创建重放应返回 201（改为按操作映射状态）；次轮冒烟对同一响应重复读取内容导致 `ObjectDisposedException`（改一次读取复用）。最终 [PostgreSQL 18 CI 运行 43073409894](https://github.com/yml-qq/Planna/actions/runs/43073409894) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、任务/版本校验、创建/编辑/应用重放、旧 token/快照拒绝、非法步骤、过期草案、重复应用、跨 owner、步骤原子写入与 revision/变更/审计一致性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、21 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。AI-03/04 计划与配额、consent 持久化留待后续切片。

## AI-03/04 计划预览与应用检查（2026-11-12）

- 实现 `POST /ai/plans`（确定性排程：顺序时间块、`estimate_minutes` 缺省 30、范围 ≤ 7 天、最多 20 个操作；与既有 `task_time_blocks` 重叠的任务进 `conflicts` 且不排程，超范围/超操作数进 `unplanned_task_ids`；计划持久化 30 分钟有效，不写业务数据）与 `POST /ai/plans/{id}/apply`（校验 owner、状态、过期、快照、token 哈希、accepted 操作集合完全一致与每个任务 `expected_version`；通过后同一事务创建全部时间块、推进一次 revision、按块写 `sync_changes(entity_type='time_block')`、写 `ai_action_audits(plan_apply)` 与幂等结果；失败 `409 PLAN_STALE` 不部分执行，同键重放同一结果）。
- `spec/openapi.yaml` 两个端点补充语义与 400/401/404/422 响应；契约测试增至 22 项。无 schema 变更、无新迁移、无凭据入库。
- [PostgreSQL 18 CI 运行 43074823842](https://github.com/yml-qq/Planna/actions/runs/43074823842) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、范围/任务校验、冲突与 unplanned、错误快照/token/操作集合拒绝、原子时间块写入、重放、陈旧任务版本、过期计划、跨 owner、删除账号与 revision/变更/审计一致性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、22 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。多操作类型、consent 与配额限流留待后续切片。

## PRIV-EXPORT-01 云端导出检查（2026-11-12）

- 实现 `POST /me/exports`（幂等创建；`REPEATABLE READ` 快照内导出 account/lists/tasks/steps/tags/reminders/time_blocks/ai_consents，`source=cloud`、`pending_mutation_ids=[]`，MVP 同步生成 `ready` job 返回 202）、`GET /me/exports`、`GET /me/exports/{id}`、`GET /me/exports/{id}/download`（仅 owner、ready 且 24h 内；过期 `410 EXPORT_EXPIRED`、未就绪 `409 EXPORT_NOT_READY`、跨 owner `404`）。新增迁移 `202611120009_AccountExportPayload`（`account_exports.payload jsonb`）与审阅 SQL `server/export-migrations.sql`。
- `spec/openapi.yaml` 补充导出语义与 400/401/404/409/410；契约测试增至 23 项（含 `account-export.schema.json` 引用与 `AccountExportJob` 必填字段）。
- [PostgreSQL 18 CI 运行 43076494283](https://github.com/yml-qq/Planna/actions/runs/43076494283) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、快照内容（各表数据、排除软删）、同键重放、列表/状态、附件下载内容、跨 owner、过期、未就绪、非法/缺失 id、删除账号，以及导出不推进 revision/不写业务变更。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、23 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。账号删除（PRIV-DELETE-01）、step-up、对象存储与清理任务留待后续切片。

## TIME-01D occurrence 撤销完成检查（2026-11-12）

- 扩展 `POST /tasks/{id}/reopen`：RRULE 任务必须指定 `occurrence_id` 并撤销已完成的 occurrence（`done → todo`、清空 `completed_at`、版本 +1，任务行不变）；缺失 `occurrence_id` → `422 RECURRING_TASK_REQUIRES_OCCURRENCE`，非法/跨任务 → `422 INVALID_OCCURRENCE`，未完成 → `409 NOT_COMPLETED`；普通任务保持任务级 reopen；响应为 `oneOf [Task, Occurrence]`，同键重放原响应。
- `spec/openapi.yaml` 更新 reopen 请求/响应与语义；契约测试保持 23 项并通过。无 schema 变更、无新迁移。
- [PostgreSQL 18 CI 运行 43077888174](https://github.com/yml-qq/Planna/actions/runs/43077888174) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、规则→occurrence→完成→撤销端到端、重放、重复撤销、非法/缺失 occurrence、非循环任务 reopen、跨 owner、删除账号与 revision/变更记录一致性。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、23 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。`legal_workday`、提醒调度与编辑范围留待 `TIME-01E`。

## PRIV-STEPUP-01 敏感操作复核检查（2026-11-12）

- 实现 `POST /auth/step-up/challenges`（6 位一次性码只存 HMAC 哈希、10 分钟有效、每账号 15 分钟 3 次、同键重放原过期时间不重发、`202` 不泄露验证码）与 `POST /auth/step-up`（只接受最新未消费挑战、最多 5 次尝试、成功返回绑定 owner 与 `account_delete` 用途的 10 分钟 HMAC 签名 token）。
- 新增迁移 `202611120010_StepUpChallenges`（`auth_step_up_challenges` + `auth_step_up_email_outbox`，租赁/退避/5 次上限）与审阅 SQL `server/stepup-migrations.sql`；Worker 新增 `StepUpEmailWorker` 消费队列，登录验证码队列与限流不受影响。
- `spec/openapi.yaml` 补充语义/400/422 与 token 约束；契约测试增至 24 项。迭代修复：移除 API 中误注册的仅 Worker 依赖 `StepUpEmailDispatcher`（DI 校验失败）；挑战过期时间截断到微秒使重放响应与首次一致。
- [PostgreSQL 18 CI 运行 43081769119](https://github.com/yml-qq/Planna/actions/runs/43081769119) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、队列投递与幂等重放、验证码消费、token 绑定/篡改/过期、过期挑战、尝试上限、挑战限流、删除账号，以及不写 revision/`sync_changes`/`client_mutations`。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、24 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。账号删除（`PRIV-DELETE-01`）留待后续切片。

## PRIV-DELETE-01 账号删除检查（2026-11-12）

- 实现 `POST /me/deletion-requests`（需登录 + `Idempotency-Key`）：要求绑定 owner/`account_delete` 用途的 step-up token 与 `acknowledge_offline_devices=true`；同一事务标记 `users.deleted_at`、撤销全部 `device_sessions`、写入 `account_deletion_requests(status='accepted', policy_version='account-deletion-v1')` 与幂等结果；返回 `202` `account-deletion` 契约对象。无效 token `403 STEP_UP_REQUIRED`、ack 缺失 `422`、缺幂等键 `400`；同键重放原结果，删除后旧设备认证/同步/导出/重试一律 `401 ACCOUNT_DELETED`；删除不写 revision/`sync_changes`。
- `spec/openapi.yaml` 更新删除端点语义与 400/422；契约测试增至 25 项（含 `account-deletion.schema.json` 校验）。无 schema 变更、无新迁移。
- [PostgreSQL 18 CI 运行 43084137323](https://github.com/yml-qq/Planna/actions/runs/43084137323) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、缺幂等键、无效 token、未确认离线设备、删除原子性（账号/会话/请求/幂等记录）、旧 token 与第二设备回连被拒、revision/同步日志不变。迭代修复：清理前序 step-up 队列遗留的 pending 邮件，避免跨冒烟串码。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、25 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。删除后清理作业与保留策略（`PRIV-CLEANUP-01`）留待后续切片。

## AI-CONSENT-01 AI 数据授权检查（2026-11-12）

- 实现 `GET /ai/consent`（`{allowed_scopes, updated_at}`，未授权为空）与 `PUT /ai/consent`（`Idempotency-Key`，白名单 `task_drafts/plan_preview/task_notes/calendar_free_slots`，整份替换、空数组撤销、未知/重复 scope `422 INVALID_SCOPE`、同键重放原视图）；`POST /ai/parse` 的 `allowed_scopes` 必须为白名单、含 `task_drafts` 且全部包含在持久化授权中，否则 `403 AI_SCOPE_DENIED`。
- `spec/openapi.yaml` 新增 `/ai/consent` 与 `AiConsent`/`AiConsentScope` schema；契约测试增至 26 项。无 schema 变更、无新迁移；授权不写 revision/`sync_changes`，随账号导出并级联删除。
- 迭代修复：`ai_consents.updated_at` 微秒精度导致 PUT 响应与 GET 不一致（更新时截断到微秒）。
- [PostgreSQL 18 CI 运行 43086547322](https://github.com/yml-qq/Planna/actions/runs/43086547322) 已通过迁移重放、构建、格式、静态契约与 HTTP 冒烟，覆盖鉴权、初始空授权、幂等重放、scope 交集与拒绝、撤销即时生效、未知 scope、缺幂等键、跨 owner 隔离、删除账号与不写同步状态。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、26 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。真实 Provider 的细粒度 scope 与“仅本次”授权审计留待后续切片。

## PRIV-CLEANUP-01 删除后保留期清理检查（2026-11-12）

- 实现 `AccountCleanupService.RunOnceAsync(now, retention)`：按外键顺序在单事务内清理超过保留期的已接受删除请求对应账号数据（任务/清单/标签/步骤/提醒/时间块/同步/幂等/授权/导出/审计/AI 计划与草案/step-up 队列/登录挑战与邮件/refresh 历史/设备会话），保留 `users` 墓碑、`account_deletion_requests` 审计并写 `completed_at`；失败回滚并写 `last_error_code`。Worker 新增 `PrivacyCleanupWorker`（`PRIVACY_DELETION_RETENTION_DAYS` 默认 30、`PRIVACY_CLEANUP_INTERVAL_MINUTES` 默认 60）。
- 无 schema/API/OpenAPI 变更、无新迁移。迭代修复：Npgsql 打开连接后 `ConnectionString` 会移除密码，冒烟改为传入原始连接串。
- [PostgreSQL 18 CI 运行 43088906012](https://github.com/yml-qq/Planna/actions/runs/43088906012) 已通过迁移重放、构建、格式、静态契约与全部冒烟，新增覆盖：40 天与恰好 30 天请求被清理、近期账号不受影响、墓碑/审计保留、各表清零与会话刷新历史清理、重复运行幂等。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、26 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。对象存储/备份/日志清理与保留策略仍需运维与法务配套。

## SEC-01A 服务端安全基线检查（2026-11-12）

- 实现统一安全头（`X-Content-Type-Options`/`X-Frame-Options`/`Referrer-Policy`，默认 `Cache-Control: no-store`）、未处理异常安全兜底（保留框架 `BadHttpRequestException` 的 400/413 语义、500 不含堆栈）、1 MiB 请求体上限（`413 PAYLOAD_TOO_LARGE`）、`/ai/*` 按 owner 固定窗口限流（默认 60 次/分钟，`AI_RATE_LIMIT_PER_MINUTE` 可配置，超限 `429 RATE_LIMITED` + `Retry-After`）。
- `spec/openapi.yaml` 为 7 个 AI 操作补充 429；契约测试同步更新（仍 26 项）。无 schema 变更、无迁移。
- 迭代修复：大小限制中间件短路导致 413 响应缺少安全头（将安全头中间件置于最外层）；异常兜底曾把框架 400 变成 500（按 `BadHttpRequestException.StatusCode` 映射）。
- [PostgreSQL 18 CI 运行 43092508558](https://github.com/yml-qq/Planna/actions/runs/43092508558) 已通过迁移重放、构建、格式、静态契约与全部冒烟，新增覆盖：健康/401/413/429 安全头、错误体无内部诊断、超限请求不写业务状态、按 owner AI 限流与 `Retry-After`。
- 本机仍无 PostgreSQL 服务且 Docker Linux 引擎不可连接，未在本地执行隔离库冒烟；本地构建零警告、format 通过、26 项静态契约测试与 OpenAPI 3.1 校验通过。未连接或修改任何生产库。正式 `SEC-01` 审核（依赖漏洞/配置审计/渗透测试/多实例限流）仍待办。

## DESK-01A 桌面离线核心检查（2026-11-12）

- 新增 `apps/desktop/core`（Rust + rusqlite `bundled`）：本地 SQLite schema（lists/tasks/outbox/sync_state）、`create_task`/`update_task` 在单事务内写本地任务与 outbox、`mutation_id` 幂等重放、版本冲突、`apply_pull` 按实体版本单调应用 upsert/delete（墓碑 + `skipped_stale`）、`pull_cursor` 持久化；9 项 `cargo test` 覆盖原子写、幂等、校验/FK 回滚、冲突、失败计数、拉取与重开持久化。
- 新增 `.github/workflows/desktop.yml`（windows-latest：`cargo fmt --check`、`clippy -D warnings`、`cargo test`）；本地 `cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、`cargo test` 全部通过（9 passed）。
- [Desktop core CI 运行 43093895480](https://github.com/yml-qq/Planna/actions/runs/43093895480) 与 [Server integration 运行 43093895420](https://github.com/yml-qq/Planna/actions/runs/43093895420) 均 **success**；未改动服务端契约。
- 未在本机执行 Tauri 打包或真机/桌面冒烟（Tauri 外壳与 Vue UI 属 `DESK-01B`）；本切片不声称桌面应用已可运行。

## DESK-01B 桌面 Web UI 检查（2026-11-12）

- 新增 Vue 3 + TypeScript + Vite 应用（`apps/desktop/src`）：`DesktopBridge` 边界（`MemoryBridge`/`TauriBridge`/`createBridge`）、任务 store（刷新/新增/错误映射/去重）、最小任务页与浅深色样式；Vitest 6 项单测覆盖桥过滤排序/幂等/校验、store 顺序、空/超长标题不发请求、加载与保存失败不污染状态、重复 id 替换。
- 本地验证：`pnpm install --frozen-lockfile`（exit 0）、`pnpm test`（6 passed）、`pnpm build`（`vue-tsc -b` 严格类型检查 + `vite build` 成功）；`pnpm-workspace.yaml` 显式忽略 esbuild 安装脚本以免 CI 构建脚本策略失败。
- `.github/workflows/desktop.yml` 新增 `desktop-web-ui` 作业（ubuntu、pnpm 11、Node 24）。
- **CI 已完成**：早先提交 `4e4233a` 的运行在 GitHub Actions 侧排队近 1 小时后仍 0 job（外部容量问题）；改用最新提交 `1ea4b02` 触发后：[Desktop core 43102722257](https://github.com/yml-qq/Planna/actions/runs/43102722257)（Rust core + Desktop web UI 两个作业）与 [Server integration 43102722183](https://github.com/yml-qq/Planna/actions/runs/43102722183) 均 **success**（`pnpm install --frozen-lockfile`、`pnpm test`、`pnpm build`、`cargo fmt/clippy/test`、服务端迁移与冒烟）。
- 未连接生产库、未提交凭据；Tauri 外壳与同步调度仍属 `DESK-01C`。

## DESK-01C Tauri 外壳检查（2026-11-12）

- 新增 `apps/desktop/src-tauri`（Tauri 2 独立 workspace）：窗口/CSP/能力配置（仅 `core:default`）、生成的 PNG/ICO 图标、`AppState { Mutex<Store> }` 与命令 `list_tasks`/`create_task`/`pending_mutations`（本地任务与 outbox 同事务；重复 id 返回 `DUPLICATE_MUTATION`），启动时打开 app data 目录的 `planna.db` 并确保 inbox 清单。
- 本地验证：`cargo check`（Tauri 2.12 + webview2-com 0.39，2m08s）、`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings` 全部通过；未在本机执行 `tauri build` 安装包或真实窗口冒烟。
- 桌面 CI 新增 `Windows Tauri shell`（fmt/clippy/check + rust-cache）。[Desktop core 43103429108](https://github.com/yml-qq/Planna/actions/runs/43103429108) 三个作业（Windows Rust core / Windows Tauri shell / Desktop web UI）与 [Server integration 43103429062](https://github.com/yml-qq/Planna/actions/runs/43103429062) 均 **success**。
- 未连接生产库、未提交凭据；登录/同步调度与安装包签名留待 `DESK-01D`。

## DESK-01D 离线同步引擎检查（2026-11-12）

- core 新增 `sync` 模块：`SyncTransport` 可注入的 `sync_once`（最多 100 条 outbox push、accepted/duplicate 清空、conflict/rejected 保留并计次、逐页 pull 直到 `has_more=false` 或页预算、透明 cursor 持久化、版本单调/墓碑由 `apply_pull` 保证、传输失败不丢 outbox）；outbox 新增 `base_version`（旧库幂等补列）。
- src-tauri 新增 `HttpTransport`（reqwest blocking + rustls，Bearer + `/sync/push`、`/sync/pull`）与 `run_sync` 命令；本地 `cargo test`（store 9 + sync 7 = 16 项）、`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、shell `cargo check` 均通过。
- [Desktop core 43105576627](https://github.com/yml-qq/Planna/actions/runs/43105576627) 三个作业（Windows Rust core / Windows Tauri shell / Desktop web UI）与 [Server integration 43105576757](https://github.com/yml-qq/Planna/actions/runs/43105576757) 均 **success**；服务端契约无变更。
- 未连接生产库、未提交凭据；登录/token 存储、自动重试调度、`CURSOR_EXPIRED` bootstrap 与安装包签名留待 `DESK-01E`。

## DESK-01E 同步重试与桌面登录检查（2026-11-12）

- core：新增 `TransportError { Retryable, CursorExpired, Fatal }`、注入式退避的 `sync_with_retry`、`Store::clear_cursor`；`CursorExpired` 自动清游标并从无游标重拉一次，连续过期返回错误。测试新增 4 项（core 合计 **20 项**：store 9 + sync 11），覆盖可重试两次后成功（退避 `[0,1]`）、Fatal 不重试且保留 outbox、游标恢复、连续过期报错。
- src-tauri：`HttpTransport` 按状态码分类（410/429/5xx/其他）；`run_sync` 三次尝试 + 0.5s/1s 退避；新增 `request_login_code`、`login_with_code`（refresh token 存 OS 凭据库 `keyring`，仅返回 access token）、`restore_session`（401 清本地 refresh 并 `SESSION_EXPIRED`）、`sign_out_local`。
- 本地验证：`cargo test`（20 项）、`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`（core 与 shell）、shell `cargo check` 全部通过。
- [Desktop core 43107671168](https://github.com/yml-qq/Planna/actions/runs/43107671168)（Windows Rust core / Windows Tauri shell / Desktop web UI）与 [Server integration 43107670927](https://github.com/yml-qq/Planna/actions/runs/43107670927) 均 **success**；服务端契约无变更。
- 未连接生产库、未提交凭据；登录界面、自动刷新/后台调度、服务端登出撤销与安装包签名留待 `DESK-01F`。

## DESK-01F 桌面登录 UI 检查（2026-11-12）

- 扩展 `DesktopBridge`（`requestLoginCode/login/restoreSession/signOut/runSync`，Memory 与 Tauri 两种实现）；新增 `session` store（邮箱/验证码校验、内存 access token、按 `expiresIn` 提前 60s 注入式刷新定时器、启动恢复、登出取消定时器、手动 sync 与 `NOT_SIGNED_IN/SYNC_FAILED` 错误映射）；`App.vue` 增加登录面板/同步摘要/退出，base URL 与随机设备 ID 存 `localStorage`（非机密）。
- 前端测试增至 **12 项**（新增 6 项会话：验证码请求、登录与 `3_540_000ms` 提前刷新、定时刷新轮换 token、登出取消、启动恢复成功/失败、同步错误），`pnpm build`（vue-tsc 严格检查 + vite）通过；无 Rust/服务端契约变更。
- [Desktop core 43109615757](https://github.com/yml-qq/Planna/actions/runs/43109615757)（Windows Rust core / Windows Tauri shell / Desktop web UI）与 [Server integration 43109615758](https://github.com/yml-qq/Planna/actions/runs/43109615758) 均 **success**。
- 未连接生产库、未提交凭据；后台/网络恢复自动调度、服务端登出撤销、安装包签名与真机冒烟留待 `DESK-01G`。

## DESK-01G 后台同步与登出撤销检查（2026-11-12）

- 前端：登录/恢复后按默认 5 分钟启动周期同步（`scheduleInterval` 可注入），登出/恢复失败取消，busy 时跳过，`autoSync:false` 可关闭；`signOut(baseUrl, token)` 有 token 调 `sign_out_server`、无 token 调 `sign_out_local`。
- Rust 外壳：新增 `sign_out_server`（`GET /auth/devices?limit=50` 找 `is_current` → `POST /auth/devices/{id}/revoke` + `If-Match: "v{version}"` + 幂等键 → 清 refresh token；无当前会话仅清本地）。
- 前端测试增至 **14 项**（新增后台同步创建/触发/登出取消、禁用后台同步），`pnpm build` 通过；`cargo fmt/clippy/check` 通过；无服务端契约变更。
- [Desktop core 43111436820](https://github.com/yml-qq/Planna/actions/runs/43111436820)（Windows Rust core / Windows Tauri shell / Desktop web UI）与 [Server integration 43111436800](https://github.com/yml-qq/Planna/actions/runs/43111436800) 均 **success**。
- 未连接生产库、未提交凭据；`tauri build` 安装包/签名、网络恢复触发与真机窗口冒烟留待 `DESK-01H`。

## DESK-01H 网络恢复与发布构建检查（2026-11-12）

- 前端：登录/恢复后订阅 `online` 事件（可注入 `watchOnline`），网络恢复且持有 access token 时立即同步；登出/恢复失败退订。前端测试增至 **15 项**（新增恢复后同步一次、登出退订），`pnpm build` 通过。
- Rust：`cargo build --release`（Tauri 2.12 + reqwest/keyring/rusqlite）在本地 **3m08s 成功**（exit 0），验证桌面应用二进制可编译链接；未执行 `tauri build` 打包/签名。
- 文档：`67-DESK-01H.md` 给出发布清单（前端/Rust 校验、`cargo tauri build` 三平台产物、Authenticode/Developer ID/notarization、updater 公钥与私钥管理、干净系统安装/升级/卸载冒烟、商店合规），明确签名凭据不得入库。
- [Desktop core 43113570657](https://github.com/yml-qq/Planna/actions/runs/43113570657)（Windows Rust core / Windows Tauri shell / Desktop web UI）与 [Server integration 43113570578](https://github.com/yml-qq/Planna/actions/runs/43113570578) 均 **success**；无服务端契约变更。
- 未连接生产库、未提交凭据；签名安装包与自动更新（需发布凭据）为 `DESK-01I` 部署任务。

## HAB-01A 习惯 CRUD 检查（2026-11-12）

- 新增迁移 `202611130001_Habits`（`habits` 表：标题 1..200、`daily/weekly/weekdays`、weekly 唯一 weekday 约束、目标 1..1000、单位 1..20、owner 复合唯一/级联），审阅 SQL `server/habit-migrations.sql`。
- 端点：`GET/POST /habits`、`GET/PATCH/DELETE /habits/{id}`（幂等键；PATCH 版控；DELETE 使用 `If-Match: "vN"` 软删除）；校验 IANA 时区、weekdays 排序去重、非 weekly 清空 weekdays、`paused` 切换 `paused_at`；账号删除清理作业加入 `habits`。
- 习惯本切片不写 revision/`sync_changes`（导出/同步留 `HAB-01B`）；幂等记录与请求哈希保证重放一致。
- `spec/openapi.yaml` 新增 5 个端点与 `Habit`/`HabitPatch` schema；契约测试增至 **27 项**并通过。
- [Server integration 43115731827](https://github.com/yml-qq/Planna/actions/runs/43115731827) 与 [Desktop core 43115731923](https://github.com/yml-qq/Planna/actions/runs/43115731923) 均 **success**，覆盖迁移重放、Habit 冒烟（鉴权/校验/重放/版控/跨 owner/删除）与既有服务端测试。
- 未连接生产库、未提交凭据；打卡日志、连续天数、习惯同步/导出与客户端打卡 UI 留待 `HAB-01B`。

## HAB-01B 打卡日志检查（2026-11-12）

- 新增迁移 `202611130002_HabitLogs`（`habit_logs`：`(owner_id, habit_id, log_date)` 唯一、quantity 0..1000、status `done/skipped`、FK 级联），审阅 SQL 更新为 `server/habit-migrations.sql`。
- 端点：`PUT /habits/{id}/logs/{date}`（`Idempotency-Key`，同日原地 upsert 并版本 +1，显式日期即用户补签，缺省 quantity=1/status=done）；`GET /habits/{id}/logs?from&to`（必填、含端点、≤366 天、按日期升序）。账号删除清理作业先删 `habit_logs` 再删 `habits`，`AccountCleanupSmoke` 已扩展覆盖。
- `spec/openapi.yaml` 新增两个端点与 `HabitLog` schema；契约测试增至 **28 项**并通过。日志暂不写 revision/`sync_changes`（与 HAB-01A 一致）。
- 迭代修复：冒烟窗口宽度误写为 2 个月（预期 422 却合法），改为超过 366 天后通过。
- [Server integration 43119204817](https://github.com/yml-qq/Planna/actions/runs/43119204817)（迁移重放 + Habit/HabitLog 冒烟 + 清理冒烟）与 [Desktop core 43119204667](https://github.com/yml-qq/Planna/actions/runs/43119204667) 均 **success**。
- 未连接生产库、未提交凭据；连续天数规则、习惯同步/导出 schema v2 与客户端打卡 UI 留待 `HAB-01C`。

## HAB-01C 连续天数检查（2026-11-12）

- 定义并实现 streak 规则（补齐 F06 未定部分）：以显式 `through` 本地日期为最后一天，只统计 `log_date <= through`；达标 = `done && quantity >= target_quantity`，`skipped` 与低量 `done` 均为断点；`current_streak` 从 through 向前连续达标数，`longest_streak` 为截至 through 的最长连续段；暂停/节假日无例外。
- 新增只读端点 `GET /habits/{id}/streak?through=YYYY-MM-DD`（缺/非法日期 400、跨 owner/已删习惯 404、未登录 401），返回 `HabitStreak`；无 schema 变更、无迁移、不写幂等记录。契约测试保持 **28 项**并通过。
- 冒烟扩展：8 天日志构造两段连续（01-02、05-08）与断点（03 低量、04 跳过）；through=11-08 → 4/4、through=11-03 → 0/2、through=11-05 → 1/2；缺参数/非法日期 400、随机习惯 404、删除账号后 401。
- 迭代修复：streak 的跨 owner 断言在删除外账号后执行，改为断言 `ACCOUNT_DELETED`。
- [Server integration 43122545563](https://github.com/yml-qq/Planna/actions/runs/43122545563) 与 [Desktop core 43122545668](https://github.com/yml-qq/Planna/actions/runs/43122545668) 均 **success**。
- 未连接生产库、未提交凭据；习惯同步/导出与客户端打卡留待 `HAB-01D`。

## HAB-01D 导出 v2 检查（2026-11-12）

- 新增 `spec/account-export-v2.schema.json`（`schema_version: account-export-v2`；`data` 必填新增 `habits`/`habit_logs`，含 habit 与 habit_log 子 schema）；v1 保留供历史参考。
- 导出服务在同一 `REPEATABLE READ` 快照内新增习惯（排除软删）与日志查询，输出 v2；`log_date` 以 `yyyy-MM-dd` 序列化。OpenAPI 下载引用切换为 v2；示例 `account-export-local/cloud.json` 更新为 v2。
- 契约测试保持 **28 项**并通过（示例按 v2 校验，cloud `pending_mutation_ids` 约束保留）。
- 导出 smoke 增加习惯/日志 fixture 并断言版本、数量与字段；既有任务/清单/标签/提醒/时间块/授权断言不变。
- 本地验证：`dotnet build --warnaserror` 0 警告 0 错误、`dotnet format` 通过、契约测试 28/28、OpenAPI 校验 OK。
- [Server integration 43154224559](https://github.com/yml-qq/Planna/actions/runs/43154224559) 与 [Desktop core 43154224514](https://github.com/yml-qq/Planna/actions/runs/43154224514) 均 **success**。
- 未连接生产库、未提交凭据；习惯/日志的 push 同步、客户端打卡与提醒留待 `HAB-01E`。

## HAB-01E 习惯同步 push 检查（2026-11-12）

- `/sync/push` 新增 `habit`（创建/部分合并更新/软删墓碑，版本校验与 conflict 服务器实体）与 `habit_log`（同日唯一创建、同日冲突 retryable、按版本更新、不支持删除、删除习惯后拒绝）；跨 owner 一律 rejected；每个接受/冲突写入推进一次 revision 并写 `sync_changes`（新实体类型），幂等重放返回 duplicate。
- `SyncMutation.entity_type` 枚举加入 `habit`/`habit_log`；契约测试增加枚举断言，保持 **28 项**通过。无 schema 变更、无迁移。
- 新增 `HttpHabitPushSmoke`：创建/重放/pull 可见、更新与陈旧冲突、日志创建/同日冲突/更新、删除与删除后拒绝、非法实体/缺字段/weekly 缺 weekdays/非法日期、跨 owner，并核对 revision=5、`sync_changes` 按实体计数（habit 3、habit_log 2）、幂等记录 5。
- 本地验证：`dotnet build --warnaserror` 0 警告 0 错误、`dotnet format` 通过、契约测试 28/28、OpenAPI 校验 OK。
- [Server integration 43155930310](https://github.com/yml-qq/Planna/actions/runs/43155930310) 与 [Desktop core 43155930298](https://github.com/yml-qq/Planna/actions/runs/43155930298) 均 **success**。
- 未连接生产库、未提交凭据；客户端打卡 UI、提醒与离线习惯 outbox 留待 `HAB-01F`。

## HAB-01F 桌面本地习惯存储检查（2026-11-12）

- core 本地 SQLite 新增 `habits` 与 `habit_logs`（同日唯一）；`create_habit` 与 `upsert_habit_log` 在单事务内写本地行与 outbox（habit/habit_log，新建 base_version=0、同日更新为更新前版本），同 mutation id 幂等；非法值/wеekly 缺 weekdays/习惯不存在整体回滚。
- `apply_pull` 新增 habit（版本单调、软删墓碑）与 habit_log（仅 upsert、版本单调）分支；同步引擎 pull 过滤器接受 `task/habit/habit_log`，推送复用既有 transport；新增 `uuid` 依赖用于本地日志 id。
- 新增 `habit_tests.rs` 8 项：原子写、幂等、weekly 校验、同日复用与 base_version 递增、非法回滚、拉取新旧/墓碑、未知实体与日志删除、同步引擎推送并清空 outbox/推进游标；core 测试合计 **28 项**。
- 本地：`cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、`cargo test` 全部通过。
- [Server integration 43157421165](https://github.com/yml-qq/Planna/actions/runs/43157421165) 与 [Desktop core 43157421097](https://github.com/yml-qq/Planna/actions/runs/43157421097) 均 **success**；服务端无变更。
- 未连接生产库、未提交凭据；Tauri 习惯命令、Vue 打卡 UI 与提醒留待 `HAB-01G`。

## HAB-01G 桌面打卡面板检查（2026-11-12）

- Tauri 新增 `list_habits/create_habit/upsert_habit_log/list_habit_logs` 命令（DTO snake_case，生成 mutation id，本地行与 outbox 同事务）；前端桥接新增 habit 类型与四个方法（Memory 与 Tauri 两种实现）。
- 新增 `habits` store：刷新习惯 + 仅当日日志、创建（标题 1..200、目标 1..1000、默认单位、daily）、按目标数量打卡且同日版本递增、`localDate()` 本地日历日期与错误映射；`App.vue` 登录后新增“习惯打卡”卡片（列表、今日状态/打卡按钮、新增表单），登出清空状态。
- 前端测试增至 **21 项**（新增 6 项习惯）；`pnpm build`（vue-tsc + vite）通过；shell `cargo fmt/clippy/check` 通过；服务端无变更。
- [Server integration 43158924568](https://github.com/yml-qq/Planna/actions/runs/43158924568) 与 [Desktop core 43158924686](https://github.com/yml-qq/Planna/actions/runs/43158924686)（Windows Rust core / Windows Tauri shell / Desktop web UI）均 **success**。
- 未连接生产库、未提交凭据；提醒/本地通知、习惯暂停与冲突 UI 留待 `HAB-01H`。

## HAB-01H 习惯暂停检查（2026-11-12）

- 服务端 `/sync/push` habit upsert payload 新增可选 `paused`：创建时 true 写入 `paused_at`，更新时 true 保留/写入、false 清除、缺省不变；版本/冲突/审计语义不变。
- 桌面 core 新增 `HabitPatchLocal` 与 `Store::update_habit`（本地标题/目标/单位/暂停时间 + outbox 同事务、`base_version=旧版本`、payload 仅含改动字段、空/非法补丁与删除习惯报错、幂等）；Tauri 新增 `update_habit` 命令。core 测试新增 3 项（合计 **31 项**）。
- 服务端冒烟扩展：`paused=true`（v3）、`paused=false`（v4）均 accepted，删除基于 v4；账目核对 revision=7、habit 变更 5、habit_log 2、幂等记录 7、墓碑版本 5。
- 本地验证：`dotnet build --warnaserror` 0 警告 0 错误、`dotnet format`/契约测试 28 项、`cargo fmt/clippy/test` 通过。
- [Server integration 43160586192](https://github.com/yml-qq/Planna/actions/runs/43160586192) 与 [Desktop core 43160586061](https://github.com/yml-qq/Planna/actions/runs/43160586061) 均 **success**。
- 未连接生产库、未提交凭据；暂停/连续天数 UI、冲突解决与提醒/本地通知留待 `HAB-01I`。

## HAB-01I 暂停与连续天数 UI 检查（2026-11-12）

- 桥接新增 `updateHabit`（Memory 版本 +1 / Tauri `update_habit`）；习惯 store 刷新时拉取最近 90 天日志并计算 `todayLogs/logs/streaks`，`addDays`/`currentStreak` 纯函数与服务端规则一致；`togglePause` 往返切换并替换列表项，打卡后本地合并日志并重算 streak。
- UI 习惯行显示“连续 N 天”、暂停状态与“暂停/恢复”按钮；错误文案增加 `HABIT_PAUSE_FAILED`。
- 前端测试增至 **25 项**（新增跨月/跨年日期、streak 门槛与连续、刷新计算、暂停往返），`pnpm build`（vue-tsc + vite）通过；无服务端/契约/schema 变更。
- [Server integration 43162105311](https://github.com/yml-qq/Planna/actions/runs/43162105311) 与 [Desktop core 43162105244](https://github.com/yml-qq/Planna/actions/runs/43162105244) 均 **success**。
- 未连接生产库、未提交凭据；提醒/本地通知、同日日志冲突 UI 与习惯编辑留待 `HAB-01J`。

## HAB-01J 习惯本地提醒检查（2026-11-12）

- 新增提醒纯函数（`isReminderTime/localTime/dueReminders/runReminders/loadReminders`）：仅启用且时间匹配、未暂停、当日无 done 日志的习惯；按 `habit:date` 去重每天一次；单条通知失败隔离并记账；localStorage 解析逐项校验。
- 通知适配：浏览器 `MemoryNotifier`；Tauri 动态加载 `@tauri-apps/plugin-notification`（首次发送检查/申请权限）。Tauri 增加 notification 插件依赖与 `notification:default` 能力，shell `cargo check/clippy/fmt` 通过。
- UI：每个习惯行提供提醒时间与启用复选框，配置持久化 `planna.habitReminders`；登录后每 30 秒以本地日期/时间执行提醒，卸载清理定时器。
- 前端测试增至 **30 项**（新增 5 项提醒）；`pnpm build` 通过。未在真机验证通知投递（headless 无桌面通知运行时），仅验证编译、能力配置与逻辑。
- [Server integration 43163714732](https://github.com/yml-qq/Planna/actions/runs/43163714732) 与 [Desktop core 43163714684](https://github.com/yml-qq/Planna/actions/runs/43163714684) 均 **success**。
- 未连接生产库、未提交凭据；冲突 UI、习惯编辑与免打扰设置留待 `HAB-01K`。

## HAB-01K 编辑/冲突可见性/免打扰检查（2026-11-12）

- 习惯编辑：桥接与 store 新增 `updateDetails`（标题/目标/单位校验，成功后替换列表项并重算 streak）；UI 内联编辑表单（保存/取消），错误码 `HABIT_UPDATE_FAILED`。
- 冲突可见性：Tauri 新增 `outbox_status`（pending/failed 基于 attempts）与桥接 `outboxStatus()`；同步/启动后刷新，`failed>0` 显示“同步冲突：N 条变更未上传”，不静默丢弃。
- 免打扰：`quietHours`（起止 `HH:MM`、支持跨午夜）持久化 `planna.quietHours`；提醒 tick 在窗口内不发送且不占用当日去重键；`withinQuietHours/loadQuietHours` 校验。
- 前端测试增至 **35 项**（新增编辑成功/非法/失败、跨午夜与同日窗口、抑制通知、持久化解析），`pnpm build` 通过；shell `cargo check/clippy/fmt` 通过；服务端无变更。
- [Server integration 43165263336](https://github.com/yml-qq/Planna/actions/runs/43165263336) 与 [Desktop core 43165263376](https://github.com/yml-qq/Planna/actions/runs/43165263376) 均 **success**。
- 未连接生产库、未提交凭据；冲突逐条处置、weekly 日程编辑与通知真机验证留待 `HAB-01L`。

## HAB-01L 同步冲突处置检查（2026-11-12）

- core `OutboxEntry` 增加 `last_error`（`pending_mutations` 返回），core 测试补充失败原因断言（31 项）；无 schema 变更。
- Tauri 新增 `outbox_issues`（仅 attempts>0 的 mutation）与 `discard_mutation`（存在才删除并返回 true）；桥接新增 `outboxIssues()/discardMutation()`。
- UI 在同步/启动后显示冲突数量与逐条列表（实体/操作/尝试次数/最后错误），“放弃本地变更”删除 outbox 记录并刷新列表、计数与习惯数据。
- 明确记录语义限制：放弃仅删本地 outbox，不向服务端回滚；后续拉取按版本单调规则收敛，完整覆盖/重试语义留 `HAB-01M`。
- 前端 35 项测试与 `pnpm build`、shell `cargo check/clippy/fmt`、core 31 项测试均通过；服务端无变更。
- [Server integration 43166817174](https://github.com/yml-qq/Planna/actions/runs/43166817174) 与 [Desktop core 43166817100](https://github.com/yml-qq/Planna/actions/runs/43166817100) 均 **success**。
- 未连接生产库、未提交凭据；weekly 编辑、覆盖/重试与通知真机验证留待 `HAB-01M`。

## HAB-01M weekly 编辑与冲突收敛检查（2026-11-12）

- weekly 编辑：`HabitPatchLocal` 增加 `schedule_type/weekdays`，core 校验 daily/weekly/weekdays、weekly 1..7 去重排序、非 weekly 清空 weekdays；Tauri `update_habit` 接受 `scheduleType/weekdays`；UI 编辑表单增加日程选择与星期复选框、列表显示日程摘要。
- 冲突收敛：`PushResult` 增加可选 `server_entity`，`SyncOutcome` 增加 `resolved_conflicts`；带服务器实体的 conflict 通过 `force_apply_snapshot`（task/habit/habit_log，忽略本地版本）写入服务器状态并移除 mutation，未带实体仍进入待处理列表。
- 测试：core 新增 2 项（server-wins、weekly→daily 清空），合计 **33 项**；前端新增 1 项 weekly 编辑，合计 **36 项**；`cargo clippy/fmt`（core/shell）与 `pnpm build` 通过。
- [Server integration 43168587409](https://github.com/yml-qq/Planna/actions/runs/43168587409) 与 [Desktop core 43168587315](https://github.com/yml-qq/Planna/actions/runs/43168587315) 均 **success**；服务端无变更。
- 未连接生产库、未提交凭据；显式三态处置、冲突台账与通知真机验证留待 `HAB-01N`；自动 server-wins 会丢弃本地未推送编辑（已在 `80-HAB-01M.md` 说明）。

## HAB-01N 冲突三态处置检查（2026-11-12）

- core 新增 `sync_conflicts` 台账（mutation/实体/服务器实体/版本/时间）与三种处置：`keep_server`（写服务器快照、删 mutation 与台账）、`retry`（清零 attempts/last_error、保留版本）、`overwrite`（base_version 重设为服务器版本、清零失败状态）；同步引擎在带 `server_entity` 的 conflict 时改为记录台账并保留 outbox（撤销 HAB-01M 的自动覆盖）。
- Tauri 新增 `list_conflicts`/`resolve_conflict(strategy)`；桥接与 UI 增加冲突列表与“以服务端为准/重试/覆盖”三个按钮，处置后刷新冲突、outbox、任务与习惯。
- 测试：core 新增冲突解析测试，合计 **34 项**；同步测试验证“记录台账 → keep_server 收敛”；前端 36 项与 `pnpm build` 通过；core/shell `cargo clippy/fmt` 通过；服务端无变更。
- [Server integration 43170244438](https://github.com/yml-qq/Planna/actions/runs/43170244438) 与 [Desktop core 43170244516](https://github.com/yml-qq/Planna/actions/runs/43170244516) 均 **success**。
- 未连接生产库、未提交凭据；通知真机验证、跨时区展示与台账审计留待 `HAB-01O`。台账本地保存、不上传，清库会丢失台账但保留 outbox。

## HAB-01O 跨时区检查（2026-11-12）

- 新增 `dateInTimeZone/timeInTimeZone`（`Intl.DateTimeFormat`，非法时区回退本地）；习惯刷新/打卡/编辑后的 streak 均按习惯 `time_zone` 计算“今日”和 90 天窗口；UI 显示习惯时区。
- 提醒改为按习惯时区比较提醒时间与今日日期，去重键为 `habit:习惯时区日期`；免打扰仍按设备本地时间窗口。
- core 新增 `prune_conflicts`（删除无 outbox mutation 的台账行），`sync_once` 每次同步前执行；新增 prune 测试（core 合计 **35 项**）。
- 前端提醒测试改为固定 UTC 时区 + 动态本地时间，**36 项**与 `pnpm build` 通过；core/shell `cargo clippy/fmt` 通过；服务端无变更。
- [Server integration 43171888623](https://github.com/yml-qq/Planna/actions/runs/43171888623) 与 [Desktop core 43171888640](https://github.com/yml-qq/Planna/actions/runs/43171888640) 均 **success**。
- 未连接生产库、未提交凭据；通知真机验证、台账导出与服务端 streak 一致性抽查留待 `HAB-01P`。

## HAB-01P 冲突台账导出检查（2026-11-12）

- Tauri 新增 `export_conflicts`，输出 `conflict-ledger-v1` JSON（schema_version/exported_at/conflicts[] 含服务器实体与版本）；桥接 `exportConflicts()`；UI 在存在冲突时提供“导出冲突台账”，优先写剪贴板并展示 JSON。
- 前端新增导出测试（Memory 桥返回合法版本化空台账），测试增至 **37 项**；`pnpm build` 与 shell `cargo check/clippy/fmt` 通过；服务端无变更。
- streak 一致性：服务端规则由 HAB-01C 冒烟覆盖，客户端按同一规则在习惯时区计算（HAB-01I/O）；本切片记录抽查依据，未新增跨栈自动比对。
- **通知真机投递未验证**：当前环境无桌面通知运行时，仅验证插件编译、能力配置、调度/免打扰/去重逻辑。
- [Server integration 43173459806](https://github.com/yml-qq/Planna/actions/runs/43173459806) 与 [Desktop core 43173459876](https://github.com/yml-qq/Planna/actions/runs/43173459876) 均 **success**。
- 未连接生产库、未提交凭据；人工 QA（通知投递、签名安装包、streak 抽查、台账字段评审）留待 `HAB-01Q`。

## FOCUS-01 专注会话 API 检查（2026-11-12）

- 迁移 `202611140001_FocusSessions`：`focus_sessions` 表（state/version/planned/actual/break、ended_at 约束、任务外键、`ux_focus_sessions_open` 部分唯一索引保证单 owner 一个未结束会话）。
- 契约：`GET/POST /focus-sessions`、`GET/PATCH /focus-sessions/{id}`，含 `Idempotency-Key`、`If-Match: "vN"`、状态机（active↔paused、→completed/abandoned）、终态拒绝、owner 隔离、任务归属校验；OpenAPI 新增路径与四个 schema，契约测试 **29 项**通过。
- 实现：`FocusSessionService` 使用 `client_mutations` 幂等 + owner 级 advisory lock，所有写事务化；`actual_seconds` 由客户端提供，服务端不从墙钟推断。
- 冒烟 `HttpFocusSmoke`：创建/重放/同 key 复用/开放会话冲突/列表/GET/跨 owner 404/暂停→恢复→完成/陈旧版本/终态拒绝/完成后可新建/非法 planned/任务/状态/limit/缺失 If-Match。
- 本地：`dotnet build --warnaserror` 0 警告 0 错误、`dotnet format`、契约 29 项通过。
- [Server integration 43175516165](https://github.com/yml-qq/Planna/actions/runs/43175516165) 与 [Desktop core 43175516258](https://github.com/yml-qq/Planna/actions/runs/43175516258) 均 **success**。
- 未连接生产库、未提交凭据；专注尚未进入 `/sync` 变更流，客户端计时 UI/断电恢复留待 `FOCUS-02`。
