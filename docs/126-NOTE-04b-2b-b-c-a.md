# 126｜NOTE-04b-2b-b-c-a：笔记冲突“以服务端为准”落库

状态：代码已实现；本地 core 笔记 **7 项**测试与前端 88 项/build 通过。CI 结果见 `VALIDATION.md`。附件/模板/全文搜索属 `NOTE-04b-2b-b-c-b`。

## 范围与验收

- **冲突快照**：`Store::force_apply_snapshot` 新增 `note` 分支：解码服务端 note 视图并 upsert（`created_at` 缺失时回退 `updated_at`），保留版本/删除标记；修复此前 note 冲突点“以服务端为准”会返回 `unsupported snapshot entity` 的问题。
- **UI 刷新**：冲突解决与丢弃 mutation 后同时刷新笔记列表，避免本地 UI 与 SQLite 不一致。
- **测试**（新增 1 项，笔记合计 7 项）：空库应用 note 快照（v3）→ 再应用 v4 替换，标题/版本正确。

## 语义与边界

- 只覆盖“以服务端为准”；“重试/覆盖”沿用现有 outbox 逻辑（重试重置 attempted，覆盖重写快照），note 无需特殊分支。
- 冲突列表仍是通用实体列表（显示 entityType/版本），未做笔记正文 diff 预览。
- 附件/模板/全文搜索未实现。

## 后续

- `NOTE-04b-2b-b-c-b`：笔记冲突 diff、附件/模板、全文搜索与任务详情内笔记。
