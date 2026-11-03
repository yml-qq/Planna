# 05｜领域建模与数据库设计

## 1. 设计目标

PostgreSQL 作为服务端权威数据源，所有客户端 SQLite 作为本地工作副本。ID 由客户端生成 UUID（建议 UUIDv7，但 UUIDv4 可兼容），避免离线创建后重映射；服务器负责所有者隔离、版本推进和变更游标。客户端版本不可直接假设为服务器版本。

## 2. 领域上下文

- `Identity`：User、DeviceSession、UserPreference、Consent。
- `Organization`：TaskList、Tag、SavedFilter。
- `Work`：Task、TaskStep、TaskEvent、TaskTimeBlock。
- `Time`：RecurrenceRule、Occurrence、Reminder、NotificationDelivery、CalendarEvent。
- `Growth`：Habit、HabitLog、FocusSession、Goal、Milestone、Note。
- `Sync`：ClientMutation、SyncChange、SyncCheckpoint。
- `Intelligence`：AiConversation、AiPlan、AiPlanAction、AiMemory、AiToolAudit、AiUsage。
- `Commerce`：Plan、Subscription、Entitlement、PaymentRecord（后期）。

MVP 中 Task 是核心聚合；List/Tag 通过 ID 关联，Reminder/Recurrence 单独聚合并进行事务协调；不在单个表中硬塞所有领域属性。

## 3. 任务状态及生命周期

| 状态 | 含义 | 允许的迁移 |
|---|---|---|
| `todo` | 未开始 | `in_progress`, `done`, `archived` |
| `in_progress` | 执行中 | `todo`, `done`, `archived` |
| `done` | 已完成 | `todo`, `archived` |
| `archived` | 已归档 | `todo`、或由恢复指令回原态 |
| `deleted_at != NULL` | 软删除 | restore / 永久清理(受保留策略) |

循环任务的“实例完成”不改变 series 的 status；未来发生实例仍存在。AI plan draft 不算业务 task。禁止通过时间过期直接设为 done。

## 4. 时间类型精确定义

- Instant：具体时刻，以 `timestamptz` 存储（逻辑 UTC）；API 用 RFC3339 带 offset 时间串，返回规范 UTC `Z`。
- LocalDate：只有日期无时间的截止日 `DATE`；例如每年生日，跨时区不能从时间戳推算一天偏移。
- LocalDateTime + Zone：每周一 09:00 的浮动当地时间，存 `timestamp without time zone` + IANA TZ（如 `Asia/Shanghai`），在运行时解析具体 instant。
- `start_at`：允许开始；`due_at`：截止时刻；`due_local_date`：全天截止日，二者按 all_day 互斥；`scheduled_*`：排程投入时间，存到 time_blocks。
- 所有客户端设备设置改动都不应改变既存任务真实语义；数据库升级必须保留原定时区。

## 5. 核心表结构与索引策略

核心 DDL 草案见 `../db/001_init.sql`，SPEC-01 增量草案见 `../db/002_spec01.sql`；均需在 DATA-01 转为并验证 EF Core migration。建议字段示意：

| 表 | 必要列 | 关键约束/索引 |
|---|---|---|
| users | id,email,timezone,created_at | email lowercase unique；软删除保留策略 |
| device_sessions | id,user_id,refresh_hash,device_name,revoked_at | unique refresh_hash；按 user 查询 |
| task_lists | id,owner_id,name,sort_order,archived_at,version | `(owner_id, sort_order)` |
| tasks | id,owner_id,list_id,title,status,priority, due_at,due_local_date,time_zone,version,deleted_at | `(owner_id,status,due_at)` 部分索引；FK list 同 owner 校验 |
| task_steps | id,owner_id,task_id,title,is_done,position,estimate_minutes | `(task_id, position)`；建议时长可空 |
| tags/task_tags | id,owner_id,name；task_id,tag_id | 标签名唯一策略决定大小写归一；跨 owner 约束 |
| recurrences | id,owner_id,task_id,kind,rrule,dtstart_local,time_zone | 一个任务最多一个活动规则（MVP） |
| occurrences | id,owner_id,series_task_id,original_local_start,status,completed_at | unique(series_task_id,original_local_start) |
| reminders | id,owner_id,task_id,occurrence_id,scheduled_at,offset_minutes,channel | 幂等 delivery key |
| task_time_blocks | id,owner_id,task_id,start_at,end_at,is_locked | end_at > start_at |
| sync_changes | owner_id,revision,change_index,entity_type,entity_id,op,payload,entity_version | `(owner_id,revision,change_index)` |
| client_mutations | owner_id,mutation_id,device_id,request_hash,response | PK(owner_id,mutation_id) |
| ai_plans | id,owner_id,status,expires_at,snapshot_hash,operations | 只存脱敏必要上下文 |
| ai_breakdown_drafts | id,owner_id,task_id,task_version,steps,snapshot_hash,confirmation_token_hash,expires_at,status | 组合外键约束 owner/task；最多 20 步 |
| account_exports | id,owner_id,status,snapshot_at,storage_key,expires_at | owner 查询；ready 才可下载 |
| account_deletion_requests | id,owner_id,status,policy_version,requested_at | 每账号至多一条；删除事务撤销全部会话 |
| ai_action_audit | id,owner_id,plan_id,action_type,before,after,actor | 按 owner/created_at 查询 |
| scheduled_jobs | id,job_type,dedupe_key,run_at,status,attempts,locked_until | `dedupe_key` unique + ready index |

## 6. 逻辑删除与变更流

服务端删除 Task 时：设置 `deleted_at`、增加 `version`、同时写入 `sync_changes(op='delete')`。不能立即硬删除，否则其他离线设备重新出现“幽灵任务”。保留 tombstone 的最小时间必须覆盖服务端支持的最长离线期；客户端 cursor 过旧则走带提示的完整重建。

变更事件在**同一数据库事务**中与业务数据一同提交。同步推送应返回服务器新版本和 ack，不依赖客户端 `updated_at` 排序。跨实体批量事务只发布成功部分（对于 AI apply 建议全成功或全回滚）。

## 7. 并发和所有权约束

- 所有修改都带 owner_id, entity_id 和 expected_version；SQL `UPDATE ... WHERE id=$1 AND owner_id=$2 AND version=$3 RETURNING version`，0 行时检查是否不存在/无权/冲突（响应不泄漏他人资源存在性）。
- 同一 owner 下 task 的 list_id/tag_id 必须属于该 owner；应用层检查 + 必要的组合外键/触发器保护，不能只信前端。
- 子任务不能引用其他用户或形成循环依赖；MVP 禁止无限层级，优先检查项平铺。
- 对 `client_mutations` 按 `(owner_id,mutation_id)` 唯一，以 request_hash 区分重复重试和 ID 碰撞。

## 8. AI 数据模型

`ai_plans`: `id, owner_id, expires_at, status, snapshot_hash, confirmation_token_hash, operations JSONB, created_at, applied_at`。`operations` 是经过 JSON Schema 校验的白名单指令，不保存原始模型推理过程；所有操作引用 task_id、expected_version、预计变更字段。新草案只存短期确认 token 的哈希；旧草案在迁移时使其过期。

`ai_breakdown_drafts` 持久化用户可编辑的步骤草案和任务基线版本；确认 token 只存哈希。应用事务创建步骤、审计、sync changes 与幂等结果。草案过期、任务版本变化或 owner 不一致时不产生任何业务步骤。

`ai_memories`: `id, owner_id, kind, content, source, confirmed_at, expires_at, deleted_at`；未经用户确认不得入库；支持列出与删除。

`ai_usage`: `request_id, owner_id, provider, model, task_type, tokens_input, tokens_output, estimated_cost, latency_ms, status`；日志不保存明文笔记/任务名称。

## 9. 统计及分区策略

MVP 使用索引和物化/计算视图完成统计；任务量上来后针对 sync_changes、notification_deliveries、ai_usage 按月分区或冷归档。不得过早给 tasks 分区造成复杂主键限制。周报统计基于确定性 SQL，LLM 不自行猜数。

## 10. 数据生命周期

访客本地数据 → 登录后执行合并流程（冲突预览） → 活跃持久存储 → 软删除/归档 → 数据导出 → 用户请求删除 → 立即禁用账号与撤销会话 → 按声明的期限删除备份和下游数据。旧设备回连时不得继续 push；本地副本由用户选择导出或清除。具体保留期必须在上线合规评审中确定，并在产品隐私政策与实际实现一致。

## 11. 迁移与兼容

EF Core migration 只前向增加；上线前数据库快照+恢复演练；破坏性列删除采用 expand/migrate/contract 三阶段；客户端协议变更保留可接受的版本窗口。生产不可在启动时自动执行未经审阅的危险迁移。`db/001_init.sql` 是当前 DDL 草案，创建 .NET 工程时以审阅后的 EF Core 迁移为执行基线并校对二者。
