# 115｜GOAL-03g-b：桌面批量排序接入

状态：代码已实现；本地 `pnpm test`（82 项）与 `pnpm build` 通过，CI 见 `VALIDATION.md` 的 GOAL-03g-b 小节。AI 重试/退避与删除审计属 `GOAL-03g-c`。

## 范围与验收

- **Tauri**：新增 `goal_reorder_milestones(base_url, access_token, goal_id, expected_version, milestone_ids)` → `PUT /goals/{id}/milestones/order`，返回更新后的 Goal。
- **桥接**：`goalReorderMilestones`（Memory 校验版本/集合后重排并递增版本；Tauri 命令映射）。
- **goal store**：`moveMilestone` 改为**单次批量调用**（交换相邻 id 后提交完整顺序），不再发两次独立 PATCH；成功后直接替换目标，不额外 refresh。
- 测试（前端 **82 项**）：原排序测试扩展断言 position 为 `[1,2,3]`、目标版本仅 +1（原子一次写入）。

## 语义与边界

- 批量接口为全量替换，store 总是提交当前全部里程碑 id；并发变化会得到 409/422，用户需刷新重试。
- 上移/下移仍只是相邻交换，拖拽/多选排序未实现。
- 版本递增会淘汰其他旧客户端的后续编辑（预期乐观并发）。

## 后续

- `GOAL-03g-c`：AI 建议重试/限流退避、目标删除审计与批量排序的冲突提示。
