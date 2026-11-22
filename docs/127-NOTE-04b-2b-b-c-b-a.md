# 127｜NOTE-04b-2b-b-c-b-a：任务详情展示关联笔记

状态：代码已实现；本地 `pnpm test`（89 项）与 `pnpm build` 通过；[Server integration 45034780647](https://github.com/yml-qq/Planna/actions/runs/45034780647) 与 [Desktop core 45034780590](https://github.com/yml-qq/Planna/actions/runs/45034780590) 均 **success**。附件/模板/全文搜索属 `NOTE-04b-2b-b-c-b-b`。

## 范围与验收

- 新增纯函数 `notesForTask(notes, taskId)`（按关联任务过滤，空/无匹配返回空）。
- 任务详情展开时，在专注历史下方展示关联笔记（标题/无标题占位 + 正文前 80 字符）；点击任务“详情”即可看到。
- 测试（前端 **89 项**，新增 1 项）：三条笔记中两条关联 `task-1`、无匹配任务返回空。

## 语义与边界

- 仅展示，不提供在任务详情内编辑/新建；笔记编辑仍在笔记卡片。
- 只展示**本地已加载**的笔记（本地 SQLite 列表），未额外拉取服务端；同步后 `syncNow` 刷新笔记。
- 长正文截断 80 字符（字符数，非字节），未做 Markdown 渲染。
- 任务被删/未同步时，本地笔记仍保留 `taskId`，但任务详情不可见。

## 后续

- `NOTE-04b-2b-b-c-b-b`：任务详情内新建/编辑笔记、附件/模板、全文搜索。
