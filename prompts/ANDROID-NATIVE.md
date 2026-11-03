# Prompt：Android Kotlin 原生客户端

任务：{例如 AND-01，今日任务页面/Room 本地事务}
先读 AGENTS.md、docs/03-UX-FLOWS.md、docs/07-OFFLINE-SYNC.md、docs/10-RECURRENCE-NOTIFICATION.md、docs/13-ANDROID.md、spec/openapi.yaml。

技术不可变：Kotlin + Jetpack Compose + Room + Coroutines/Flow + ViewModel + Hilt + WorkManager。禁止 Flutter、React Native、Tauri Mobile。Repository 读取 Room，所有修改本地事务写任务和 outbox；WorkManager 只做可延迟的同步，不能当成精确闹钟。

实现一个功能的 UI、状态机、用例、Room DAO/Entity/Migration、同步任务、错误/离线/权限 UI 和对应测试。若涉及提醒，说明 Android 13+ 通知权限、exact alarm 的使用资格、Doze/OEM 后台限制。

交付：真实 Gradle 构建与测试结果、操作步骤、代码文件、已测设备/API level、未测事项。不得把 Mock 数据当作真实同步已成功。
