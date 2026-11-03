# 27｜需求—接口—数据—测试追踪矩阵

这是开发代理和 QA 每次变更必须维护的“单一追踪表”。状态初始一律 `planned`，不能因为文档已经存在就标为 implemented。

| Story/需求 | 后端 | 数据表 | API | 三端 | 验收测试 | 状态 |
|---|---|---|---|---|---|---|
| US-001 任务离线创建 | tasks + sync | tasks/client_mutations/sync_changes | /tasks,/sync/* | Tauri/Android/iOS | SYNC-01 | planned |
| US-002 幂等提交 | sync | client_mutations | /sync/push | 全端 sync engine | SYNC-02 | planned |
| US-003 编辑冲突 | tasks + sync | tasks/version | PATCH,/sync/push | 冲突 UI | SYNC-03 | planned |
| US-004 AI 解析 | ai | 可选请求日志 | /ai/parse | AI 快捷输入 | AI-01 | planned |
| US-006 AI 规划 | ai + scheduler | task_time_blocks/ai_plans | /ai/plans | 计划预览 | AI-02 | planned |
| US-007 AI stale apply | ai + tasks | ai_plans/ai_action_audits | /ai/plans/{id}/apply | 确认弹窗 | AI-03 | planned |
| US-008 AI 授权 | identity + ai | ai_consents | /ai/consent | 隐私设置 | AI-04 | planned |
| US-009 循环实例 | recurrence | recurrences/task_occurrences | /tasks/* | 三端编辑/查看 | TIME-01 | planned |
| US-011 通知权限 | reminders | reminders/scheduled_jobs | 任务/提醒 API | Android/iOS/Tauri | REM-01 | planned |
| US-012 删除与墓碑 | tasks + sync | deleted_at/sync_changes | DELETE,/sync/pull | 三端回收站 | SYNC-04 | planned |
| US-013 游客合并 | identity + sync | user/device | bootstrap,push | 各端首次登录 | SYNC-05 | planned |
| US-016 越权拒绝 | identity/policy | 所有 owner 表 | 全部受保护 API | 全端 | SEC-01 | planned |
| US-019 AI 计划整份确认 | ai + tasks | ai_plans/ai_action_audits | /ai/plans/{id}/apply | 计划预览 | AI-04 | planned |
| US-020 可选模块入口 | preferences + local UI | 本地偏好/既有模块数据 | 无新增服务端 API（UX-02 核对跨端偏好） | Tauri/Android/iOS | UX-02 | planned |
| US-021 导出与账号删除 | identity + privacy + sync | users/device_sessions/account_exports/account_deletion_requests | /auth/step-up,/me/exports/*,/me/deletion-requests | 设置/离线回连 | PRIV-01 | planned |
| US-022 AI 任务拆解 | ai + tasks | task_steps/ai_breakdown_drafts/sync_changes | /ai/tasks/{id}/breakdowns,/ai/breakdowns/{id}/* | 任务详情/草案预览 | AI-02 | planned |

## 维护要求

每个 PR 必须引用上述 Story/测试 ID；改过 API 或数据库要更新此表、机器契约与测试数据。状态只有 `planned/in_progress/verified/blocked`，只有真实运行通过测试并经过审查才可标 `verified`。新增模块先加入矩阵，再由 AI 代理编码，防止“功能上线但没有验收”。
