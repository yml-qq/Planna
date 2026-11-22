# 119｜TIME-01G-a：工作日覆盖 API（自定义节假日/调休）

状态：代码已实现；本地 build/format/契约 **35 项**通过。首次 CI Server 失败（连接未打开），修复 `4ff0f8f` 后 [Server integration 45014243194](https://github.com/yml-qq/Planna/actions/runs/45014243194) 与 [Desktop core 45014243201](https://github.com/yml-qq/Planna/actions/runs/45014243201) 均 **success**。`legal_workday` 规则与提醒重排属 `TIME-01G-b`。

## 范围与验收

- **表** `workday_overrides`：`(owner_id, jurisdiction, date)` 唯一；`kind ∈ {holiday, workday}`；`note ≤200`；`jurisdiction` 1..32；按 `(owner_id,date)` 索引；删除为物理删除（覆盖项本身是可重建的用户数据）。
- **契约**：
  - `GET /calendar/workday-overrides?from&to&jurisdiction=CN`：按日期升序；范围 ≤366 天；缺省 jurisdiction 归一化为 `CN`；非法范围 400。
  - `PUT /calendar/workday-overrides/{date}`：自然键 upsert（owner+jurisdiction+date），创建 201 / 替换 200；kind 非法或 note 超长 422；日期非法 400。
  - `DELETE /calendar/workday-overrides/{date}?jurisdiction=CN`：204；不存在 404（重复删除非幂等）。
- **隔离**：全部按 owner 作用域；不同 jurisdiction 互不影响；跨 owner 不返回数据。
- **OpenAPI/契约**：新增 3 个 schema 与 3 个操作；契约测试 **35 项**。
- **冒烟** `HttpWorkdayOverrideSmoke`：未认证 401、创建/替换/按 jurisdiction 列表、跨 owner 隔离、非法日期/kind/note/范围、删除与重复删除、数据库行确认。

## 明确不做/边界

- **不内置任何法定节假日数据**；`CN` 只是默认 jurisdiction 标识，不代表已提供国务院放假安排。真实日历需由可信来源导入（`TIME-01G-b` 或部署侧数据作业），避免伪造。
- 尚未把 `legal_workday` 用于 RRULE/occurrence 生成、提醒重排或日历 UI；这些在 `TIME-01G-b`。
- 无版本号/软删/审计；upsert 幂等但并发最后写入者生效（无 If-Match）。
- 前端无入口；仅 API。

## 后续

- `TIME-01G-b`：`legal_workday` 规则求值、occurrence/提醒重排、可信日历导入与 UI（含时区/DST 测试）。
