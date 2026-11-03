# 07｜离线优先与跨端同步协议（重要）

## 1. 目标和范围

三端均离线可读取、创建、修改、完成/删除任务；之后双向同步，确保数据不丢、重复操作幂等、可检测冲突。个人任务不需要多人文本 CRDT，优先成熟的版本式增量同步。服务端负责最终权威版本，客户端本地数据库是 UI 唯一读取来源。

## 2. 本地数据库最小模式

`local_tasks`（server_version、local_revision、dirty 标记、deleted_at、显示字段）、`local_lists`、`local_tags`、`outbox`、`sync_state`（cursor,last_sync_at,bootstrap_epoch）、`conflicts`、`reminder_queue`。outbox 包括 `mutation_id,entity_id,entity_type,op,base_version,payload,created_at,attempt_count,next_retry_at,state`。

同一 UI 操作必须用一笔 SQLite 事务同时更新本地实体和加入 outbox，避免任务可见但出站操作丢失。禁止 UI 在 API success 后才保存本地数据。

## 3. 上传算法（伪代码）

```text
on user mutation:
  BEGIN LOCAL TX
    validate fields & local access
    apply to local entity (optimistic)
    append outbox(mutation_id UUID, base_version, payload)
  COMMIT
  schedule sync (when network is usable)

push worker:
  ensure one sync worker per account/device
  load next eligible mutations ordered by dependency/time
  POST /sync/push (up to N, bounded payload)
  for each result:
    accepted/duplicate => update local server_version; remove outbox
    conflict => mark conflict; do not discard local draft
    rejected => mark nonretryable + actionable error
    5xx/network => exponential backoff + jitter
```

依赖顺序：先 list 再新建 task，再 steps/reminders；删除时先处理依赖关系。上传的 `base_version` 是编辑时见到的服务端版本，不能在多次本地未上传编辑中递增伪造服务端版本；可合并尚未发送且无外部引用的本地同实体 patch。

## 4. 下载算法

1. 使用持久化 `cursor` 请求 `/sync/pull`；服务端按 owner+seq 稳定排序。
2. 开启本地事务：对每条 change 检查未提交本地修改；对纯净实体应用新快照/墓碑；对 dirty 实体执行三方差异检查，无法自动合并则放 conflict 队列。
3. 同一事务保存 `next_cursor`；失败时整个批次回滚，重试不会丢事件。
4. `has_more` 继续直到追平，避免一次性在主线程处理全部。
5. pull 回来后只更新影响的近期通知调度。

切勿先更新 cursor 再写本地数据；否则崩溃造成永久跳过事件。

## 5. 幂等与顺序

- 客户端 mutation ID 稳定、重试不改变；服务端为 `(owner_id,mutation_id)` 建唯一约束。
- 重复收到同 mutation_id + 同 request_hash 返回首次响应；若 hash 不同返回 `IDEMPOTENCY_MISMATCH`。
- 数据库可存在全局诊断序列，但实际同步使用每账户已提交的 revision；事务提交顺序与裸序列分配顺序可能不同，**不能天真直接用 `BIGSERIAL seq` 代替一致提交水位**。
- 实施可选策略：按账户行级锁维护 per-user monotonic committed revision（事务内更新用户 sync version + change rows，保证同用户提交序），或采用事务一致的 CDC/日志机制。**推荐首发使用每用户 `sync_revision` 串行化提交**，避免 seq 提前分配、后提交造成漏 pull。
- pull cursor 指向该用户已提交 revision，不暴露顺序漏洞；多变更同一事务可加 `change_index` 形成 `(revision,index)` 稳定顺序。

## 6. 冲突判定与解决

| 场景 | 规则 |
|---|---|
| A 改标题、B 改优先级 | 基于相同 base 和 field mask 可安全合并，否则冲突 |
| A/B 同时改标题 | conflict UI 选择保留本地/远端/复制为新任务 |
| 远端删除、本地编辑 | 不复活删除；保留本地临时副本供用户手动恢复 |
| 本地删除、远端修改 | 弹冲突；明确删除优先策略或用户决策，不静默覆盖 |
| 完成同一任务 | 相同完成 mutation 可去重；不同操作按版本检查 |
| 同一提醒多次触发 | 用 delivery_key 去重，UI 动作幂等 |
| 重复任务实例 | `series_id + occurrence_key` 唯一，完成次数去重 |

禁止默认用本机时间 last-write-wins 覆盖标题/笔记。冲突 UI 应显示变更设备、时间（供参考）和字段差异；用户决定后创建新的 mutation。

## 7. 首次同步、游客账户合并与重建

首次登录 → `/sync/bootstrap` 下载带快照版本的一致视图 → 导入本地或开启本地访客合并向导；同名任务不自动等同；可以检测可能重复并提示。下载中断必须可重新开始，不能出现一半替换的工作区。账号登出时保留/擦除本地数据由用户选择与隐私策略决定，默认保护其他账号的隔离。

账号删除成功后，所有会话立即撤销；旧设备恢复连接时得到不可重试的 `ACCOUNT_DELETED`，停止原账号 push/pull，保留本地 SQLite 供用户选择离线导出或清除。客户端不能自动创建新账号重放旧 outbox，也不能静默把旧账号数据合并到新账号。纯本地导出标记未同步 mutation，云端导出只包含 `snapshot_at` 前已提交的数据。

cursor 过期 → 服务端返回 410 → 先导出当前 dirty/outbox 备份 → 建立新快照 → 与未同步本地改动三方对账 → 恢复待提交操作。绝不直接把本地库覆盖。

## 8. 各端后台行为约束

- Android：WorkManager 适于可延迟的可靠同步；立即同步在前台使用 coroutine；通知精确定时与 sync worker 职责分开。
- iOS：URLSession/BGTask 仅机会式运行，不保证频率；前台启动、切换前台、静默推送等可触发补充同步，离线操作不依赖后台任务。
- Tauri：前台和系统恢复连接时执行同步；系统托盘不代表永不退出；关闭/崩溃也应保留 outbox。

## 9. 性能与诊断

- 小批次（例如 50–200 条）+ 请求压缩 + cursor 分页。
- 指标：outbox backlog、last_success_sync_age、conflict rate、pull apply errors、cursor expired rate。
- UI 诊断：最近成功同步时间、待上传数、失败原因/重试、导出诊断（不含个人明文）。

## 10. 必测故障注入

- 创建后立刻杀进程；上传成功但 ack 丢失；服务器 500 与 429；重放两次 mutation；A/B 改同字段；离线删除 vs 远端修改；10 万历史变更 cursor；服务器事务慢提交；设备时间错误；旧 App 版本；登录切换账户；恢复快照过程中断网。

## 11. 验收门槛

同设备同操作重放 100 次只生成一条业务记录；同步 cursor 绝不跳过已提交事件；任何冲突都不自动丢失本地用户编辑；即使 AI 不可用，也能完成离线任务 CRUD 和同步。
