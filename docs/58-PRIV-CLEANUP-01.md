# 58｜PRIV-CLEANUP-01：账号删除后的保留期清理

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43088906012](https://github.com/yml-qq/Planna/actions/runs/43088906012) 已通过。

## 范围与验收

- `AccountCleanupService.RunOnceAsync(now, retention)`：选出 `account_deletion_requests.status='accepted' AND completed_at IS NULL AND requested_at <= now - retention` 的请求（最多 100 个/轮），在单事务内按外键顺序清理该 owner 的业务数据：`ai_action_audits`、`ai_plans`、`ai_breakdown_drafts`、`task_occurrences`、`reminders`、`task_time_blocks`、`task_steps`、`recurrences`、`task_tags`、`tasks`、`task_lists`、`tags`、`sync_changes`、`client_mutations`、`account_exports`、`ai_consents`、step-up 挑战与邮件队列、按邮箱清理登录挑战与登录邮件、`refresh_token_history`（按会话）、`device_sessions`；随后写 `completed_at` 并清空错误码。
- 保留：`users` 墓碑行（旧设备继续得到 `ACCOUNT_DELETED`）、`account_deletion_requests` 审计行、`owner_sync_clocks`（无 PII）；不保留任何任务/清单/标签/步骤/提醒/时间块/同步/授权/导出/会话内容。
- 幂等：已 `completed_at` 的请求不再处理；失败时回滚该请求并写 `last_error_code='CLEANUP_FAILED'`，其余请求继续。
- Worker 新增 `PrivacyCleanupWorker`：启动即执行，随后按 `PRIVACY_CLEANUP_INTERVAL_MINUTES`（默认 60，1..1440）轮询；保留期 `PRIVACY_DELETION_RETENTION_DAYS`（默认 30，1..3650），非法配置启动失败。
- 测试覆盖：过期（40 天）与边界（恰好 30 天）请求被清理、近期请求不受影响、墓碑与审计保留、各类表清零、`refresh_token_history` 与会话清理、重复运行幂等、保留期可配置行为。

## 实现和部署

本切片无 schema 变更、无 API/OpenAPI 变更、无新迁移。清理为独立 Worker 作业，SQL 为固定语句序列（参数化 owner/email），不使用动态拼接。备份/日志保留期限仍需按发布前审定的隐私政策执行；本实现只清理在线库中该账号的数据。

## 边界与后续

- 对象存储/备份中的导出文件、日志与备份介质清理不在本作业范围（需存储与运维策略配合）。
- 账号删除是“接受即禁用 + 保留期后清理”的两阶段流程；清理完成前不恢复任何数据访问。
