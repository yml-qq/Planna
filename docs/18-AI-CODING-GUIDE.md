# 18｜使用 AI 编程工具开发本 App 的完整作业手册

## 1. 核心方法：规格驱动 + 垂直切片 + 自动验收

**不要向 AI 一次性下达“开发完整滴答清单”**。把系统切成可以在独立分支实现、编译、测试、合并的纵向功能片：需求/数据/HTTP/本地/页面/测试全链路，每片必须有验收脚本。人类负责产品取舍、安全边界和验收；AI 负责编码候选、生成测试、解释失败、修复问题。

## 2. 仓库初始化建议

```text
repo/
  AGENTS.md                # 根 AI 指令
  docs/                    # 本文档集
  spec/                    # openapi/schemas
  db/migrations/
  server/
  desktop/
  android/
  ios/
  tests/fixtures/
  .github/workflows/
```

每个 AI 会话都让工具先阅读：`00-README.md`、`AGENTS.md`、当前 feature 的 PRD/业务规则、`04-ARCHITECTURE.md`、`06-API-CONTRACT.md`、`07-OFFLINE-SYNC.md`、对应平台文档。严禁仅根据上一轮聊天记忆生成实现。

## 3. AI 开发单元任务格式

每个工单应包含：ID、用户价值、所涉文件、明确范围/非范围、API/数据契约、并发与权限、测试数据、验收命令、预期输出、回滚措施。示例：

```text
ID: TASK-CRUD-01
目标: ASP.NET Core API 支持创建普通任务及幂等重试。
输入文档: PRD F01, DATA, API-CONTRACT, OFFLINE-SYNC。
允许修改: server/src 中对应业务模块、EF Core migrations、tests。
不允许: 修改同步协议版本、接入 AI Provider、添加新框架。
验收:
  - POST tasks 201
  - 同 idempotency key 重试返回等价结果
  - 不同用户访问返回 404
  - EF Core migration 可从空库启动
  - dotnet format/build/test + integration 通过
交付: 改动文件清单、运行命令、测试输出摘要、风险与已知限制。
```

## 4. AI 驱动实施顺序

**阶段 0 文档/契约**：核对版本、确定 OS 支持、生成 OpenAPI、Schema、migration 计划、完整 ADR；首发服务商和账号/支付方案先用 Mock。

**阶段 1 基础设施**：Monorepo、.NET solution、CI、Postgres/Redis Compose、health、auth、中间件和日志。

**阶段 2 领域 API**：Task/List/Tags/Steps CRUD、完成/撤销、repetition 和 reminder 数据模型、版本/权限/幂等。

**阶段 3 离线同步**：per-account committed revision、mutation/outbox、bootstrap/pull、冲突策略和故障注入。

**阶段 4 客户端纵向切片**：Tauri TaskList + SQLite；Android Compose TaskList + Room；iOS SwiftUI TaskList + SQLite；三端使用相同 `fixtures`。

**阶段 5 AI 原生功能**：规则解析→LLM structured parse→AI Plan draft→可行性校验→用户确认→事务 apply→审计与撤销。

**阶段 6 日历提醒**：时区/DST/重复实体、Android/iOS/Tauri 本地通知、worker 推送、用户通知权限处理。

**阶段 7 功能增强**：习惯/专注/目标/笔记/统计/个性化与会员。

**阶段 8 安全、上架、运营**：渗透测试/压测、灰度、监控、账号注销、隐私与商店上架。

## 5. 每轮 AI 修改代码的固定步骤

1. **读取真实仓库状态**：目录、git status、依赖清单、测试状态；不凭想象编程。
2. **复述任务边界和实现计划**：约定只修改哪些模块，标出潜在 schema breaking change。
3. **先写/更新测试**：包含 happy/edge/error/offline/conflict，至少新增可运行测试。
4. **最小增量实现**：遵守项目原有依赖与代码风格，不顺便重构其他模块。
5. **执行验证**：真实运行 lint/build/test/migrations，不伪称通过。
6. **提交变更摘要**：文件、数据库/API 影响、运行结果、未完成点；必要时更新 ADR/文档。
7. **人工审查**：安全、隐私、幂等、迁移、跨端影响，之后 merge。

## 6. 给 AI 的通用系统提示词

```text
你是资深 C#/ASP.NET Core .NET 10、Tauri 2、Android Kotlin/Compose 和 iOS Swift/SwiftUI 工程团队。
你必须遵循仓库 AGENTS.md、docs/01-PRD.md、docs/04-ARCHITECTURE.md、docs/06-API-CONTRACT.md、docs/07-OFFLINE-SYNC.md。
产品 Planna（普拉清单）是现代化、轻量级、AI 原生、个人优先的智能任务与时间管理 App，不是企业 SaaS。
服务端简单 CRUD 使用 EF Core，复杂查询使用 Dapper 专用查询实现；不得混在一个 Repository。
禁止把移动端改成任何跨平台 UI；禁止模型直接写 SQL；禁止新增无需求的基础设施。
在动手前确认现有文件和测试；若需求有冲突先列出冲突并采用已记录 ADR，必要时提出最小修改。
每次仅做一个垂直切片；为权限、幂等、时区、离线和回滚写测试。
不能虚构运行结果、接口或依赖版本；没有实际运行的命令标记为未验证。
输出：目标、改动、验证命令与结果、风险、待办。
```

## 7. 角色 Prompt 协作流程

- **产品/架构代理**：从 PRD 生成可验收 Story + API/DB 影响清单。
- **.NET 代理**：只实施业务接口、迁移和集成测试。
- **同步代理**：维护协议、cursor 正确性、冲突和断网测试，不让平台端自行修改语义。
- **客户端代理**：按各自 native 规范实现相同 UseCase，明确系统权限和生命周期差异。
- **AI Agent 代理**：工具 schema、输出解析、模型路由、Plan apply 安全。
- **QA/Security 代理**：独立审查、构造恶意输入、执行测试、报告可复现失败。

不同代理可以并行提交**不冲突的模块/分支**，不能并行更改同一个 OpenAPI Schema/数据 migration 而缺少一个主负责人。

## 8. 反模式清单

- AI 没读当前代码就声称“已实现全部功能”。
- 先构建漂亮 UI，后补数据同步，最终无法离线。
- 大模型直接生成 SQL/自动删除用户任务。
- 客户端自己定义字段名/时区和任务完成规则。
- 把 `updated_at` 当作 sync cursor。
- 只测在线 happy path，不测系统杀进程/并发/双端。
- 把最核心的提醒/导出放入付费才能使用且会员失效后删除用户数据。

## 9. 具体 Prompt 目录

`../prompts/DOTNET-BACKEND.md`、`SYNC-ENGINE.md`、`TAURI-DESKTOP.md`、`ANDROID-NATIVE.md`、`IOS-NATIVE.md`、`AI-AGENT.md`、`TEST-REVIEW.md`；AI 工具应在实际仓库按工单复制相应模板，填入目标功能和允许修改范围。

## 10. 人工验收输出样板

```markdown
### 功能交付报告
- 工单：
- 实现范围：
- 修改文件：
- 数据库迁移：
- OpenAPI 变化：
- 单元测试：运行命令 / 实际结果
- 集成与设备测试：运行命令 / 实际结果
- 离线/同步/提醒影响：
- AI 权限/隐私影响：
- 尚未验证项：
- 回滚方案：
```
