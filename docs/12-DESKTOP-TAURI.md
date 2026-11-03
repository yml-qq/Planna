# 12｜Tauri 2 桌面端详细技术设计（Windows/macOS/Linux）

## 1. 技术约束

**桌面使用 Tauri 2**；UI 采用 Vue 3 + TypeScript + Vite + Pinia + Vue Router；系统能力通过 Tauri Rust Commands 与官方插件访问。SQLite 持久化由 Rust service（sqlx sqlite）封装，避免 WebView 组件直接执行任意 SQL。桌面可复用 Rust 纯领域算法，但服务端 API 与客户端权限边界仍然分离。Tauri 基于系统 WebView，不等于 Electron，平台 WebView 行为需实机检查。

## 2. 项目目录

```text
desktop/
  package.json
  src/
    app/                 # router, providers, bootstrap
    features/tasks/      # pages/components/stores
    features/calendar/
    features/ai/
    features/settings/
    shared/components/
    shared/api/           # Tauri commands typed wrappers
    shared/design/
  src-tauri/
    Cargo.toml
    tauri.conf.json
    capabilities/         # 平台/窗口权限白名单
    src/
      main.rs
      commands/           # 命令薄层 + DTO
      local_db/           # migrations & repos
      sync/               # outbox, sync coordinator
      notifications/      # desktop local notifications
      shortcuts/          # hotkeys & conflict handling
```

## 3. UI 与交互规范

- 默认三栏布局：左导航 240px、任务列表自适应、中/右详情可折叠；小窗口采用两栏或单栏。
- 顶部快捷添加、命令面板 (`Ctrl/Cmd+K`)、快捷创建 (`Ctrl/Cmd+N`)、任务完成/撤销键；系统托盘快捷创建/显示今天。
- 最小化与关闭区别明确：关闭窗口是否继续后台运行可配置，默认应尊重系统习惯与用户选择。
- 系统托盘菜单不能过度干扰，含打开主窗口/快速添加/退出。
- macOS 菜单栏、Windows taskbar、Linux tray 在不同桌面环境可用性不同，不保证所有发行版相同。

## 4. 数据与线程模型

Vue 页面 → Typed Command → Rust application service → SQLite TX → UI store 接收变更通知。`invoke` 的参数必须做服务端（Rust 进程内）验证；不要把数据库连接或原始查询接口暴露给渲染进程。网络同步 Rust 层负责，遵循通用 sync 协议；网络中断、关闭窗口、升级时 outbox 持久化。

## 5. Tauri 权限安全

- `capabilities` 显式给各窗口最小权限；不要使用泛化全部文件访问权限。
- 禁止启用非必要 shell 执行；URL opener 仅允许可信协议/域名；关闭非必要 deep link；桌面更新包要签名和校验。
- WebView 中 HTML/Markdown 渲染必须防 XSS，不对用户输入执行脚本；CSP 设置限制外部资源与 connect-src。
- 加密密钥保存在系统安全凭据库，登录 token 不放 localStorage；来自远端的 AI 内容当不可信文本渲染。

## 6. 本地数据

`src-tauri` 提供 task/list/get/mutate/sync/notification 的有限命令；数据库 migration 随 App 版本升级，先备份关键元数据后迁移。必要时使用 Tauri 官方 SQL 插件，但必须限制 SQL 操作对渲染层的暴露范围，优先 Rust 封装仓储 API。

## 7. 多窗口和快捷捕获

V1.0 仅主窗口和可选迷你快捷添加窗口；快捷窗口可从托盘/自定义全局快捷键呼出；Windows/macOS/Linux 热键权限和占用情况不同，显示注册成功/冲突提示。多窗口共享同一个 Rust state/SQLite 事务协调器，避免双窗口同时写错。

## 8. 通知/更新

Tauri notification plugin 在应用运行时使用系统通知；退出后后台提醒需单独平台服务，不默认承诺。自动更新通过受信发布源、签名、分阶段推送；更新失败能回滚到上一可用版本或至少不损坏本地数据库。

## 9. 打包和发布

- Windows：MSI/NSIS 视安装和自动更新策略决定；代码签名和 SmartScreen 声誉需规划。
- macOS：签名 + notarization，兼容 Apple Silicon 与 Intel 的发布矩阵按用户量选择。
- Linux：AppImage/deb/rpm 视支持范围定，先支持 Ubuntu LTS 类发行版；WebKitGTK 依赖与系统托盘兼容性测试。
- CI：平台矩阵构建、签名凭据隔离、发布产物 SBOM/hash，更新源加签。

## 10. 桌面验收

离线创建/重启任务不丢；托盘行为符合设置；快捷键冲突可处理；跨屏缩放/4K/字体放大正常；自动更新不丢 SQLite；窗口在 1024×720 起仍可完整操作；AI 不可用时主任务功能完全正常。
