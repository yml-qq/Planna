# 13｜Android 原生客户端技术与交互设计

## 1. 固定技术栈

Kotlin、Jetpack Compose、Material 3、Navigation、ViewModel/StateFlow、Coroutines、Room(SQLite)、DataStore、WorkManager、Hilt（依赖注入）、Ktor Client 或 Retrofit/OkHttp（二选一，建议 Retrofit/OkHttp 对稳定 REST）。必须是原生 Android，不使用 Flutter/React Native/Tauri Mobile。

## 2. 推荐架构

```text
android/app/
  ui/          # Compose screens, navigation, state
  feature/task|calendar|ai|settings/
  domain/      # use cases, domain validation
  data/
    local/     # Room entities, DAO, migrations
    remote/    # generated/openapi DTOs, Retrofit
    repository/# offline-first repo, single source of truth
    sync/      # outbox, WorkManager orchestration
  platform/    # notifications, share sheet, widgets
  di/          # Hilt modules
```

建议单 Activity + Compose Navigation；UI 只订阅 Repository 暴露的 Flow，本地 Room 是主显示来源；用户动作先入本地事务并出站 outbox，不要求联网才成功。

## 3. 任务和日历 UI

首页四 Tab（今天、清单、日历、我的）+ FAB；可滑动完成/延期、长按多选；任务详情采用 bottom sheet 或独立页面，支持时间/提醒/优先级快捷编辑；屏幕旋转与平板双栏适配。DatePicker/TimePicker 需清晰区分“全天”和“具体时间”。

## 4. 原生系统集成

- **通知**：`NotificationManager`/notification channels；Android 13+ 申请 `POST_NOTIFICATIONS`；通知 action 支持完成/稍后，需要接收器调用本地 repo 并加入 outbox。
- **闹钟**：对需要准确触发的单次提醒评估 `AlarmManager` exact alarm 的资格/权限；多数可容忍延迟的后台同步用 WorkManager，不依赖 Doze 中保证精度。
- **小组件**：使用 Glance 或系统支持的 App Widget 方式实现今天任务/快速添加，需控制隐私锁屏内容。
- **快捷输入**：Share Sheet、App Shortcuts/动态快捷方式、桌面长按应用图标。
- **语音**：用户主动点击并授权后调用系统识别或合规语音服务；禁止未授权后台录音。
- **系统备份**：谨慎配置 allowBackup，对 token/加密数据排除；必要密钥使用 Android Keystore。

## 5. 离线与同步

本地 Room `TaskEntity/OutboxEntity/SyncStateEntity`；DAO `@Transaction` 原子写；`SyncWorker` 使用网络约束、指数退避；用户前台操作可立即同步；所有 pending mutation 的状态应可观察。Doze 与 OEM 电池管理可能延迟后台执行，重新进入前台必须补同步。推送到达触发主动拉取，但推送本身不是权威数据。

## 6. 安全

- Token 使用 Encrypted storage / Android Keystore 支持的安全实现，禁止明文 SharedPreferences。
- HTTPS，网络安全配置拒绝生产明文 HTTP；应用内 WebView 非必要不启用。
- PendingIntent 使用适当 immutable/mutable flags，通知 action 不可信输入做权限校验。
- 剪贴板和锁屏不自动泄漏敏感任务内容；截图/最近任务界面保护提供可配置选项。

## 7. 测试

- Unit：ViewModel 状态转换、use case 校验、RRULE 测试夹具。
- Instrumentation：Room migration、Compose UI、权限拒绝与通知交互。
- 集成：离线新建、断网重启、重新联网、两设备并发编辑/重复提醒。
- 设备矩阵：主流 Android 版本与 ROM、通知权限拒绝、勿扰/省电、折叠屏/平板/大字体；最低 SDK 与目标 SDK 上线时根据商店政策锁定。

## 8. 发布和监控

Google Play/AAB 及中国大陆渠道 APK 分发策略要区分；隐私政策、数据安全表单、权限理由、第三方 SDK 清单；崩溃/ANR、通知授权率和 sync backlog 监控；按渠道安全签名和逐步发布。

## 9. 完成标准

在飞行模式下完整 CRUD，系统杀进程后仍可找回 pending 任务；恢复联网后跨端只出现一条；提醒权限被拒时 App 不崩溃；完整大字体与 TalkBack 支持；AI 故障不影响基础功能。
