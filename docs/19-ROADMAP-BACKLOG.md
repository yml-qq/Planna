# 19｜版本路线图、史诗和 AI 编码任务拆解

## 1. 排期说明

这是按依赖排序的**相对里程碑**，非承诺工期；实际开发时间取决于团队数量、工程基础、商店审核和自动化测试水平。MVP 标准是任务可靠 + 本地离线 + 跨端同步 + AI 核心闭环，而不是页面数量。

## 2. Epic 拆解（依赖严格）

| Epic | 任务 ID | 交付说明 | 前置 | 验收 |
|---|---|---|---|---|
| E00 | SPEC-01 | 冻结 V1.0 任务拆解/导出/删除/AI 整份确认契约 | 无 | OpenAPI/Schema/合成样例一致性 |
| E01 | INFRA-01 | .NET solution/CI/Compose/health | E00 | clean build |
| E02 | AUTH-01 | 用户/设备/refresh token | E01 | session tests |
| E03 | DATA-01 | EF Core schema/migrations/索引 + Dapper 查询契约 | E01 | 空库迁移和查询集成测试 |
| E04 | TASK-01 | Task 创建写入 `POST /tasks` + owner 权限 + 幂等（已交付，见 `30-TASK-01.md`） | E02,E03 | owner tests |
| E04 | TASK-READ-01 | Task 分页读取 `GET /tasks`（owner 过滤 + 游标分页，已交付） | TASK-01 | pagination tests |
| E04 | TASK-UPDATE-01 | Task 详情/更新/软删（`GET/PATCH/DELETE /tasks/{id}`，版本化 + 幂等） | TASK-READ-01 | state tests |
| E04 | TASK-COMPLETE-01 | Task 完成/撤销完成（已交付，见 `39-TASK-COMPLETE-01.md`） | TASK-UPDATE-01 | state tests |
| E04 | TASK-LIST-01 | List CRUD（`GET/POST /lists` + `GET/PATCH/DELETE /lists/{id}`，owner 权限 + 幂等） | TASK-COMPLETE-01 | owner tests |
| E04 | TASK-TAG-01A | Tag CRUD（`GET/POST /tags` + `GET/PATCH/DELETE /tags/{id}`，已交付，见 `41-TAG-01A.md`） | TASK-LIST-01 | owner tests |
| E04 | TASK-TAG-01B | 任务标签关联（批量替换）与按标签过滤（已交付，见 `42-TAG-01B.md`） | TASK-TAG-01A | association tests |
| E04 | TASK-STEP-01 | 步骤 CRUD 与查询（已交付，见 `43-STEP-01.md`） | TASK-TAG-01B | step tests |
| E05 | SYNC-01 | per-owner revision + changelog | E03 | concurrency tests |
| E05 | SYNC-PUSH-01 | 批量 mutation 推送（task upsert/delete、幂等、冲突，已交付，见 `45-SYNC-PUSH-01.md`） | SYNC-01 | replay tests |
| E05 | SYNC-PULL-01 | 增量拉取 `GET /sync/pull`（revision 游标、has_more、410，已交付，见 `44-SYNC-PULL-01.md`） | SYNC-02 | pull tests |
| E05 | SYNC-BOOTSTRAP-01 | bootstrap 快照与游标衔接（已交付，见 `46-SYNC-BOOTSTRAP-01.md`） | SYNC-PULL-01 | data-loss tests |
| E06 | DESK-01 | Tauri shell/local SQLite/task UI | SYNC-02 | offline scenario |
| E06 | AND-01 | Compose shell/Room/task UI | SYNC-02 | offline scenario |
| E06 | IOS-01 | SwiftUI shell/SQLite/task UI | SYNC-02 | offline scenario |
| E07 | TIME-01 | 时间语义/RRULE/实例 | TASK-COMPLETE-01 | DST fixtures |
| E07 | REM-01 | 本地提醒/权限/动作 | TIME-01 | device tests |
| E07 | REM-02 | worker/远程推送（V1.1） | TIME-01 | retry tests |
| E08 | AI-01 | Provider/structured parse | E02,TASK-COMPLETE-01 | golden tests |
| E08 | AI-02 | 任务拆解草案/编辑确认（V1.0） | AI-01 | 结构化契约、no auto-write |
| E08 | AI-03 | Scheduler hard constraints | TIME-01 | conflict=0 |
| E08 | AI-04 | Plan preview/apply/audit | AI-03,SYNC-03 | stale apply tests |
| E09 | UX-01 | 设计系统/无障碍/空状态 | E06 | accessibility tests |
| E09 | UX-02 | 可选模块入口与轻量首页（V1.1） | UX-01 | 隐藏/恢复不丢数据 |
| E10 | HAB-01 | 习惯/打卡 | E04 | repeat tests |
| E10 | FOCUS-01 | 专注计时 | E04 | interruption tests |
| E10 | GOAL-01 | 目标/里程碑 | E04 | progress tests |
| E10 | NOTE-01 | 轻笔记 | E04 | search/privacy |
| E11 | PRIV-01 | 数据导出与账号删除（V1.0） | AUTH-01,SYNC-03 | 导出/删除/离线设备回连测试 |
| E11 | SEC-01 | 首发安全与隐私审核 | AUTH-01,DATA-01,SYNC-03,REM-01,AI-02,AI-04,UX-01,PRIV-01 | 权限/数据治理/渗透测试 |
| E11 | REL-01 | 上架/签名/灰度/运维 | SEC-01 | release checklist |

E04 切片命名以实际交付为准：早期 `TASK-01` 交付的是任务创建写入 `POST /tasks`（`30-TASK-01.md`），不是原计划的 List CRUD；List CRUD 并入 `TASK-03` 后拆分为 `TASK-LIST-01`（清单 CRUD）与 `TASK-TAG-01A/B`、`TASK-STEP-01`（标签、任务标签关联、步骤与查询）。任务读取单独列为 `TASK-READ-01`（`37-TASK-READ-01.md`）；原 `TASK-02`（更新/完成/软删）拆分为 `TASK-UPDATE-01`（详情/更新/软删，`38-TASK-UPDATE-01.md`）与 `TASK-COMPLETE-01`（完成/撤销完成，`39-TASK-COMPLETE-01.md`）。E05 的 `SYNC-02`（push/outbox/幂等）交付为 `SYNC-PUSH-01`（`45-SYNC-PUSH-01.md`）；`SYNC-03`（pull/bootstrap/conflict）拆分为 `SYNC-PULL-01`（增量拉取，`44-SYNC-PULL-01.md`）与 `SYNC-BOOTSTRAP-01`（快照与冲突）。

## 3. V1.0 交付范围

V1.0 必须完成 E00-E09 的首发任务，其中 `REM-02`（远程推送）和 `UX-02`（可选模块入口）明确延期到 V1.1；还必须完成 `PRIV-01` 数据导出与账号删除、`SEC-01` 首发安全审核和 `REL-01` 上架门禁。`SPEC-01` 已在 `28-SPEC-01.md` 固定首发契约；进入实现前仍须把 SQL 草案转换为 EF Core migration 并通过真实数据库测试。客户端可按 `桌面→Android→iOS` 的研发实际效率构建样板，但**产品首发承诺覆盖哪些平台，应在对外宣传前经过真机测试**。不因同步难度而退化成纯在线 App。

## 4. V1.1 功能扩展

`REM-02`、`UX-02`、HAB-01/FOCUS-01/GOAL-01/NOTE-01、四象限视图、用户数据统计、习惯提醒、组件快捷操作、语音快捷录入和 AI 周复盘。V1.2 补充 AI 动态重排、OCR、语义搜索、多模型优化和第三方日历集成候选。

## 5. 每个 Epic 的完成标准

- 需求与数据契约版本固定。
- 有至少一个可运行 happy path + 一个错误/边界 + 一个权限测试。
- 涉及本地数据的功能有离线与同步场景。
- 涉及提醒/时区的功能有 DST 与权限场景。
- 涉及 AI 的功能有拒绝执行、模型超时、过期计划、恶意输入与成本统计场景。
- 文档、错误码、监控、回滚说明齐备。

## 6. 产品迭代优先级原则

首发不能舍弃正确性、安全、导出、提醒和本地可靠性去抢做“AI 自动化炫技”；功能扩展需用留存、用户反馈和真实 AI 成本证明价值。V2.0 的共享清单/多端日历生态必须经过单独的隐私与协作模型评审。
