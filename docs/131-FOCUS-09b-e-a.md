# 131｜FOCUS-09b-e-a：报表“另存为”对话框

状态：代码已实现；本地前端 89 项测试、build、shell `cargo check/clippy/fmt` 通过；[Server integration 45042414372](https://github.com/yml-qq/Planna/actions/runs/45042414372) 与 [Desktop core 45042414289](https://github.com/yml-qq/Planna/actions/runs/45042414289) 均 **success**。后台通知真机验证与独立详情页属 `FOCUS-09b-e-b`。

## 范围与验收

- **Tauri**：新增 `focus_report_save_as(app, filename, content)`：
  - 通过 `tauri-plugin-dialog` 的阻塞保存对话框选择路径；
  - 默认文件名经 `sanitize_report_filename` 处理（ASCII 安全、`.csv`、≤120）；
  - 用户取消返回 `null`（不是错误）；写入失败返回错误字符串。
- **插件/权限**：`Cargo.toml` 增加 `tauri-plugin-dialog = "2"`；builder 注册插件；capabilities 增加 `dialog:default`。
- **桥接/Store**：`saveFocusReportAs`（Memory 返回 `chosen/<name>`，Tauri 映射命令）；focus store `saveReportAs` 映射错误。
- **UI**：统计卡片新增“另存为…”，显示保存路径/已取消/失败。
- 测试：前端保持 **89 项**，`saveReportAs` 并入既有保存测试；无对话框真机自动化。

## 语义与边界

- 对话框为原生 OS 窗口，无法在 Vitest/CI 中自动点击验证；本切片只验证编译、命令注册与桥接逻辑。
- 仍保留 FOCUS-09b-d 的“导出 CSV”到下载目录（无对话框、同名覆盖）。
- 文件名净化仅 ASCII；取消不写文件；不做原子写入。
- 后台/系统通知真机投递仍未验证。

## 后续

- `FOCUS-09b-e-b`：后台通知真机验证、独立任务详情页与导出格式扩展（JSON/PDF）。
