# Planna 普拉清单：全套产品与研发实施文档

> 产品名称：Planna；中文名：普拉清单（商标/域名仍需核验）  
> 基线版本：v0.1 / 2026-11-03  
> 定位：面向中国大陆个人用户的现代化、轻量级、AI 原生、个人优先的智能任务与时间管理 App；隐私可控，多端离线优先。  
> **技术决策已锁定：后端 C# / ASP.NET Core（.NET 10.0）；简单 CRUD 使用 EF Core，复杂查询使用 Dapper，分别实现数据访问；桌面 Tauri 2（前端 Vue 3 + TypeScript）；Android Kotlin + Jetpack Compose；iOS Swift + SwiftUI。移动端不使用 Flutter、React Native 或 Tauri Mobile。**

## 01. 如何使用本套文档

这些文档是**可供 AI 编码助手逐步实施的详细设计基线**，而不是已经开发完成的软件。先阅读产品目标和架构决策，再沿“功能切片”实施。未明确的产品选择均写成“建议/待验证”，不应自动当成真实用户数据或已获得许可。

| 顺序 | 文件 | 内容与使用场景 |
|---|---|---|
| 1 | `docs/01-PRD.md` | 定位、目标用户、完整产品需求、范围与非目标 |
| 2 | `docs/02-FUNCTIONAL-SPECS.md` | 详细业务规则、边界、验收标准 |
| 3 | `docs/03-UX-FLOWS.md` | 信息架构、页面清单、交互和状态设计 |
| 4 | `docs/04-ARCHITECTURE.md` | .NET 服务端、Tauri、双原生移动端总体架构 |
| 5 | `docs/05-DATA-DOMAIN.md` | 领域模型、表、字段、状态机与约束 |
| 6 | `docs/06-API-CONTRACT.md` | HTTP API、请求/响应、鉴权与错误规范 |
| 7 | `docs/07-OFFLINE-SYNC.md` | 离线优先、多端同步、冲突和幂等 |
| 8 | `docs/08-AI-CAPABILITIES.md` | AI 业务能力、用户确认、授权、记忆与体验 |
| 9 | `docs/09-AI-ORCHESTRATION.md` | 模型网关、Agent 工具、排程、评测和成本 |
| 10 | `docs/10-RECURRENCE-NOTIFICATION.md` | 重复规则、时区、提醒调度和通知可靠性 |
| 11 | `docs/11-DOTNET-BACKEND.md` | 后端工程结构、分层、依赖和并发实践 |
| 12 | `docs/12-DESKTOP-TAURI.md` | Tauri 2 Windows/macOS/Linux 客户端 |
| 13 | `docs/13-ANDROID.md` | Kotlin + Compose 原生 Android |
| 14 | `docs/14-IOS.md` | Swift + SwiftUI 原生 iOS |
| 15 | `docs/15-SECURITY-PRIVACY.md` | 权限、加密、个人信息和 AI 数据边界 |
| 16 | `docs/16-QUALITY-TESTING.md` | 自动化测试、质量门禁、测试矩阵 |
| 17 | `docs/17-DEVOPS.md` | 部署、CI/CD、备份、监控、恢复 |
| 18 | `docs/18-AI-CODING-GUIDE.md` | 让 Codex/Cursor/其他 AI 编码工具逐步实施 |
| 19 | `docs/19-ROADMAP-BACKLOG.md` | 版本、史诗、任务拆解、依赖和交付顺序 |
| 20 | `docs/20-BUSINESS-METRICS.md` | 会员、成本、指标及可行性验证 |
| 21 | `docs/21-ADR.md` | 已选方案、原因、备选、未来复审条件 |
| 22 | `docs/22-DESIGN-SYSTEM.md` | 设计 Token、组件标准和无障碍 |
| 23 | `docs/23-LEGAL-RISKS.md` | 中国大陆上线、资质、隐私与风险检查 |
| 24 | `docs/24-USER-STORIES.md` | 关键用户故事及 Given/When/Then 验收 |
| 25 | `docs/25-OPERATING-RUNBOOK.md` | 生产故障、提醒漏发、同步冲突应急预案 |
| 26 | `docs/26-LOCAL-SETUP.md` | 本地环境安装、平台工具链与首次启动 |
| 27 | `docs/27-TRACEABILITY.md` | 需求、API、数据、测试追踪矩阵 |
| 28 | `docs/28-SPEC-01.md` | V1.0 任务拆解、导出、账号删除与 AI 整份确认契约 |
| 29 | `docs/29-DATA-01.md` | EF Core 迁移起点、隔离库验证与当前限制 |
| - | `spec/openapi.yaml` | 可用于 SDK 生成的核心 API 契约初稿 |
| - | `spec/task.schema.json` | 任务记录 JSON Schema |
| - | `spec/ai-plan.schema.json` | AI 计划预览 JSON Schema |
| - | `spec/ai-breakdown.schema.json` | AI 任务拆解草案 JSON Schema |
| - | `spec/account-export.schema.json` | 版本化数据导出 JSON Schema |
| - | `spec/account-deletion.schema.json` | 账号删除接受响应 JSON Schema |
| - | `db/001_init.sql` | PostgreSQL 核心表可执行 DDL 起点 |
| - | `db/002_spec01.sql` | SPEC-01 增量 DDL 草案，待转换为 EF Core migration |
| - | `AGENTS.md` | 项目级 AI 编码规则（直接复制到仓库根目录） |
| - | `prompts/*.md` | 后端、同步、AI、桌面、Android、iOS 开发 Prompt |
| - | `examples/` | 请求与同步示例 |
| - | `tests/contract/` | SPEC-01 OpenAPI/Schema/合成样例静态契约检查 |
| - | `server/` | .NET 10 迁移工程、生成 SQL 与数据库冒烟检查；业务服务尚未实现 |

## 02. 产品边界：必须遵守

- 个人优先：**不做企业聊天、复杂组织权限或重型团队项目管理**。共享清单在后期。
- AI 原生：智能输入、任务拆解、智能日程和动态建议是核心业务；**模型不得直接写数据库**。
- 用户主权：AI 默认生成计划预览，确认后才批量执行；所有批量更改可审计且尽量可撤销。
- 离线优先：客户端 SQLite 本地数据为 UI 读取来源；离线可 CRUD，恢复连接后自动同步。
- 通知可靠：基础本地提醒不依赖 AI；系统限制导致不可保证准点送达时明确说明。
- 后端 .NET 10 模块化单体，按业务模块划分边界；初期不微服务化。
- 简单 CRUD 使用 EF Core，复杂查询使用 Dapper；两者分属明确的写模型/查询实现，不混在一个 Repository 里。
- 三端按**共同 API 契约 + 共同业务用例测试**保持一致，不能因为三端独立代码而产生语义漂移。
- 用户隐私：只向用户授权的模型发送最小必要数据，支持关闭 AI、删除个人数据及导出。

## 03. 推荐技术基线（锁定大方向，具体小版本上线前核验）

| 区域 | 建议技术 |
|---|---|
| Server | C# / ASP.NET Core (.NET 10.0) + Dependency Injection + OpenTelemetry |
| Database | PostgreSQL 16+；EF Core 负责简单 CRUD 与迁移，Dapper 负责复杂只读查询；Redis / S3 兼容存储按需使用 |
| Async Jobs | 独立 .NET Worker Service + PostgreSQL 任务表 + `FOR UPDATE SKIP LOCKED`；后期独立队列 |
| Desktop | Tauri 2 + Vue 3 + TypeScript + Vite + Pinia + SQLite + Rust commands |
| Android | Kotlin + Jetpack Compose + Room + Hilt + Coroutines/Flow + WorkManager |
| iOS | Swift + SwiftUI + SQLite (GRDB 作为 SQLite 访问层候选) + URLSession + Keychain + UserNotifications |
| Contract | OpenAPI 3.1、JSON Schema、UUIDv7/UUID、RFC3339 UTC、IANA 时区 |
| AI | .NET AI Orchestrator、模型 Provider 抽象、JSON Schema 约束、授权工具、规则排程 |
| DevOps | Docker Compose（早期）、Nginx/Caddy、GitHub Actions/GitLab CI、OpenTelemetry |

## 04. 定义“完成”

某功能交付需要同时满足：产品验收、API/Schema 契约、异常处理、离线同步、权限和安全、三端适配（若处于该平台版本范围）、自动化测试、日志与监控、文档更新。禁止“演示能点就算完成”。

## 05. 建议实施顺序

1. 冻结 `spec/` 和数据/同步语义；完成 ADR 审核。
2. 建立 ASP.NET Core API + EF Core migration + 测试数据库 + CI。
3. 实现 Task/List/Tag/Reminder 的一条完整端到端切片（先 Desktop + Android，iOS 以同契约跟进）。
4. 引入本地 SQLite 和 outbox，完成断网与双设备冲突测试。
5. 引入智能输入与“AI 计划预览 → 用户确认 → 业务执行”的安全闭环。
6. 丰富习惯、目标、专注、智能日程；开展性能、合规和商店发布测试。

## 06. 来源与版本注意事项

历史产品讨论：[个人轻量待办与 AI 规划构想](https://chatgpt.com/share/6ae9a4ae-a5f4-83ee-a1cb-ac764efc54a3)。其中“功能丰富但入口轻量”“AI 贯穿记录、规划、执行、复盘”的方向已纳入本基线；历史 Rust 服务端建议已由当前 .NET 10 决策替代。实际实施以本仓库 PRD、ADR 和机器契约为准。

官方参考：
- Tauri 2：https://v2.tauri.app/ 和 https://tauri.app/concept/architecture/
- .NET：https://learn.microsoft.com/dotnet/
- ASP.NET Core：https://learn.microsoft.com/aspnet/core/
- EF Core：https://learn.microsoft.com/ef/core/
- Dapper：https://github.com/DapperLib/Dapper
- Android 架构：https://developer.android.com/topic/architecture/recommendations
- Apple SwiftUI：https://developer.apple.com/documentation/swiftui
- RFC 5545：https://www.rfc-editor.org/rfc/rfc5545
- 中国大陆 APP 备案：https://www.miit.gov.cn/zwgk/zcwj/wjfb/tz/art/2023/art_920db564162e4312916a01bed6540ad8.html
- Android 精准提醒：https://developer.android.com/develop/background-work/services/alarms
- Apple 本地提醒：https://developer.apple.com/documentation/usernotifications/scheduling-a-notification-locally-from-your-app
- OpenAI 工具调用理念：https://developers.openai.com/api/docs/guides/function-calling

以上为架构参考，**具体 .NET SDK、NuGet 包、Tauri Rust 工具链、Gradle、Xcode、OS 最低支持版本等必须在真正创建工程时再次检查**。本套文档不承诺已经通过编译、真实机验证或商店审核。
