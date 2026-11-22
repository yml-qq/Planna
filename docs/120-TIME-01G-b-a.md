# 120｜TIME-01G-b-a：工作日判定 API

状态：代码已实现；本地 build/format/契约 **35 项**通过。CI 结果与推送状态见 `VALIDATION.md`。occurrence/提醒重排、可信日历导入与 UI 属 `TIME-01G-b-b`。

## 范围与验收

- **纯函数** `WorkdayCalendar`：
  - 覆盖优先：`holiday` → 非工作日，`workday` → 工作日（调休）；
  - 无覆盖时周一至周五为工作日、周六周日为休息日；
  - `CountWorkdays`、`AddWorkdays`（向后找第 N 个工作日，5 年上限防死循环）。
- **契约** `GET /calendar/workdays?from&to&jurisdiction=CN`：
  - 返回 `{from,to,jurisdiction,days:[{date,kind}]}`，按日期升序；
  - 与覆盖列表相同：owner 作用域、默认 CN、范围 ≤366 天、非法范围 400。
- **OpenAPI/契约**：`WorkdayDays`/`WorkdayDay` schema 与操作；契约测试（**35 项**，断言并入工作日覆盖用例）。
- **冒烟**：在 `HttpWorkdayOverrideSmoke` 中插入 10/01 `workday` 覆盖，调用 `/calendar/workdays` 断言 9/28 工作日、10/01 调休工作日、10/03 周六休息；并直接断言纯函数（周六调休、周一放假、跨周末 `AddWorkdays`）。

## 语义与边界

- **仍不内置法定节假日数据**：只有 owner 显式覆盖 + 周末规则，因此默认结果不等于中国法定日历。
- 未接入 RRULE `legal_workday`、occurrence 生成或提醒重排；未做时区/DST（有日期无时间语义）。
- 覆盖按 jurisdiction 隔离；同 owner 不同 jurisdiction 的覆盖互不影响。
- 5 年上限只用于 `AddWorkdays`，不是业务保证。

## 后续

- `TIME-01G-b-b`：`legal_workday` RRULE 求值、occurrence/提醒重排、可信日历导入与前端日历 UI。
