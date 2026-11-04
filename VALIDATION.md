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
