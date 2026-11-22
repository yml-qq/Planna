# 144｜TIME-01G-b-b-b-a：工作日历客户端与月视图

状态：代码已实现；本地前端 `pnpm test`（96 项）、`pnpm build`、shell `cargo check/clippy/fmt` 通过；[Server integration 45072311127](https://github.com/yml-qq/Planna/actions/runs/45072311127) 与 [Desktop core 45072311246](https://github.com/yml-qq/Planna/actions/runs/45072311246) 均 **success**。法定节假日数据导入/提醒重排仍属 `TIME-01G-b-b-b-b`。

## 范围与验收

- **Tauri 命令**：`workday_days`（GET `/calendar/workdays`）、`workday_override_set`（PUT 覆盖）、`workday_override_clear`（DELETE 覆盖）。
- **桥接**：`WorkdayDay` 类型与 3 个方法；Memory 以工作日/周末默认 + 覆盖 map 模拟。
- **calendar store**：`monthRange`/`monthLeadingBlanks` 纯函数；`year/month/days/loading/error`；`load`、`previousMonth`、`nextMonth`、`toggle`（工作日↔休息日）、`clear`。
- **UI**：新增“工作日历”卡片：上一月/下一月、周一开头 7 列网格、工作日/休息日样式；点击日期切换覆盖；错误显示。
- 测试：前端 92→**96 项**（月边界/闰年/前导空白、加载/切换/清除、跨年导航、未登录）。

## 语义与边界

- 默认 `CN` 且**不内置法定节假日数据**；没有导入真实日历前，月视图只反映“周一至周五 + 用户覆盖”，不是法定日历。
- 日历卡片**未纳入模块可见性设置**（UX-02 的模块列表）；始终在登录后显示。
- 点击是“切换覆盖”，不是三态选择；不能直接在 UI 输入备注，清除走 store 方法（UI 暂未暴露“清除”按钮）。
- 网格用 `role=grid` 但子项是 button/span 而非 `gridcell`；axe 未覆盖此组件。
- 无时区/DST 语义（纯日期）；无批量导入 UI。

## 后续

- `TIME-01G-b-b-b-b`：受信日历导入 UI、legal_workday RRULE 求值、提醒/occurrence 重排与工作日历模块可见性。
