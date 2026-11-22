# 132｜NOTE-04b-2b-b-c-b-b-b-b-a：本地笔记搜索下推 SQLite

状态：代码已实现；本地 core `cargo test`（笔记 8 项）、前端 89 项/build 通过；[Server integration 45044198303](https://github.com/yml-qq/Planna/actions/runs/45044198303) 与 [Desktop core 45044198371](https://github.com/yml-qq/Planna/actions/runs/45044198371) 均 **success**。全文索引/附件/模板属后续切片。

## 范围与验收

- **Store**：新增 `notes_matching(query)`：
  - 未删除笔记中按标题/正文 `instr(lower(...), needle)` 不区分大小写子串匹配；
  - 空/空白查询等价于 `notes()`；
  - 结果仍按 `updated_at DESC, id`。
- **Tauri**：`notes_local(query?, task_id?)` 在 SQLite 侧搜索并过滤任务，再映射 DTO。
- **桥接**：Tauri 的 `noteList` 直接传 query/taskId 给命令，不再把全部笔记取回前端过滤（只保留 `limit` 截断）。
- **测试**：core 新增 1 项（标题/正文大小写、无匹配、空查询），笔记 **8 项**；前端 89 项保持通过。

## 语义与边界

- 子串搜索，非分词/排名/前缀索引；`lower()` 仅对 ASCII 大小写生效（中文不受影响）。
- 无 FTS5/全文索引；大数据量下仍会全表扫描（`deleted_at IS NULL` 过滤）。
- 搜索只覆盖本地 SQLite 笔记；服务端笔记经拉取进入本地后才可搜到。
- 空查询返回全部；limit 仍在前端截断。

## 后续

- 全文索引（FTS5）、附件/模板与任务详情内删除属后续切片。
