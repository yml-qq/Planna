# 135｜NOTE-05a-b：附件保留期清理（服务 + Worker）

状态：代码已实现；本地 build/format/契约 **36 项**通过；[Server integration 45052218579](https://github.com/yml-qq/Planna/actions/runs/45052218579) 与 [Desktop core 45052218620](https://github.com/yml-qq/Planna/actions/runs/45052218620) 均 **success**。清理逻辑由冒烟直接调用验证；Worker 定时循环未做集成测试。

## 范围与验收

- **清理服务** `NoteAttachmentCleanup.PurgeDeletedAsync(olderThan)`：
  - 选出 `deleted_at IS NOT NULL AND deleted_at < cutoff` 的附件；
  - 先删磁盘文件（失败则保留该行、跳过本轮，后续重试），再删数据库行；
  - 返回 `{purged_attachments, removed_files}`。
- **API 注册**：`INoteAttachmentCleanup` 单例，供维护/运维复用。
- **Worker**：新增 `NoteAttachmentCleanupWorker`（BackgroundService）：
  - 默认保留 30 天（`NOTE_ATTACHMENT_RETENTION_DAYS` 1..3650）；
  - 默认每 60 分钟执行（`NOTE_ATTACHMENT_CLEANUP_INTERVAL_MINUTES` 1..1440）；
  - 仅在有清理时记录心跳日志；异常记录后继续。
- **冒烟**：上传→软删→解析清理服务（cutoff=未来 1 分钟）→断言行/文件均删除、报告 `{1,1}`。
- **文档**：附件文件目录与清理策略写入 `docs/134-NOTE-05a.md`/本文件。

## 语义与边界

- 清理只处理**软删且超过保留期**的附件；活动附件不受影响。
- 先删文件后删行：若进程在两步之间崩溃，可能留下指向缺失文件的行（下载返回 `ATTACHMENT_FILE_MISSING`），比遗留孤儿文件更安全；后续人工/运维可再清。
- 清理不会校验文件是否属于该租户路径之外（storage_key 由服务端生成）；若数据库被篡改，理论上可删除任意文件——信任 DB 边界。
- Worker 定时循环、配置解析未在 CI 集成测试；只验证服务逻辑。
- 未清理历史活动/已删笔记级联附件（note 软删不触发附件软删）；未做配额/病毒扫描。

## 后续

- `NOTE-05b`：桌面上传/下载 UI、附件与同步/备份策略、配额与病毒扫描。
