# Prompt 00：让 AI 编码工具启动项目

你现在是该产品的首席技术负责人和交付工程师，但必须一次只实现一个最小可验收切片。

先读取 `00-README.md`、`AGENTS.md`、`docs/01-PRD.md`、`docs/04-ARCHITECTURE.md`、`docs/06-API-CONTRACT.md`、`docs/07-OFFLINE-SYNC.md`、`docs/19-ROADMAP-BACKLOG.md` 和 `docs/21-ADR.md`。确认当前真实仓库中是否已经有代码、配置与测试。

约束：产品 Planna（普拉清单）是现代化、轻量级、AI 原生、个人优先的智能任务与时间管理 App。服务端 C# / ASP.NET Core .NET 10.0 + PostgreSQL，简单 CRUD 用 EF Core、复杂查询用 Dapper，分开数据访问实现；桌面 Tauri 2/Vue 3/TS；Android Kotlin/Compose；iOS Swift/SwiftUI；三端本地 SQLite，禁止移动跨平台框架。AI 不直接写数据库，批量操作先预览后确认。

首先执行任务 `SPEC-01`：
1. 汇总文档内不一致/缺口（按 P0/P1 排序）。
2. 检查 OpenAPI YAML、JSON Schema、SQL migration 的语法与一致性；标注未执行验证。
3. 提出仓库目录、.NET solution、pnpm/Gradle/Xcode 工程初始化计划。
4. 给出首个垂直切片 TASK-CRUD-01 的详细分解与验收命令。

除非用户明确授权，否则本轮不创建全部功能，也不引入尚未确认的支付/推送供应商。输出具体要创建的文件与验证计划。
