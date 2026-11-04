# 21｜Architecture Decision Records（架构决策记录）

每个 ADR 记录：状态 Accepted/Proposed、场景、选项、决策、理由、代价、复审触发条件。下面是初始建议，若用户/团队以后修改需显式提交新 ADR。

## ADR-001：服务端 C# / ASP.NET Core .NET 10.0（Accepted，替代原 Rust 决策）

背景：用户明确将服务端从 Rust 改为 C# / ASP.NET Core .NET 10.0，并要求高可维护性。采用按业务模块划分的模块化单体，API、Application、Domain、Infrastructure 分层，Worker 独立宿主。原 Rust/Axum 服务端方案废止；Tauri 桌面端的 Rust commands 保留。代价是 C# 服务端与 Tauri Rust 客户端不共享代码，以 OpenAPI/Schema 和共同测试夹具保持语义一致。

## ADR-002：桌面 Tauri 2 + Vue 3/TS（Accepted）

背景：用户指定 Tauri。选择 Vue 3 + Vite + Pinia，Tauri Rust command 访问受限系统能力，SQLite 服务层放 Rust；不在 render 层暴露任意 SQL。成本：系统 WebView 差异、桌面三平台签名与更新复杂。复审：出现平台兼容/无障碍不可接受问题。

## ADR-003：Android/iOS 独立原生（Accepted）

Android Kotlin+Jetpack Compose+Room；iOS Swift+SwiftUI+SQLite/GRDB。拒绝 Flutter/React Native/Tauri mobile。优势：原生通知、Widget、系统快捷方式和平台一致体验；代价：客户端重复实现。解决：OpenAPI+共同测试夹具+功能契约，不跨语言分享 UI。

## ADR-004：PostgreSQL（Accepted）

原因：强事务、约束、JSONB、全文检索/索引、行锁同步游标容易实现；MySQL 是可替代方案，但在锁语义、JSON 过滤和全文检索处需要单独设计，不要一套 SQL 同时假设两者兼容。

## ADR-005：离线优先 SQLite（Accepted）

UI 本地单一数据来源，本地 CRUD 与 outbox 在一个事务里完成；跨端使用幂等 mutation+增量 change log。优点是可靠离线和快速 UI；代价是同步、迁移、冲突测试复杂，不允许首发绕开这一点。

## ADR-006：每账户提交 revision 而非 `updated_at`/裸序列（Accepted）

同一 owner 的事务锁/推进 revision 以保证提交顺序一致性；`BIGSERIAL` 可能在事务提交顺序上出现空洞导致 pull 漏事件。代价：同账户写串行，但个人应用写入规模合理。复审：大型共享工作区/高并发写入。

## ADR-007：后端模块化单体 + 独立 worker（Accepted）

首发 ASP.NET Core API + 独立 .NET Worker Service，避免微服务网络和分布式事务成本；Worker 与 API 可分别扩容。复审：按业务模块测到不可接受资源争用。

## ADR-008：AI 不直接改数据库（Accepted）

模型只返回草稿/计划或调用白名单读工具；写入依赖用户确认的 plan + 业务应用服务，始终检查版本、权限、交易原子性和审计。复审：只可能增强有限自动化许可，不可取消最终后端权限验证。

## ADR-009：RRULE + after_completion 独立（Accepted）

按日历重复遵循 RFC 5545；“完成后 N 天”用专门规则，发生实例与完成历史独立表。复审：引入外部日历双向同步。

## ADR-010：AI Provider 可切换（Accepted）

适配不同模型服务商的结构化输出、工具接口和成本/超时，早期只部署 1 主 1 备/Mock；不在业务层硬编码型号。复审：服务商政策/性能/账单发生重大变化。

## ADR-011：数据迁移/备份优先（Accepted）

保持前向兼容 schema，保留 tombstone 与导出能力；强制用户升级必须保护离线数据。复审：用户加入端到端加密诉求。

## ADR-012：首发个人优先（Accepted）

不做复杂团队协作和社交，但保留后期共享清单的 ID/权限扩展余地，避免早期组织模型膨胀。

## ADR-013：EF Core 命令侧 + Dapper 复杂查询侧（Accepted）

简单 CRUD 和迁移由 EF Core 负责；复杂只读查询由 Dapper 的专用 Query Service 负责。两者在 Infrastructure 内按业务模块分开放置，不混入同一个 Repository，也不建立泛型万能 Repository。应用服务协调权限、事务、幂等、版本和同步日志；Dapper SQL 强制参数化与 owner 过滤。若以后需要 Dapper 写操作，先记录例外及事务语义。复审：真实查询性能和维护成本出现可测量问题。

## ADR-014：SPEC-01 首发数据主权与 AI 草案（Accepted）

任务拆解采用持久化可编辑草案，最多 20 步，整组确认并按任务版本应用；AI 计划 V1.0 只支持完整操作集合。数据导出分本地离线副本与云端已提交快照，统一版本化 JSON；云端文件就绪后保留 24 小时供授权下载。账号删除经近期身份复核，接受后立即禁用账号及会话，旧设备停同步并保留本地副本供用户选择导出或清除。代价是新增草案、导出任务和删除请求状态及跨设备边界测试；保留期限由发布前隐私政策审定。详见 `28-SPEC-01.md`。

## ADR-015：邮箱验证码首次登录即注册（Accepted）

补齐原 OpenAPI 缺少的登录验证码申请端点；验证通过时原子创建个人账号、Inbox、同步时钟和设备会话。独立 `/auth/register` 暂不实现，页面可复用同一入口。刷新令牌只存哈希和使用历史，重放撤销对应设备会话；访问令牌逐次查会话状态。代价是邮件供应商可用性、验证码与令牌历史清理及发信 outbox 仍需后续运维切片处理。详见 `31-AUTH-01.md`。

## ADR-016：PostgreSQL 18 单版本基线（Accepted）

当前开发库使用 PostgreSQL 18.6；项目以 PostgreSQL 18 为唯一支持的服务端主版本，CI 和本地 Docker 示例只运行 18。不承诺 PostgreSQL 16 兼容性，也不再维护双版本测试矩阵。历史在 16 上通过的测试记录仅作为当时的验证证据。部署到其他主版本前须另行评估并验证迁移、查询和备份恢复。

## ADR-017：登录邮件加密 outbox 与独立 Worker（Accepted）

验证码挑战和加密邮件负载在同一 PostgreSQL 事务提交，API 返回 `202` 代表排队成功。独立 .NET Worker 使用 `FOR UPDATE SKIP LOCKED`、限时租约和有限重试处理；多实例可并发领取。数据库只保存 HMAC 验证哈希与 AES-GCM 密文，发送成功、过期或终止失败后清除密文。SMTP 发送与数据库确认无法形成原子事务，崩溃重试可能重复投递同一验证码；客户端仍只能消费一次。加密密钥必须在 API 和 Worker 中一致，轮换需先处理未完成队列。详见 `32-AUTH-02.md`。
