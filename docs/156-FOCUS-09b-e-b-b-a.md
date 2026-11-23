# 156｜FOCUS-09b-e-b-b-a：JSON 报表 Schema 与示例校验

状态：代码已实现；本地契约测试 **37 项**通过；[Server integration 45205996334](https://github.com/yml-qq/Planna/actions/runs/45205996334) 与 [Desktop core 45205996300](https://github.com/yml-qq/Planna/actions/runs/45205996300) 均 **success**。

## 范围与验收

- 新增 `spec/focus-report-v1.schema.json`（JSON Schema 2020-12）：
  - `schema_version` 固定 `planna-focus-report-v1`；`from/to` 为 date；`timeZone` 字符串；
  - 会话/秒数非负整数；`days[]` 每项 `date/completedSessions/actualSeconds`，`additionalProperties:false`。
- 新增 `examples/spec-01/focus-report.json` 合成样例。
- 契约测试 `test_focus_report_schema`：用 `Draft202012Validator` + FormatChecker 校验样例，并断言必填字段集合；契约测试 36→**37 项**。

## 语义与边界

- Schema 是**导出契约**，运行时 TypeScript/Rust 不加载它校验；客户端序列化仍靠类型。
- 只覆盖 v1；无版本迁移/兼容策略（后续版本需新 schema 文件）。
- 样例为合成数据，不含真实用户。
- 未覆盖 PDF/Excel 或后台通知真机验证。

## 后续

- `FOCUS-09b-e-b-b-b`：Schema 运行时校验/版本迁移、PDF/Excel 导出与后台通知真机验证。
