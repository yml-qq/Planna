# 154｜NOTE-05c-b-b-b-b-b-b-a：数据库级附件限流（跨实例）

状态：代码已实现；本地 build/format/契约 **36 项**通过；CI 见 `VALIDATION.md`。

## 范围与验收

- 新迁移 `attachment_rate_windows(owner_id PK, window_start, count)`（用户级联删除、count≥0）。
- 限流窗口从进程内静态字典改为**数据库行**：
  - 上传事务内先取 `pg_advisory_xact_lock(hashtext(owner))`，再读取/初始化窗口；
  - 过期（≥1 分钟）重置为 count=1；未过期且 count≥上限 → 429 + 动态 Retry-After；否则 count+1。
- **冒烟改为跨实例验证**：第一个 WebApplicationFactory 上传 2 次，第二个独立实例第 3 次上传 → 429，证明窗口在 DB 共享；Retry-After 仍为 1..60。
- 备份/恢复演练表清单加入 `attachment_rate_windows`。
- 契约不变（36 项）。

## 语义与边界

- 仍是**固定窗口**（非滑动/令牌桶）；多实例共享同一行，advisory lock 串行化同 owner。
- 限流计数在事务内；配额/文件失败回滚会**撤销本次计数**；成功/重放提交才计数。
- 每 owner 一行、不清理历史行（用户删除级联）；高频 owner 每次上传多一次 SELECT/UPDATE。
- 429 时事务回滚（不会把拒绝次数计入）。
- 未实现分布式滑动窗口、Redis、速率告警或客户端退避。
- 客户端仍未消费 Retry-After。

## 后续

- `NOTE-05c-b-b-b-b-b-b-b`：滑动窗口/令牌桶、清理策略、客户端退避、预览与病毒扫描。
