# Prompt：.NET 10 后端功能开发

任务 ID：{填写，如 TASK-CRUD-01}
范围：{明确本轮只做的 API/UseCase}
允许修改：server/ 对应模块、migrations、API 契约和 tests；不改 Android/iOS/Tauri。

先读 AGENTS.md、docs/02-FUNCTIONAL-SPECS.md、docs/05-DATA-DOMAIN.md、docs/06-API-CONTRACT.md、docs/07-OFFLINE-SYNC.md、docs/11-DOTNET-BACKEND.md、spec/openapi.yaml 和 db/001_init.sql，核查当前仓库状态。

实施要求：
- 用 C# / ASP.NET Core .NET 10.0 + PostgreSQL，Domain 不依赖 Web/DB 层。
- 简单 CRUD 和迁移使用 EF Core；复杂只读查询使用 Dapper 专用 Query Service，不混在一个 Repository。
- Handler 只做身份、解析和响应；业务流程在 Application Service；DB 在 Repo。
- 每条写操作验证 owner_id/nested foreign key ownership、expected_version 和幂等键。
- 业务写入与账户同步 revision、changelog 和 mutation response 保存在同一数据库事务。
- 错误映射统一 AppError；不得泄漏 SQL/秘钥/其他用户资源是否存在。
- 为校验、权限、重试幂等、并发冲突、时间/时区加入测试。

先给出简短实现计划，再在仓库内修改真实文件；执行 dotnet format/build/test、EF Core migration/integration 和 Dapper 查询测试（不能运行时明确原因），最后列文件改动、迁移、测试结果和潜在问题。不要宣称未验证的部分已完成。
