# 25｜生产运维 Runbook 与事故响应

## 1. 事故等级

- Sev1：任务不可恢复丢失、越权泄露、批量 AI 未经确认改数据、生产数据库不可用。
- Sev2：大范围 sync 堵塞、提醒调度大面积失败、登录失效、客户端升级造成数据库不可读。
- Sev3：某模型 Provider 超时、少量任务冲突 UI 错误、非关键统计异常。

发现后先确认影响账户/时间窗，记录 incident ID，冻结风险变更；未经审批不导出用户明文到个人电脑或 AI 聊天工具。

## 2. 同步积压

检查 API 5xx/429、Postgres pool/locks、owner sync_revision、worker backlog、设备 outbox。不要为了“恢复”清空 outbox 或硬删除 changelog。必要时暂停写入接口、保留 queued operations，修复后按幂等重放；用测试账号先验收。

## 3. 提醒漏发

分层检查：(1) 用户设备通知权限/勿扰；(2) 客户端近期本地调度；(3) 服务端 scheduled_jobs 计数、pending lease、DLQ；(4) push 凭据与 provider ack；(5) 跨时区/夏令时规则。不能把 provider ack 误当用户收到。修复重发应有 delivery_key 去重。

## 4. AI Provider 故障

立刻对 AI 入口降级：保留纯规则输入、手动保存，提示智能规划不可用；暂停不必要的自动总结，切换备用 Provider 前检查权限、数据地域和成本。核心任务 API 不应随着模型连接池不可用而挂掉。

## 4.1 登录邮件积压或失败

`AUTH_EMAIL_QUEUE_BACKLOG` 或 `AUTH_EMAIL_DELIVERY_TERMINAL` 触发后，先检查 Worker 存活、PostgreSQL 连接、SMTP 配置与供应商状态。五分钟没有 `AUTH_EMAIL_QUEUE_HEARTBEAT` 也需按 Worker 停止排查。不要手工清空或改写待发队列；恢复后让租约和退避机制自动处理。排查日志只使用挑战 ID 和聚合计数，不复制邮箱、验证码或令牌。

## 4.2 登录申请大面积返回 429

先核对 API 实际 TCP 对端及 `AUTH_TRUSTED_PROXIES` 是否匹配直接上游代理，确认是否因共享代理 IP 把用户合并到同一限额；再看正常共享出口 IP、真实突发流量及数据库锁等待。不要通过信任任意 `X-Forwarded-For`、删除挑战记录或轮换 `AUTH_CODE_PEPPER` 临时绕过限流；阈值调整应经流量核验并复测并发与误伤。

## 5. AI 批量错误操作

冻结有问题的 AI plan/apply 路径；根据 `ai_action_audit` 确定受影响 task IDs、before/after、关联计划和版本；优先给用户明确通知与受控撤销（再次校验当前版本，不能盲目覆盖用户后续修改）；调查 scope/validator/prompt/tool 变更并补充回归案例。

## 6. 数据库恢复

按照已演练的备份流程恢复到隔离环境，先验证表/索引/变更 revision 和数据完整性，再确定 RPO/RTO 与生产切换方案；恢复过程中冻结写入或记录新的变更用于对账。恢复后通知客户端可能触发 cursor expired/rebootstrap，必须保留各端 outbox。

## 7. 安全事件

撤销泄露 token/秘钥、阻断可疑访问、保留必要审计证据、根据适用法律和合同履行通知/报告；禁止在公共群/未授权 AI 服务贴出真实用户数据。补全 root cause、修复、测试和预防措施。

## 8. 上线后的固定运营动作

每日：看 API 5xx、sync lag、reminder backlog、AI 失败/费用异常。每周：检查备份、更新渠道、依赖漏洞、关键用户反馈。每月：在隔离环境恢复备份，评审 AI prompt/model 改动，检查用户数据删除流程、审计无异常。
