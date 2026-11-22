# 116｜FOCUS-09b-d：专注报表导出到下载目录

状态：代码已实现；本地前端 `pnpm test`（84 项）、`pnpm build`、Rust `cargo test --lib`（3 项）通过；[Server integration 45005953461](https://github.com/yml-qq/Planna/actions/runs/45005953461) 与 [Desktop core 45005953541](https://github.com/yml-qq/Planna/actions/runs/45005953541) 均 **success**。后台通知真机验证与独立详情页属 `FOCUS-09b-e`。

## 范围与验收

- **Tauri**：`focus_report_save(app, filename, content)`：
  - 文件名经 `sanitize_report_filename`：只保留 ASCII 字母数字/`.`/`-`/`_`，剥离路径，空值回退 `focus-report`，自动补 `.csv`，长度 ≤120；
  - 解析系统下载目录，`create_dir_all` 后写入，返回绝对路径；错误以字符串返回前端。
- **桥接**：`saveFocusReport(filename, content)`（Memory 返回 `downloads/<name>`，空内容报 `EMPTY_REPORT`；Tauri 命令映射）。
- **UI**：统计卡片新增“导出 CSV”，使用 `planna-focus-<to>.csv` 文件名，成功显示保存路径，失败显示错误码。
- **测试**：前端新增 1 项（保存成功/失败映射）+ 报表文件名 1 项（合计 **84 项**）；Rust 新增 3 项文件名净化测试。

## 语义与边界

- 固定写入系统下载目录，不弹保存对话框，不覆盖用户选择；同一天重复导出会覆盖同名文件（无时间戳后缀）。
- 非原子写入（直接 `fs::write`）；极端中断可能留下半截文件。
- 文件名净化仅 ASCII，中文文件名会被剥离为回退名；未做路径穿越外的高级校验。
- 不改变服务端；通知真机验证仍未执行。

## 后续

- `FOCUS-09b-e`：保存对话框/时间戳防覆盖、后台通知真机验证、独立任务详情页。
