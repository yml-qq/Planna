# 56｜PRIV-DELETE-01：账号删除请求与离线设备

状态：代码已实现；PostgreSQL 18 隔离库 CI 验证见 `VALIDATION.md` 的 PRIV-DELETE-01 小节。

## 范围与验收

- `POST /me/deletion-requests`（需登录 + `Idempotency-Key`）：要求 `confirmation_token`（`POST /auth/step-up` 签发、绑定 owner 与 `account_delete` 用途、10 分钟有效）与 `acknowledge_offline_devices=true`；token 无效/过期 `403 STEP_UP_REQUIRED`，ack 缺失 `422 INVALID_DELETION_REQUEST`。
- 成功时**同一事务**：标记 `users.deleted_at`、撤销该 owner 全部 `device_sessions`（`revoked_at` + 版本 +1）、写入 `account_deletion_requests(status='accepted', policy_version='account-deletion-v1')`、写幂等结果；返回 `202 {deletion_request_id, status:"accepted", requested_at, policy_version}`（`account-deletion.schema.json`）。
- 删除后旧设备：认证/同步/导出/再次删除均返回 `401 ACCOUNT_DELETED`（不可重试，不返回其他账号资料）；删除动作不写 revision/`sync_changes`（不属于同步数据）。
- 幂等：同键重放返回原 202 结果；不同键在账号已删除后返回 `ACCOUNT_DELETED`（文档约定的一致不可用结果）。
- 测试覆盖鉴权、缺幂等键、无效 token、未确认离线设备、成功删除的原子性（账号/会话/请求/幂等记录）、旧 token 与第二设备回连被拒、revision 与同步日志不变。

## 实现和部署

本切片无 schema 变更、无新迁移（`account_deletion_requests` 自 SPEC-01 迁移即存在）。事务内使用 owner 级 advisory lock 串行化并发删除；`user.deleted_at` 由认证链路作为账号不可用判定。真实数据清理由独立作业执行，备份/日志保留期限按发布前审定的隐私政策，不在本规范臆定。

## 边界与后续

- `PRIV-CLEANUP-01`：删除后的数据清理作业、导出文件失效/清理与保留策略未实现；客户端删除确认页与本地副本处理属客户端切片。
- 删除后同键 HTTP 重放会被认证层拦截为 `ACCOUNT_DELETED`（服务层幂等仅覆盖删除提交前的重试场景）。
