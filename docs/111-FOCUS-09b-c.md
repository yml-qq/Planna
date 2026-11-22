# 111｜FOCUS-09b-c：专注统计报表复制（CSV）

状态：代码已实现；本地 `pnpm test`（78 项）与 `pnpm build` 通过，桌面 CI 见 `VALIDATION.md` 的 FOCUS-09b-c 小节。报表文件下载/后台通知属 `FOCUS-09b-d`。

## 范围与验收

- **报表工具**（`src/report.ts` 纯函数）：
  - `focusStatsCsv(stats)`：表头 `date,minutes,completed_sessions,actual_seconds`，按日行、CRLF、末尾换行；空统计仍返回合法表头；
  - `focusStatsSummary(stats)`：`from ~ to（time_zone）：N 分钟，完成 X 次，放弃 Y 次`。
- **UI**：统计卡片显示人类可读摘要；新增“复制 CSV”，通过 `navigator.clipboard` 复制当前窗口报表并显示状态；剪贴板不可用/失败有提示。
- 测试（前端 **78 项**，新增 3 项）：CSV 表头与两行数据、摘要文案、空报表。

## 语义与边界

- 仅复制到剪贴板，不触发文件下载（Tauri webview 的下载行为不确定，未做承诺）；服务端不生成报表文件。
- CSV 使用分钟四舍五入展示，`actual_seconds` 保留原值；无字段转义需求（日期/数字）。
- 摘要与 CSV 基于当前 7/30 天窗口与浏览器时区统计。

## 后续

- `FOCUS-09b-d`：报表文件导出（服务端或 Tauri 保存对话框）、后台/系统通知真机验证、独立任务详情页。
