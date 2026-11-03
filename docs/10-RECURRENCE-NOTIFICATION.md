# 10｜重复任务、时区、提醒和可靠交付设计

## 1. 时间正确性的总规则

- `2026-11-03`（全天日期）和 `2026-11-03T09:00:00+08:00`（具体 instant）不是一回事。
- “每天早上 8 点”是 LocalTime+IANA TimeZone 的周期性意图，不能简单每隔 86400 秒。
- 服务器时刻统一 UTC，显示按用户/事件原始时区；OS DST 规则由标准时区库提供。
- 用户旅行时需要分别定义“跟随当地时区提醒”与“始终按创建时区提醒”的策略，设置默认值并提供解释。

## 2. Recurrence 规则类型

| 类型 | 示例 | 引擎 |
|---|---|---|
| calendar | 每周二/周四 | RFC5545 RRULE |
| calendar | 每月最后一天 | `FREQ=MONTHLY;BYMONTHDAY=-1` 等有效规则 |
| calendar | 每年纪念日 | RRULE + LocalDate |
| after_completion | 完成后 3 天 | 下一次基于完成时刻/日期生成，非 RRULE |
| workday | 每个周一至周五 | RRULE BYDAY=MO,TU,WE,TH,FR |
| legal_workday | 中国法定工作日含补班 | 节假日数据服务，不等同周一至周五 |

`RRULE` 支持 `COUNT`/`UNTIL`，避免错误同时使用；库在 DST 缺失/重叠本地时刻中的行为必须测试。服务端评估兼容 .NET 10 的 RFC 5545 实现；不直接把完整 RFC 语义复制到客户端三次。

## 3. Occurrence 的稳定身份

生成发生实例时键 `series_id + original_local_start + timezone_id`；数据库 unique；每次完成写 occurrence 事件。规则调整分 `本次` 和 `整个系列`（V1.0），未来支持 `本次及未来`。历史实例和完成记录不可被后续修改破坏。只展开未来 30~90 天窗口，循环无终止系列不无限建表。

## 4. 编辑不同范围的用户体验

- `only_this`: 产生本次例外（exdate + override）或独立 instance override，不改变后续。
- `entire_series`: 修改 series master，旧 history 保留，新未来按新版本重算尚未完成的窗口。
- `this_and_future`: 首发不提供；以后通过拆分系列为旧系列终止+新 series，处理历史关系。

## 5. 通知基础架构

**客户端本地通知为近期提醒主通道；服务端 worker 维护需要远程送达的通知任务和设备 token**。同一提醒在多个设备通知是用户设置问题，必须允许控制哪些设备接收。避免同设备本地+远端双响：生成每端 channel/delivery_key 及抑制策略；推送回执不能可靠等同“用户已看见”。

- Client scheduler：同步任务后重建未来 N 天本地提醒；移动端保留系统 API 的待执行数量限制；不一次提交无限重复。
- Server scheduler：PostgreSQL `scheduled_jobs` 记录 run_at、dedupe_key、attempts、status；worker `FOR UPDATE SKIP LOCKED` 抢占；租约/续约防止两 worker 重发；可重试错误指数退避 + DLQ。
- API：创建/更改任务 → 在同一事务更新提醒计划与业务变更；异步 worker 调整远程推送。
- 取消：删除任务/完成任务/取消提醒后，客户端主动取消对应 notification identifier；服务端作废未发送 job。

## 6. 平台限制

**Android**：Android 13+ 需要通知运行时权限；exact alarm 权限需根据当前 Android 版本、用途和商店政策评估，不应该对所有用户强求。WorkManager 用于可延期任务，不作为秒级精确闹钟。

**iOS**：`UNUserNotificationCenter` 调度本地通知；`BGTaskScheduler` 不是准点执行机制；通知展示和是否被系统抑制受专注模式/权限影响。适当提前调度近期本地通知，勿承诺系统无法保证的准点或持续响铃。

**Tauri Desktop**：使用通知插件和系统原生通知；程序完全退出时不要假设 JS timer 继续执行；后台守护或系统启动项需用户许可与平台打包支持，首发可依赖进程运行时提醒与用户设备远程推送配置。

## 7. Exactly-once 的现实边界

数据库可实现幂等逻辑效果；真实外部推送不能保证物理 exactly-once。接入 APNs/Android 推送服务后，使用 stable delivery key + 尽力去重 + 接受偶发重复/网络延迟的事实，产品不要写“100% 必达”。关键通知不能以 AI 服务可用为前提。

## 8. 核心测试矩阵

| 用例 | 期待结果 |
|---|---|
| 2028 闰年 2/29 与平年每年纪念日 | 根据明确业务策略处理平年 |
| 夏令时跳过 02:30 | 按显式策略顺延/跳过并告知 |
| 秋季 01:30 重复两次 | 只生成一个逻辑 occurrence |
| 每月最后一天 | 2/28 或2/29、4/30 正确 |
| 完成后 3 天 | 从实际完成发生时刻/日期推导下一次 |
| 连续跨端完成同一 occurrence | 只认一次完成事件 |
| 关闭通知权限 | 任务保存成功，显示提醒不可用 |
| 服务端 worker crash | lease 过期重试、无重复业务变更 |
| 任务改期 | 旧提醒取消，新提醒创建 |
| 手机重启/时区改变 | 恢复调度并不重发已完成通知 |

## 9. 监控与告警

记录 `reminder_scheduled`, `delivery_attempt`, `provider_ack`, `device_action` 不记录用户明文任务标题；统计发送延迟分布、重试/死信数和同步后本地提醒重建失败率。告警时先区分 worker 故障、push 凭据异常、用户权限关闭和设备 OS 行为。
