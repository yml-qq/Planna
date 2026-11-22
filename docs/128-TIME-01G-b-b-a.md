# 128｜TIME-01G-b-b-a：工作日覆盖批量导入 API

状态：代码已实现；本地 build/format/契约 **35 项**通过；[Server integration 45036660571](https://github.com/yml-qq/Planna/actions/runs/45036660571) 与 [Desktop core 45036660566](https://github.com/yml-qq/Planna/actions/runs/45036660566) 均 **success**。`legal_workday` RRULE 求值与提醒重排属 `TIME-01G-b-b-b`。

## 范围与验收

- **契约**：`POST /calendar/workday-overrides/import`（认证）：
  - body `{jurisdiction?, overrides:[{date, kind, note?}]}`，1..366 条；
  - 单请求内日期重复、非法日期/kind、note >200、空数组或超过 366 条 → 422 `INVALID_IMPORT`；
  - 自然键 `(owner, jurisdiction, date)` upsert，事务内全部成功；返回 `{imported, jurisdiction}`；
  - 重复导入同一批数据返回相同计数（幂等），不产生重复行。
- **实现**：`WorkdayOverrideService.ImportAsync`，事务 + `ON CONFLICT DO UPDATE`；无迁移（复用 `workday_overrides`）。
- **OpenAPI**：新路径 + `ImportWorkdayOverrideItem/ImportWorkdayOverridesRequest/ImportWorkdayOverridesResponse`；契约测试并入工作日覆盖用例（**35 项**）。
- **冒烟**：导入 2 条 → 200 imported=2；重复导入仍 2；列表 2 条；重复日期/空数组/非法 kind/367 条 → 422；跨 owner 仍隔离。

## 语义与边界

- **服务端仍不内置法定节假日数据**；导入内容由调用方提供（应为可信来源），本切片不校验数据授权/签名。
- 导入不做版本号/If-Match；同一日期并发导入最后写入者生效。
- 不做部分成功：任一条非法 → 整批 422，不写任何行。
- 未接入 `legal_workday` RRULE、occurrence 生成、提醒重排或前端导入 UI。

## 后续

- `TIME-01G-b-b-b`：`legal_workday` 求值语义、occurrence/提醒重排与受信日历来源/授权。
