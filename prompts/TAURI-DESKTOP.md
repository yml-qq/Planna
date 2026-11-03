# Prompt：Tauri 2 桌面客户端

任务：{例如 DESK-01，任务列表 + 离线快速添加}
先读 AGENTS.md、docs/03-UX-FLOWS.md、docs/07-OFFLINE-SYNC.md、docs/12-DESKTOP-TAURI.md、docs/22-DESIGN-SYSTEM.md 与 spec/openapi.yaml。

固定技术：Tauri 2、Vue 3/TypeScript/Vite/Pinia、Rust Commands + SQLite；Windows/macOS/Linux。不要使用 Electron；不要让 WebView 执行任意 SQL/命令，不在 localStorage 保存 token。

要求：独立设计 UI 状态、Rust application service、本地 SQLite migration、outbox、Typed invoke、task list + create/detail 的事件与测试；桌面快捷键/托盘可延后，但须保持模块接口可扩展。离线先写本地、页面立即刷新、后台同步、故障提示非阻塞；AI 卡片不直接改数据。

验收：tsc/vue typecheck、单元测试、Rust tests、桌面构建；无网络和重启任务不丢；最小窗口尺寸、键盘焦点与快捷操作通过测试。给出验证结果及三个操作系统未测试的风险。
