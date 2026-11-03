# Prompt：iOS Swift 原生客户端

任务：{例如 IOS-01，任务列表 + SQLite + 本地 outbox}
先读 AGENTS.md、docs/03-UX-FLOWS.md、docs/07-OFFLINE-SYNC.md、docs/10-RECURRENCE-NOTIFICATION.md、docs/14-IOS.md、spec/openapi.yaml。

固定 Swift + SwiftUI + SQLite（优先评估 GRDB）+ URLSession + Swift Concurrency + UserNotifications + Keychain。禁止 Flutter/React Native/Tauri Mobile。数据写入必须是 SQLite entity+outbox 原子事务；SwiftUI 通过 repository 状态显示，不直接执行网络保存。

实现 View/状态/UseCase/Repo/迁移/测试；若涉及本地通知，用 UNUserNotificationCenter 而不是 Timer/BGTask 保证精准提醒。不得声称 BGTaskScheduler 可按固定时间执行。所有 token 放 Keychain；AI 发送用户数据需明确授权。

请给出 Xcode build/test/XCUITest 实际结果；无 macOS runner 时标记未验证，不得虚构已编译。列出屏幕尺寸/VoiceOver/通知权限/DST/后台限制风险。
