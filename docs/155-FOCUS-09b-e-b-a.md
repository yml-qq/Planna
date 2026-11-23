# 155｜FOCUS-09b-e-b-a：JSON 报表导出

状态：代码已实现；本地前端 `pnpm test`（102 项）、`pnpm build`、Rust `cargo test --lib`（3 项）/clippy 通过；[Server integration 45200880021](https://github.com/yml-qq/Planna/actions/runs/45200880021) 与 [Desktop core 45200879856](https://github.com/yml-qq/Planna/actions/runs/45200879856) 均 **success**。

## 范围与验收

- **JSON 序列化** `focusStatsJson`：`{schema_version:"planna-focus-report-v1", ...FocusStats}`，缩进 2 空格。
- **文件名**：`focusReportFilename(stats, 'csv'|'json')` 生成 `.csv`/`.json`。
- **UI**：统计卡片新增“导出 JSON”（写入下载目录）并显示路径/错误；保留 CSV 复制/导出/另存为。
- **Tauri**：`sanitize_report_filename` 接受 `.csv` 或 `.json`（其他扩展名仍补 `.csv`）；另存为对话框增加 JSON 过滤器。
- 测试：前端 101→**102 项**（JSON 文件名/序列化）；Rust 3 项仍通过（CSV/JSON 文件名保留）。

## 语义与边界

- JSON 结构直接映射 `FocusStats`，**没有独立 JSON Schema 文件**；`schema_version` 仅供客户端版本识别。
- 固定写入下载目录、同日覆盖；另存为对话框仍需用户选择；无 PDF/Excel。
- 无流式写入（统计窗口数据量小）；UTF-8。
- JSON 不含用户/设备标识，仅聚合统计与日期。

## 后续

- `FOCUS-09b-e-b-b`：导出 JSON Schema、PDF/Excel、文件时间戳防覆盖与导出历史。
