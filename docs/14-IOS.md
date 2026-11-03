# 14｜iOS 原生客户端技术与交互设计

## 1. 固定技术栈

Swift、SwiftUI、Foundation/Concurrency（async/await、actors）、URLSession、Keychain、UserNotifications、BackgroundTasks、App Intents/Widgets（后续）。本地数据选 SQLite：推荐评估 GRDB（Swift 原生 SQLite 包装库）以便显式掌控 SQL/outbox/迁移；也可评估 SwiftData，但复杂增量同步适配和版本控制需单独验证。移动端不使用 Tauri/Flutter/React Native。

## 2. 推荐模块结构

```text
ios/Planna/
  App/                  # App entry, Navigation, DI
  Features/Tasks/
  Features/Calendar/
  Features/AI/
  Features/Settings/
  Domain/               # models, validation, use cases
  Data/Local/           # SQLite migrations, repo, outbox
  Data/Remote/          # URLSession, typed API models
  Data/Sync/            # SyncCoordinator actor
  Platform/Notifications/
  Platform/Widgets/
  Shared/DesignSystem/
```

SwiftUI 是声明式原生 UI；不把数据写入放在 View 内，ViewModel/Observable state 通过 repository 读取，本地数据库是单一状态来源。

## 3. 关键视图

TabView（今天、清单、日历、我的），全局新建按钮采用平台原生视觉规范；TaskDetail 支持 sheet、DatePicker、提醒选择、子任务、标签；iPad 后期利用 NavigationSplitView 双栏；深色模式、动态字体、VoiceOver 支持。

## 4. 离线与后台

本地 SQLite 同一事务更新实体/outbox；`SyncCoordinator` actor 保证每账号同一时刻只有一个同步循环；前台/网络恢复触发 push+pull；BGTaskScheduler 可以请求后台处理机会，但不能当做固定同步间隔或精确提醒的承诺。客户端收到推送可以尝试增量拉取，最终以 API 数据为准。

## 5. 提醒与系统权限

- 使用 `UNUserNotificationCenter` 调度本地通知，先在用户实际开启提醒时解释权限；支持通知 category/action（完成/稍后）并处理重复点击幂等。
- iOS 能否展示通知受 Focus/通知摘要/用户设置控制；本地计划需管理系统 pending 数量上限（具体按 OS 文档验证），只维护有限近期提醒窗口。
- App 退出后不能靠普通 `Timer` 保证触发；由系统通知调度处理。
- APNs 作为远程同步/备选通知渠道，需兼顾权限、撤销 token 和设备会话。

## 6. iOS 安全与隐私

Keychain 保存 token/本地密钥；文件保护配置（File Protection）；最小权限策略；Sensitive data 在 App switcher/小组件中可隐藏；不要在 UserDefaults 记录密钥；网络严格 TLS/ATS，除非有审核通过的例外。云端 AI 发送任务内容前单独获得授权，上传内容可审查。

## 7. Apple 生态能力的合理顺序

V1.0：App、离线任务、提醒、深链。V1.1：WidgetKit 今日任务/快速打卡、App Intents 快速创建、Siri/快捷指令（能力受平台政策与版本限制）。V1.2：Calendar import/export 候选；不要未授权直接读取用户所有日历。

## 8. 测试与发布

Swift Testing/XCTest 单元、XCUITest UI、SQLite migration 与并发测试；不同 iPhone/iPad 尺寸、动态字体、VoiceOver、时区/DST、通知权限拒绝/仅临时授权、弱网和后台/进程杀死。App Store Connect、TestFlight、签名证书、隐私清单、订阅支付政策、账号删除入口。最低 iOS 版本根据目标用户、关键库版本与商店要求锁定。

## 9. 完成标准

无网络可增删改查；本地事务确保 outbox 不丢；网络恢复后重试不重复创建；锁屏通知不泄漏被保护字段；任何 AI 修改必须显示预览并由用户执行；无权限时基本待办仍完整可用。
