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
