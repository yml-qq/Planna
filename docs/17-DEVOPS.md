# 17｜部署、CI/CD、运行环境与可观测性

## 1. 环境分层

- `local`：开发者机器，Docker Compose PostgreSQL/Redis/MinIO/Mock AI Provider，使用合成数据。
- `staging`：接近生产的部署，独立 DNS/凭据/数据库，外部推送测试凭据。
- `production`：最小暴露接口、强制 TLS、备份、监控告警、严格变更审批。

MVP 使用 Docker Compose 并不意味着可以忽略高可用和恢复；在单机故障恢复要求明确前不盲目承诺 99.99%。

## 2. 服务组件

`nginx/caddy`、`api`、`worker`、`postgres`、`redis`、`object storage`（可接云服务）、`monitoring`。API 和 worker 可各启动多副本（DB 连接与租约安全前提）；Redis 不存唯一权威任务数据。TLS 与数据库非公网访问。

## 3. 配置变量建议

```dotenv
APP_ENV=staging
ASPNETCORE_URLS=http://0.0.0.0:8080
ConnectionStrings__Postgres=Host=...;Database=planna;... # 注入 Secrets，不写入 git
REDIS_URL=redis://...
JWT_ISSUER=https://api.example.com
JWT_AUDIENCE=planna-clients
AI_PROVIDER=mock
AI_MAX_REQUEST_TOKENS=8192
AI_DAILY_USER_BUDGET_USD=0.10
PUBLIC_BASE_URL=https://api.example.com
OBJECT_STORAGE_ENDPOINT=https://s3.example.com
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
```

这些只是配置字段示例，不是有效生产凭据。JWT 私钥、推送证书、支付密钥不能出现在配置示例里。

## 4. CI/CD 推荐流水线

PR：格式化 → 静态分析 → domain/unit → PostgreSQL integration → OpenAPI lint → AI eval 样本 → 前端 lint/typecheck → Android/iOS 构建检查（macOS runner 运行 iOS） → 安全扫描 → 产出报告。

Main：镜像构建+SBOM/签名 → staging 部署 → smoke+迁移测试 → 人工批准 → production 分批发布 → 观察错误率 → 必要时回滚应用；数据库采取兼容式迁移，不能依赖回滚已删除的列。

桌面多平台签名/更新独立流程；移动端商店上架需开发者账号、各自签名与商店审核，不能用通用 Linux CI 假装编译 iOS。

## 5. 数据库备份和恢复

定期物理/逻辑备份（频率按 RPO 决定），保留多版本并加密存储；离站备份；每月在隔离环境进行真实恢复演练，记录 RPO/RTO 结果。不要只检查备份文件是否存在。导出过程中管理个人数据授权。

## 6. 关键监控

- HTTP：成功/5xx/latency p95/p99、限流、请求量。
- PostgreSQL：连接池等待、慢查询、锁等待、磁盘、复制/备份状态。
- Sync：push failure、conflict rate、cursor lag、device pending mutations。
- Reminder：due backlog、job retries、provider errors、发出延迟。
- Auth email：`AUTH_EMAIL_QUEUE_HEARTBEAT` 每分钟出现；`AUTH_EMAIL_QUEUE_BACKLOG` 或 `AUTH_EMAIL_DELIVERY_TERMINAL` 需告警；心跳缺失五分钟需告警。日志只包含聚合计数与挑战 ID。
- Auth rate：监控验证码申请的 `429` 比例、按来源的误伤反馈、数据库 advisory lock 等待和挑战表增长；经代理部署时配置 `AUTH_TRUSTED_PROXIES`，只列出可信直接上游 IP，避免把所有用户算作同一来源或信任伪造转发头。
- Auth login：分别监控验证码申请与校验的 `429` 比例、登录失败事件表增长及 24 小时清理数；不记录邮箱、验证码、原始 IP 或设备 ID。登录失败事件表清理依赖 Worker，停机时需关注表增长。
- AI：Provider timeout、schema-invalid、cost/user/day、plan_stale、action_denied、model quality regression。
- 客户端：crash/ANR、离线操作失败、通知授权率、异常同步重试。

## 7. 运维告警与值班

建立 P0 数据丢失/越权、P1 API 大面积不可用/同步阻塞、P2 AI 降级/推送延迟分级；每级有值班联系人、恢复目标、跑本模板 `25-OPERATING-RUNBOOK.md`。日志保留期、隐私脱敏和备份需要符合法律与内部政策。

## 8. 发布迁移策略

采用 expand/migrate/contract：先新增兼容字段 → 同步新旧代码 → 完成数据回填 → 淘汰旧字段。API 兼容过去几个移动端版本；强制升级只能用在真实安全风险和不可兼容变更，且要提供离线数据导出机制。

## 9. 服务容量规划起点

先压测单机 API、PG 和 worker 的真实吞吐，再按目标 DAU、每用户任务量、每日同步事件数、AI 调用次数和提示 token 数做容量表；关键成本往往来自 AI API、推送/短信以及存储。不要凭框架名称宣称可支持某个并发数。
