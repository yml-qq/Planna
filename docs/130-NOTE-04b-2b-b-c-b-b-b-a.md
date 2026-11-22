# 130｜NOTE-04b-2b-b-c-b-b-b-a：任务详情内编辑关联笔记

状态：代码已实现；本地 `pnpm test`（89 项）与 `pnpm build` 通过；[Server integration 45040623373](https://github.com/yml-qq/Planna/actions/runs/45040623373) 与 [Desktop core 45040623384](https://github.com/yml-qq/Planna/actions/runs/45040623384) 均 **success**。附件/模板/全文搜索属 `NOTE-04b-2b-b-c-b-b-b-b`。

## 范围与验收

- 任务详情关联笔记每条新增“编辑”：切换为内联表单（标题输入 + 正文 textarea + 保存/取消）。
- 保存调用 `notes.update(note.id, note.version, {title, content})`：沿用已有校验（标题 ≤200/正文 ≤20000/不能同时为空）与版本守卫；成功后关闭编辑并刷新列表。
- 取消清空临时字段，不写本地。
- 测试：前端 **89 项**保持通过（notes store 更新/版本冲突已有测试覆盖）。

## 语义与边界

- 仅标题/正文可编辑；关联任务、创建时间不可改；无删除按钮（删除仍在笔记卡片）。
- 编辑的是本地笔记；未同步前不触网，之后由 sync 推送。
- 无草稿保护：取消即丢弃输入；无并发编辑提示（版本冲突显示现有错误文案）。
- 附件/模板/全文搜索未实现。

## 后续

- `NOTE-04b-2b-b-c-b-b-b-b`：任务详情内删除笔记、附件/模板与全文搜索。
