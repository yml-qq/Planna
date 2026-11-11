# 55｜PRIV-STEPUP-01：敏感操作邮件复核

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43081769119](https://github.com/yml-qq/Planna/actions/runs/43081769119) 已通过。

## 范围与验收

- `POST /auth/step-up/challenges`（需登录 + `Idempotency-Key`）：向当前账号邮箱发送 6 位一次性码，`202 {purpose:"account_delete", expires_at}`；同键重放原过期时间且**不重发**；每账号 15 分钟最多 3 次挑战（超出 `429 RATE_LIMITED` + `Retry-After`），验证码 10 分钟有效且只存 HMAC 哈希。
- `POST /auth/step-up`：校验最新未消费挑战（更早的挑战不参与）、最多 5 次尝试、错误码 `401 INVALID_STEP_UP`、超限 `429`；成功后消费验证码并返回 `200 {confirmation_token, expires_at}`：10 分钟有效、HMAC 签名、绑定 owner 与 `account_delete` 用途的 token（不落库）。
- 邮件投递使用独立 `auth_step_up_email_outbox`：租赁/租约超时回收、指数退避、5 次上限、过期或已消费挑战自动清理密文；Worker 新增 `StepUpEmailWorker` 消费队列，登录验证码队列与限流完全不受影响。
- 测试覆盖鉴权、挑战/投递/幂等重放、验证码消费、token 绑定（owner/purpose/篡改/过期）、过期挑战、尝试上限、挑战限流、删除账号，以及 step-up 不写 revision/`sync_changes`/`client_mutations`。

## 实现和部署

迁移 `202611120010_StepUpChallenges` 新增 `auth_step_up_challenges` 与 `auth_step_up_email_outbox`；审阅 SQL 为 `server/stepup-migrations.sql`。验证码密文沿用 `LoginCodeCipher`（按挑战 ID + 邮箱绑定），发送沿用 `ILoginCodeSender`（生产为 SMTP，Worker 注册）。API 仅入队，不阻塞请求。

## 边界与后续

- 账号删除请求（`POST /me/deletion-requests`，校验 token + 原子禁用/撤销会话）为 `PRIV-DELETE-01`；真实清理作业与备份保留策略按隐私政策另行实现。
- 挑战投递失败只影响该次验证（可重新发起，受限流约束）；未实现多通道（短信/推送）复核。
