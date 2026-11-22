# 114｜GOAL-03g-a：里程碑批量排序 API（原子）

状态：代码已实现；本地 build/format/契约 **34 项**通过，CI 见 `VALIDATION.md` 的 GOAL-03g-a 小节。桌面改用该接口与 AI 重试/退避属 `GOAL-03g-b`。

## 范围与验收

- **契约**：`PUT /goals/{id}/milestones/order`（`Idempotency-Key`）：
  - body `{expected_version, milestone_ids[]}`：必须**恰好**包含该目标全部未删除里程碑且每个一次（minItems=1、uniqueItems）；
  - 在一个事务内按数组顺序设置 `position=1..N`，每个里程碑 `version+1`，目标 `version+1`；返回更新后的 `Goal`（含里程碑）；
  - 目标版本过期 → 409 `VERSION_CONFLICT`；缺项/多项/重复/未知 id → 422 `INVALID_MILESTONE_ORDER`；跨 owner/不存在 → 404 `GOAL_NOT_FOUND`；重复 Idempotency-Key → 200 回放；
  - 空 id/缺 key → 400；未认证 → 401。
- **实现**：`IGoalService.ReorderMilestonesAsync`；`LoadMilestoneRowsAsync` 读取未删除行；复用 owner advisory lock、mutation 台账与软删语义。
- **OpenAPI/契约**：新增路径、`ReorderMilestonesRequest` schema（expected_version、milestone_ids minItems/uniqueItems）、409/422 响应；契约测试断言（**34 项**）。
- **冒烟** `HttpGoalOrderSmoke`：未认证 401、重排成功（顺序/版本/目标版本 2）、同 key 回放、旧版本 409、重复/缺项/未知 id 422、跨 owner/不存在 404、缺 key 400。

## 语义与边界

- 排序是**全量替换**：客户端必须提交当前全部未删除里程碑，不能只提交移动的项；并发新增/删除会让旧集合 422，需要刷新后重试。
- 目标版本递增使旧客户端后续编辑得到 409（乐观并发）；不写 `sync_changes`（目标当前不参与同步）。
- 回放命中时直接返回已存响应，不再校验当前里程碑集合。

## 后续

- `GOAL-03g-b`：桌面“上移/下移”改为调用批量接口、AI 建议重试/限流退避、删除审计。
