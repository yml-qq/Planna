# 110｜FOCUS-09b-b：桌面专注历史分页消费

状态：代码已实现；本地 `pnpm test`（75 项）与 `pnpm build` 通过；[Server integration 44996532155](https://github.com/yml-qq/Planna/actions/runs/44996532155) 与 [Desktop core 44996532135](https://github.com/yml-qq/Planna/actions/runs/44996532135) 均 **success**。

## 范围与验收

- **Tauri**：新增 `focus_page(base_url, access_token, limit, task_id, cursor)` → `{items, next_cursor}`，复用 `/focus-sessions` 的游标分页。
- **桥接**：`FocusPage` 类型与 `focusPage`（Memory 用数字偏移模拟不透明游标；Tauri 映射命令），与既有 `focusList` 并存。
- **focus store**：`loadTaskHistory` 改用 `focusPage` 并记录 `taskCursors`；新增 `loadMoreTaskHistory` 追加下一页并更新 cursor。
- **UI**：任务详情在存在 `next_cursor` 时显示“加载更多”，点击追加记录；空页后按钮消失。
- 测试（前端 **75 项**，新增 1 项）：3 条任务会话 limit=2 → 首页 2 条 + cursor `"2"`，加载更多后 3 条且 cursor 为 null。

## 语义与边界

- Memory 的 cursor 为数字偏移，仅测试/浏览器用；真实游标由服务端 base64url 生成，客户端不解析。
- 分页为 keyset；分页期间新增会话可能出现在后续页之前（服务端记录）。
- 历史仍无缓存/离线；报表导出与后台通知真机验证属 `FOCUS-09b-c`。

## 后续

- `FOCUS-09b-c`：周/月报表导出、独立任务详情页、后台通知真机验证。
