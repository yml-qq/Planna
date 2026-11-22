# 117｜GOAL-03g-c：AI 拆解重试与限流退避

状态：代码已实现；本地 `pnpm test`（85 项）与 `pnpm build` 通过，桌面 CI 见 `VALIDATION.md` 的 GOAL-03g-c 小节。目标删除审计/冲突提示属 `GOAL-03g-d`。

## 范围与验收

- goal store 新增 `aiRetryAt` 与 `retryPreview`：
  - `RATE_LIMITED` → 冷却 30s；`AI_UNAVAILABLE` → 冷却 10s；其他错误不冷却；
  - 冷却期内 `retryPreview` 直接返回 `false` 且不发请求；到期后重新调用预览，成功后清空冷却并清错误；
  - 冷却时间为客户端单实例全局（不按目标区分）。
- UI：AI 错误时显示“重试（Ns）”按钮，按秒禁用/倒计时；成功后消失；`nowTick` 每秒刷新，卸载时清理定时器。
- 测试（前端 **85 项**，新增 1 项）：首次 429 → `aiRetryAt=now+30000`，冷却内重试不发请求，推进 30s 后重试成功并清空错误/冷却。

## 语义与边界

- 冷却只在内存/当前窗口有效，刷新/重启后丢失；服务端 429 仍是最终限流方。
- 固定退避（10s/30s），非指数退避、无抖动；`AI_UNAVAILABLE` 与 `RATE_LIMITED` 之外的错误不自动重试。
- 未做请求取消/并发预览去重；快速重复点击由 `breakdownLoading` 禁用。

## 后续

- `GOAL-03g-d`：目标删除审计/冲突提示、预览并发去重与退避抖动。
