# 57｜AI-CONSENT-01：AI 数据授权与 scope 校验

状态：代码已实现；PostgreSQL 18 隔离库 CI 验证见 `VALIDATION.md` 的 AI-CONSENT-01 小节。

## 范围与验收

- `GET /ai/consent`：返回当前 owner 的长期 AI 数据授权 `{allowed_scopes, updated_at}`；从未授权或已撤销时为空列表、`updated_at: null`。
- `PUT /ai/consent`（`Idempotency-Key` + `{allowed_scopes}`）：整份替换长期授权；白名单仅 `task_drafts / plan_preview / task_notes / calendar_free_slots`，未知 scope、重复项或超过白名单数量返回 `422 INVALID_SCOPE`；空数组即撤销；同键重放返回原 view 与 `updated_at`，换键复用 `409 IDEMPOTENCY_KEY_REUSED`。
- `POST /ai/parse` 的 `allowed_scopes` 现在必须：属于白名单、包含 `task_drafts`、且全部包含在已持久化授权中；否则 `403 AI_SCOPE_DENIED`（未授权时任何 scoped 解析都被拒绝，纯文本无 scope 解析仍可用）。
- 授权属于账号设置数据：不写 `sync_changes`、不推进 revision；已随账号导出（`ai_consents`）；账号删除级联移除。
- 测试覆盖鉴权、初始空授权、幂等重放、scope 交集与拒绝、撤销即时生效、未知 scope、缺幂等键、跨 owner 隔离、删除账号，以及不写同步状态。

## 实现和部署

本切片无 schema 变更、无新迁移（`ai_consents` 自 DATA-01 迁移存在）。更新在同一事务内使用 owner advisory lock 与 upsert，幂等结果写入 `client_mutations`。真实模型调用的 scope 传递仍需 Provider 接入时按 effective scopes 实施。

## 边界与后续

- “仅本次”授权仍由请求内 `allowed_scopes` 表达，未持久化；撤销只影响长期授权。
- 各 AI 端点对任务数据的细粒度 scope 限制（如仅本周任务）与数据类别审计留待真实 Provider 接入切片。
