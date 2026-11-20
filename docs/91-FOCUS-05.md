# 91｜FOCUS-05：专注统计 UI 与完成休息提示

状态：代码已实现；本地 `pnpm test`（54 项）、`pnpm build`、shell `cargo check/clippy/fmt` 通过；[Server integration 44732461769](https://github.com/yml-qq/Planna/actions/runs/44732461769) 与 [Desktop core 44732461757](https://github.com/yml-qq/Planna/actions/runs/44732461757) 均 **success**。休息通知真机投递与任务详情联动属 `FOCUS-06`。

## 范围与验收

- **Tauri 命令**：`focus_stats(base_url, access_token, from, to, time_zone)` → 服务端 `GET /focus-sessions/stats`；`time_zone` 经 `urlencoding` 编码，错误仍映射 `HTTP状态:错误码`。
- **桥接**：`FocusStats/FocusDayStat` 类型与 `focusStats` 方法（Memory 按 `startedAt` UTC 日期聚合会话、Tauri 命令 + snake_case 映射）。
- **focus store**：新增 `stats`、`restHint` 与 `loadStats(from,to,timeZone)`；完成专注后设置“本次专注 X，建议休息 5 分钟”提示，开始新会话时清除。
- **UI**：专注卡片底部新增“近 7 天专注”（总会话/完成次数、按日分钟列表），完成专注后刷新统计并展示休息提示；登录与恢复会话后加载最近 7 天统计。
- 测试（前端 **54 项**，新增 2 项）：完成会话后按日聚合（1 次/600 秒）、未登录加载统计报 `FOCUS_SIGNED_OUT`。

## 语义与边界

- 统计窗口为浏览器本地时区的最近 7 天（含今天），与服务端 `time_zone` 分组一致；未结束会话不计 actual_seconds（服务端规则）。
- 休息提示仅为应用内文本，不调用系统通知；真机通知与免打扰联动留 `FOCUS-06`。
- 统计每次进入/完成后请求一次，不做缓存或后台轮询。

## 后续

- `FOCUS-06`：休息倒计时/系统通知、任务详情页专注入口、周/月统计视图。
