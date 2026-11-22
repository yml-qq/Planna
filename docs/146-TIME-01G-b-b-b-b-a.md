# 146｜TIME-01G-b-b-b-b-a：工作日历批量导入 UI

状态：代码已实现；本地前端 `pnpm test`（98 项）、`pnpm build`、shell `cargo check/clippy/fmt` 通过；[Server integration 45076372346](https://github.com/yml-qq/Planna/actions/runs/45076372346) 与 [Desktop core 45076372291](https://github.com/yml-qq/Planna/actions/runs/45076372291) 均 **success**。legal_workday 求值与提醒重排仍属 `TIME-01G-b-b-b-b-b`。

## 范围与验收

- **Tauri 命令** `workday_import` → `POST /calendar/workday-overrides/import`（items 数组 + jurisdiction）。
- **桥接** `workdayImport`（Memory 按日期写入覆盖 map）。
- **calendar store**：
  - `parseOverrideLines(text)`：每行 `YYYY-MM-DD,holiday|workday[,备注]`；忽略空行与 `#` 注释；非法日期/kind/重复日期计入 errors；
  - `importLines(text)`：解析→调用桥接→重新加载当月；返回 `{imported,errors}`；0 条时不发请求；未登录返回 null 并置错误。
- **UI**：日历卡片新增“批量导入”展开 textarea + “导入”，显示“导入 N 条，忽略 M 条”；成功后清空输入。
- 测试 96→**98 项**（解析器合法/备注/注释/非法/重复；store 导入并刷新 10/01、10/03）。

## 语义与边界

- 导入仅接受**粘贴文本**，不支持文件选择/拖拽；无预览确认步骤。
- 客户端不强制 366 条上限，服务端会拒绝；整批原子（任一服务端校验失败则全批 422）。
- 重复日期在客户端计为错误并跳过，而不是“后者覆盖前者”。
- 不内置法定数据；导入内容由用户/部署方负责来源可信；无 note 字段长度校验（服务端 200）。
- 导入不触发 occurrence/提醒重排（`TIME-01G-b-b-b-b-b`）。

## 后续

- `TIME-01G-b-b-b-b-b`：受信来源标记、文件导入、legal_workday RRULE 求值与提醒/occurrence 重排。
