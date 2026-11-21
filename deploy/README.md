# 部署与运维（REL-01 就绪包）

本目录只包含**无凭据的部署说明与配置模板**；真实密钥、证书、签名材料由 secret manager/CI secrets 注入，禁止提交。

## 1. 环境矩阵

| 环境 | 用途 | 数据 | 凭据 |
|---|---|---|---|
| local | 开发/离线联调 | Docker Compose PostgreSQL 18 + 合成数据 | 本地占位值 |
| staging | 发布前验证 | 独立库、合成数据 | staging secret manager |
| production | 生产 | 独立库、备份/恢复演练 | 生产 secret manager、审计留痕 |

## 2. 配置

- 复制 `.env.example` 为 `.env`（不要提交），逐项替换 `<secret>`；API 与 migrate 工具都读取 `PLANNADB_CONNECTION`。
- 必填：`PLANNADB_CONNECTION`、`JWT_ISSUER`、`JWT_AUDIENCE`、`JWT_SIGNING_KEY`（≥32 字节）、`AUTH_CODE_PEPPER`（≥32 字节）、`AUTH_OUTBOX_KEY`。
- 可选：`AUTH_TRUSTED_PROXIES`（仅可信上游 IP）、`AI_PROVIDER`（`none` 关闭）、`AI_RATE_LIMIT_PER_MINUTE`、SMTP 与 `PRIVACY_*`（worker）。
- 启动即失败：缺少必填项或长度不足会抛 `InvalidOperationException`，不会带病启动。

## 3. 发布步骤（无签名材料时可执行）

```bash
# 1) 门禁：构建/格式/契约/迁移冒烟（CI 已执行）
dotnet build server/Planna.slnx --configuration Release --warnaserror
dotnet format server/Planna.slnx --verify-no-changes

# 2) 迁移（先备份；expand/migrate/contract，禁止回滚已删列）
PLANNADB_CONNECTION=... dotnet run --project server/tools/Planna.Migrate

# 3) 起 API/Worker（示例；实际用进程管理器/容器编排）
PLANNADB_CONNECTION=... dotnet Planna.Api.dll
PLANNADB_CONNECTION=... dotnet Planna.Worker.dll

# 4) 健康检查与冒烟
curl -fsS http://127.0.0.1:8080/health/live
```

发布产物：`release.yml` 在 tag `v*` 或手动触发时构建 Release 并上传 API/Worker/Migrate 及 `SHA256SUMS.txt`。

## 4. 备份与恢复（每次演练记录 RPO/RTO）

```bash
# 备份（每日 + 发布前）
pg_dump --format=custom --no-owner --file=planna-$(date +%Y%m%dT%H%M%S).dump "$PLANNADB_CONNECTION"

# 恢复到隔离实例（不要在生产覆盖）
createdb planna_restore
pg_restore --no-owner --dbname=planna_restore planna-YYYYMMDDTHHMMSS.dump
PLANNADB_CONNECTION=...planna_restore dotnet run --project server/tools/Planna.Migrate
```

- 备份加密存储、保留多代、异地副本；恢复演练在隔离环境进行。
- 回滚策略：应用可回滚上一版本二进制；数据库迁移以**前向兼容**为原则（expand/migrate/contract），破坏性迁移需先导出并审批，回滚通过备份恢复而非逆向删列。

## 5. 监控与告警（最低集）

- `/health/live` 存活；5xx 比例、p95 延迟、限流 429。
- PostgreSQL 连接池等待、慢查询、磁盘、备份状态。
- 同步 push 失败/冲突率/游标滞后；worker 心跳、邮件队列 backlog、清理任务。
- AI provider 超时/schema 失败/成本；通知权限与投递（客户端）。

## 6. 发布门禁清单

见 `docs/95-REL-01.md`；未完成项（生产签名、真实部署、外部渗透）在清单中明确标注，不得口头宣称已上线。
