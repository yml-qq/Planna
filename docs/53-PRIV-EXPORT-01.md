# 53｜PRIV-EXPORT-01：云端数据导出

状态：代码已实现；[PostgreSQL 18 隔离库 CI 运行 43076494283](https://github.com/yml-qq/Planna/actions/runs/43076494283) 已通过。

## 范围与验收

- `POST /me/exports`（`Idempotency-Key`）：在 `REPEATABLE READ` 只读快照内汇总当前 owner 的已提交数据（account、lists、tasks、steps、tags、reminders、time_blocks、ai_consents、ai_memories），生成 `account-export-v1` JSON 并存入 `account_exports.payload`；状态直接为 `ready`（MVP 同步生成，契约仍返回 `202`，异步 worker 可后续替换）；同键重放原 job，换键复用 `409 IDEMPOTENCY_KEY_REUSED`。
- `GET /me/exports` 列出最近 100 个导出 job；`GET /me/exports/{id}` 返回状态（跨 owner `404`）；`GET /me/exports/{id}/download` 仅 owner 且 `ready`、未过期（24 小时）时返回 JSON 附件；过期 `410 EXPORT_EXPIRED`，未就绪 `409 EXPORT_NOT_READY`，跨 owner `404`。
- 导出内容遵循 `spec/account-export.schema.json`：`source=cloud`、`pending_mutation_ids=[]`、任务字段满足 `task.schema.json`；`ai_memories` 因表未实现返回空数组，AI consent 仅导出允许 scope。
- 迁移 `202611120009_AccountExportPayload` 为 `account_exports` 增加 `payload jsonb`；审阅 SQL 为 `server/export-migrations.sql`。测试覆盖鉴权、快照内容（各表数据）、同键重放/换键、列表、跨 owner、未就绪与过期、删除账号，以及导出不推进 revision/不写业务变更。

## 实现和部署

导出为只读快照 + 单行 job 写入；`client_mutations` 记录幂等结果（含请求哈希，防止跨端点复用 mutation_id）。下载返回 `Content-Disposition: attachment` 且文件名仅含服务端 UUID。真实对象存储与 24 小时清理任务为后续部署项（当前到期校验在读取时执行）。

## 边界与后续

- 账号删除（PRIV-DELETE-01）、step-up 复核、导出文件加密暂存/清理任务与客户端本地导出未实现。
- `ai_memories` 表与导出字段待 AI 记忆切片；导出版本升级需同步 Schema 与契约测试。
