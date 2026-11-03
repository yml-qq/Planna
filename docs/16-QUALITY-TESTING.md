# 16｜测试计划、质量门禁和 AI 评测体系

## 1. 测试金字塔

- 领域单元测试：任务状态机、时间解析、RRULE、优先级、排程硬约束、所有权判断。
- 仓储集成测试：真实 PostgreSQL/SQLite、migration、事务和并发冲突。
- API 契约测试：OpenAPI 请求/响应、错误码、限流、认证与版本兼容。
- 客户端 UI 测试：Compose UI、XCUITest、Tauri Playwright/WebDriver 可行性评估与操作回放。
- E2E：Android+iOS+桌面跨端同步，离线/重连/权限/提醒/AI apply。
- 安全和性能：OWASP API 安全检查、依赖审计、压测、混沌/注入故障。

## 2. 最关键的跨端一致性测试

| 测试编号 | 流程 | 验收 |
|---|---|---|
| SYNC-01 | A 断网创建任务；联网；B 拉取 | 一条任务，字段一致 |
| SYNC-02 | push 成功但响应丢失，客户端重复推送 | 服务端仅写一次 |
| SYNC-03 | A/B 离线同改标题 | 冲突可见，不丢修改 |
| SYNC-04 | A 删除，B 离线修改 | 不静默复活，保留副本 |
| SYNC-05 | 登出账号 A 再登录 B | A 私有数据不可见 |
| SYNC-06 | 先 pull 后客户端崩溃 | cursor 与数据原子恢复 |
| SYNC-07 | 旧 cursor 过期 | 无损 bootstrap 与 outbox 对账 |
| TIME-01 | 每月最后一天 | 2月/30天/31天正确 |
| TIME-02 | 夏令时切换 | 无重复/丢失实例 |
| TIME-03 | 全天任务跨时区查看 | 日期语义不变 |
| REM-01 | 改期+取消旧通知 | 旧提醒不重复 |
| AI-01 | 未授权笔记查询 | 无数据泄露、工具拒绝 |
| AI-02 | 计划生成后另一端修改任务 | apply 409 stale，无部分写入 |
| AI-03 | 含 prompt injection 的任务描述 | 系统权限不被提升 |
| AI-04 | AI Provider 500 | 手动任务创建仍能使用 |

## 3. 后端 CI 门禁

`dotnet format --verify-no-changes`、`dotnet build`、`dotnet test`、EF Core migration up、EF Core CRUD/并发与 Dapper 复杂查询集成测试、OpenAPI lint、依赖安全扫描、secret scan。Pull Request 必须更新或验证契约；禁止跳过失败测试用“人工已看过”合并到 main。

## 4. 移动与桌面门禁

Android：`ktlint`/静态分析、`./gradlew test`、Room migration instrumentation、Compose UI tests；iOS：SwiftLint（如采用）、Swift Testing/XCTest、XCUITest、Xcode build/test；Tauri：TypeScript typecheck、ESLint、Vitest、Tauri Rust tests、平台构建 smoke test。具体版本/命令按创建项目后的构建工具定稿。

## 5. AI 输出评测

建立 `eval_set` 含：中文日期/方言表达、模糊日期、重复规则、时区/DST、嵌套任务、过载排程、恶意输入、极长/短输入、隐私权限边界。每条数据包括输入、now、timezone、授权 scopes、预期结构化字段、不可发生的副作用。

指标：field accuracy（标题/日期/提醒/优先级）、schema pass rate、unresolved 召回率、plan hard constraint violation（要求零容忍）、unauthorized tool invocation（要求零容忍）、用户修正率、AI 实际调用成本。每次换模型/提示词运行 golden set + regression + 小流量灰度。

## 6. 可靠性压力测试

模拟用户任务 1k/10k、账号 100/10k、worker 积压、数据库连接池枯竭、长时间离线设备、每秒高峰 sync pushes；测试 DB 事务吞吐、pull 游标一致性、队列等待、服务恢复。容量数字以测量结果为准。

## 7. 测试数据规则

禁止把真实用户隐私或商用生产数据直接拷进 AI 编码工具；使用匿名合成任务、随机 ID、测试邮件、假 provider token；敏感任务仅经用户明确授权进入受控调试流程。

## 8. 发布退出标准

- 关键 P0 流程测试通过，已知数据丢失问题=0、越权问题=0。
- 所有中高安全漏洞已修复或有正式风险接受记录。
- 同步/通知/AI 故障可监控且有应急流程。
- 三平台实际设备测试覆盖，隐私/法律/商店材料齐备。
