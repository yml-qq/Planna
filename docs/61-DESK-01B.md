# 61｜DESK-01B：桌面 Web UI 与本地桥接层

状态：代码已实现；本地 `pnpm test`（6 项）与 `pnpm build`（vue-tsc + vite）通过，桌面 CI 见 `VALIDATION.md` 的 DESK-01B 小节。Tauri 外壳与同步调度属 `DESK-01C`。

## 范围与验收

- `apps/desktop` 现为 Vue 3 + TypeScript + Vite 应用：
  - `src/api.ts`：`DesktopBridge` 边界（UI 只通过该接口访问本地数据）；`MemoryBridge` 供浏览器/单测使用（按 list 过滤、按标题排序、同 id 幂等、拒绝空/超长标题）；`TauriBridge` 通过 `invoke('list_tasks'|'create_task')` 绑定 Rust core（`src-tauri` 属下一片）；`createBridge()` 依据 `__TAURI_INTERNALS__` 选择实现。
  - `src/tasks.ts`：任务列表状态（`tasks/loading/error`、`refresh`、`add`），新增去重前置、错误映射为 `INVALID_TITLE/SAVE_FAILED/LOAD_FAILED`，不会因失败破坏既有列表。
  - `src/App.vue`：最小任务页（标题输入 + 添加按钮 + 任务列表 + 加载/错误/空状态，`role="alert"`、aria label、键盘可达）；`src/styles.css` 支持浅/深色与焦点可见样式（UX-01 的最小起点）。
- 测试（Vitest，6 项）：MemoryBridge 过滤排序与幂等/校验、store 刷新与新增顺序、空/超长标题不发请求、加载与保存失败不污染状态、重复 id 替换不重复。
- 构建：`vue-tsc -b` 严格类型检查（`strict`、`noUnusedLocals`、`noUncheckedIndexedAccess`）与 `vite build` 通过；`pnpm-workspace.yaml` 显式允许忽略 esbuild 安装脚本（二进制来自平台可选依赖），避免 CI 因构建脚本策略失败。
- CI：`.github/workflows/desktop.yml` 新增 `desktop-web-ui`（ubuntu + pnpm 11 + Node 24：`pnpm install --frozen-lockfile`、`pnpm test`、`pnpm build`），与既有 Rust core 作业并行。

## 实现和部署

依赖锁定于 `pnpm-lock.yaml`；`node_modules/`、`dist/` 已忽略。UI 无网络调用、无凭据；浏览器模式仅使用内存桥，不假装已同步。

## 边界与后续

- `DESK-01C`：Tauri 2 外壳（`src-tauri` commands 绑定 core Store）、登录/同步客户端、outbox 重试调度、系统通知与平台打包；需在具备 WebView2 的环境做真机冒烟。
- 本片未实现编辑/完成/删除、筛选、搜索与同步状态指示；这些在后续 UI 切片补齐。
