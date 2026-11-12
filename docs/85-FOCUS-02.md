# 85｜FOCUS-02：桌面专注计时与恢复

状态：代码已实现；本地 `pnpm test`（44 项）、`pnpm build`、shell `cargo check/clippy/fmt` 通过；[Server integration 43177456786](https://github.com/yml-qq/Planna/actions/runs/43177456786) 与 [Desktop core 43177456889](https://github.com/yml-qq/Planna/actions/runs/43177456889) 均 **success**。空闲漂移修正与任务关联 UI 属 `FOCUS-03`。

## 范围与验收

- **Tauri 命令**：`focus_list(base_url, access_token, limit)`、`focus_start(base_url, access_token, task_id, planned_seconds)`、`focus_update(base_url, access_token, session_id, expected_version, state, actual_seconds, break_seconds, ended_at)`；使用 `reqwest` bearer、`Idempotency-Key` 与 `If-Match: "vN"`，错误映射为 `HTTP状态:错误码`（如 `409:FOCUS_SESSION_ACTIVE`）。
- **桥接**：`focusList/focusStart/focusUpdate`（Memory 内存状态机 + Tauri 命令）与 `FocusSession/FocusUpdate` 类型。
- **focus store**：
  - `formatDuration`、`elapsedSeconds`（仅 active 累计）、`loadSnapshot` 防御式解析；
  - `start/pause/resume/complete/abandon`：暂停时提交累计 actual、继续时以 actual 为基线、完成时提交最终 actual 与 ended_at；
  - 本地快照 `planna.focus`（sessionId/version/state/accumulated/resumedAt），`recover()` 拉取服务端未结束会话：快照 id+version 匹配则沿用，否则以服务端 `actual_seconds` 为基线（active 从当前时刻继续计时）；
  - ticker 每秒刷新显示；错误码映射为 UI 文案。
- **UI**：登录后“专注”卡片——无会话时“开始 25/50 分钟”，有会话时显示 `mm:ss`/`h:mm:ss`、计划时长、进行中/已暂停与暂停/继续/完成按钮；登录成功与恢复会话后自动 `recover()`。
- 测试（前端 **44 项**，新增 7 项）：时长格式、elapsed 计算、快照解析、开始→暂停→继续→完成（版本 4）、快照恢复、无快照回退、开放会话冲突与未登录。

## 语义与边界（最小规格修正）

- 服务端 `actual_seconds` 只在状态迁移时提交，不按秒持续心跳；因此**应用崩溃后 active 会话的显示进度可能低于真实经过时间**。恢复以服务端记录为准，用户可暂停后以当时显示值完成（修正 UI 留 `FOCUS-03`）。
- 专注为在线操作，不进入 outbox/离线队列；断网时开始/迁移失败并在 UI 提示。
- 任务关联字段已透传（当前 UI 固定 null）。

## 后续

- `FOCUS-03`：空闲/崩溃时长校正、任务详情联动、专注统计与后台休眠处理。
