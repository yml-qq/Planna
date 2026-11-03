# 11｜.NET 10 服务端工程详细设计

## 1. 技术基线

C# / ASP.NET Core（.NET 10.0）+ PostgreSQL；EF Core 用于简单 CRUD、变更跟踪和迁移，Dapper 用于复杂只读查询与报表。使用内置依赖注入、配置/Options、结构化日志和 OpenTelemetry。独立 .NET Worker Service 处理异步任务。具体 NuGet 小版本在创建工程时锁定并验证。

## 2. 可维护的模块化单体

```text
server/
  Planna.slnx
  src/
    Planna.Api/                 # HTTP endpoints、认证、错误映射
    Planna.Application/         # 用例、事务协调、接口
    Planna.Domain/              # 纯业务规则，不依赖 Web/ORM
    Planna.Infrastructure/      # EF Core DbContext、迁移、Dapper 查询实现
    Planna.Worker/              # 后台任务宿主
  tests/
    Planna.UnitTests/
    Planna.IntegrationTests/
    Planna.ContractTests/
```

按 `identity/tasks/lists/calendar/reminders/sync/ai` 等业务模块组织目录与注册，不建立跨模块万能 Repository。依赖方向为 `Api/Worker → Application → Domain`；Infrastructure 实现 Application 的窄接口，Domain 不引用 ASP.NET Core、EF Core、Dapper 或模型 SDK。模块之间经应用接口/事件协作，避免 Handler 直接操作 `DbContext` 或拼 SQL。

## 3. EF Core 与 Dapper 的边界

- **EF Core 命令侧**：任务、清单等简单 CRUD；通过具体 `DbContext`/专用写存储实现变更追踪、乐观并发、关系校验和迁移。写入事务同时保存业务对象、每账户 revision、changelog、幂等结果和审计。关键并发锁可在 EF Core 事务内执行受审查的参数化 SQL。
- **Dapper 查询侧**：跨表筛选、统计、日历聚合等复杂只读查询，返回专用只读 DTO。SQL 放在对应模块的 Query 实现，使用参数化查询、显式 `owner_id` 过滤、分页与字段白名单；不得暴露给 AI 或客户端。
- Dapper 查询默认不承担业务写入；若出现经证明需要 Dapper 写入的例外，单独 ADR 说明事务和并发语义。不得把 EF Core 与 Dapper 方法塞进同一个 Repository，也不得创建无边界的通用 Repository。
- 查询一致性：命令完成后从本地客户端 SQLite 更新 UI；服务端读路径需要读己之写时使用主库和明确的事务/提交边界。EF Core 和 Dapper 映射同一 PostgreSQL schema，契约测试验证关键字段与类型一致。

## 4. 创建任务流程

```text
POST /tasks
  → Authentication + UserContext
  → DTO validation
  → CreateTaskUseCase.ExecuteAsync
    → check list.owner == user
    → idempotency guard
    → BEGIN (EF Core transaction)
      lock owner sync state and advance revision
      insert task, sync change, mutation result, audit
    → COMMIT
  → HTTP 201 + contract DTO
```

账户级 revision 锁必须先在同一事务中取得，并保持至提交；不能用独立 `BIGSERIAL` 推测提交顺序。`SaveChangesAsync` 可执行多次，但事务只提交一次，失败整体回滚。

## 5. 错误、权限与并发

统一返回现有 API 契约中的 `code/message/details/request_id` 错误结构；内部 SQL/堆栈不返回客户端。所有对象和嵌套引用按 token 得到的 owner 校验。更新使用 `owner_id + id + expected_version` 条件，零行受影响映射为冲突或不可见资源。Dapper 的读查询也必须强制 owner 范围，不能由请求体提供 owner 作为授权依据。

## 6. Worker 与运行维护

独立 Worker Service 与 API 复用 Application/Domain/Infrastructure。PostgreSQL job 使用租约、重试、死信和稳定 dedupe key；多实例通过 `FOR UPDATE SKIP LOCKED` 抢占。配置超时、连接池、限流与取消传播；日志仅记录 request/trace ID 和脱敏指标，不记录明文任务。

## 7. 验证与交付

在真实源码仓库运行 `dotnet format --verify-no-changes`、`dotnet build`、`dotnet test`，并用临时 PostgreSQL 验证 EF Core 迁移、EF Core CRUD/并发、Dapper 复杂查询、权限隔离和同步事务故障。OpenAPI 契约、依赖与秘密扫描进入 CI。当前文档包只有 PostgreSQL DDL 草案；创建 .NET 工程时生成 EF Core 迁移，并校对草案，不直接把草案宣称为已运行的 EF Core migration。
