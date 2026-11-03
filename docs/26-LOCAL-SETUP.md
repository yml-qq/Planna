# 26｜本地研发环境、依赖与首次启动指南

> 本文件提供**实际建仓前的搭建路线**，并不声称已经初始化真实源码仓库。具体 SDK 最低版本、平台依赖与命令在新建项目时再次从官方文档检查。需要 macOS 才能完整构建并签名 iOS App。

## 1. 工程准备清单

- Git、一个支持提交/CI 的远程仓库；每次特性使用独立分支/PR。
- .NET SDK 10.0、C# 编辑器与 EF Core CLI；服务端 ASP.NET Core 与 Worker Service。Tauri 桌面仍需 Rust stable、Cargo、rustfmt 和 clippy。
- Docker Engine/Desktop + Compose V2，用于本地 PostgreSQL、Redis、对象存储 mock。
- Node.js LTS + pnpm，桌面 Tauri Vue/TS 环境；Windows/macOS/Linux Tauri 系统依赖参照 Tauri 2 官方安装文档。
- Android Studio Stable、Android SDK、Kotlin/Gradle、模拟器与至少一台 Android 真机。
- macOS + Xcode Stable、命令行工具、iOS Simulator、Apple 开发者账号（发布/签名阶段）；Linux/Windows 无法原生完成 iOS 构建。
- PostgreSQL 16+（Docker），EF Core migration 工具及 CI 测试数据库；Dapper 查询在真实 PostgreSQL 上验证。

## 2. 推荐 monorepo 初始化顺序

```bash
mkdir planna && cd planna
mkdir -p server desktop android ios docs spec db tests/fixtures .github/workflows
# 将文档包中的 AGENTS.md、00-README.md、docs/、spec/、db/ 复制到仓库
# 初始化 git 并提交 docs/spec 基线
```

.NET 后端先生成 `Planna.slnx` 与 Api/Application/Domain/Infrastructure/Worker 项目；按纵向切片添加 EF Core、Dapper 和测试依赖。简单 CRUD 放 EF Core 命令侧，复杂读查询放 Dapper Query Service，不混在一个 Repository。Tauri 桌面按官方创建 Vue+TS 模板，随后实现 Rust commands、本地 SQLite 和 typed bridge。Android/iOS 分别在 Android Studio/Xcode 使用原生 App 模板创建项目，并按文档目录分层。

## 3. 本地 docker-compose（设计示例）

```yaml
name: planna-dev
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_USER: planna
      POSTGRES_PASSWORD: local-dev-only
      POSTGRES_DB: planna
    ports: ["127.0.0.1:5432:5432"]
    volumes: ["pgdata:/var/lib/postgresql/data"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U planna"]
      interval: 5s
      timeout: 3s
      retries: 10
  redis:
    image: redis:7-alpine
    ports: ["127.0.0.1:6379:6379"]
volumes:
  pgdata:
```

上面只是本地示例，密码不可用于生产。实际部署时锁定镜像 digest、资源限制和健康检查。

## 4. 初始检查命令

```bash
dotnet --info
dotnet ef --version
rustc --version # Tauri 桌面
node --version
pnpm --version
docker compose version
# 安装 Android Studio、Xcode 后按平台检查 gradle/xcodebuild
```

首个可运行目标：`GET /health/live` 从 ASP.NET Core API 返回 200，EF Core migrations 能在空数据库完整执行、再跑一次不破坏已有数据，AI Provider 使用 Mock。没有真实账号/推送/支付供应商也能测试 Task CRUD。

## 5. 契约和测试数据落地

建立 `tests/fixtures/` 保存共同的任务/日期/同步/AI 输入输出，JSON Schema 自动验证；OpenAPI 自动生成 Android/iOS/TS DTO（生成器需先验证对 Swift/Kotlin null 语义的支持），每次 schema 变更在 CI 运行契约测试。先做“任务创建 → SQL → 本地同步”成功流，不要各端同时猜接口。

## 6. 开发机分工建议

- Linux/Windows：.NET API/worker、PostgreSQL、Tauri Windows/Linux、Android 可以开展。
- macOS：.NET API/worker、Tauri macOS、Android 和**必须的 iOS 构建测试/签名**。
- CI 中 macOS runner 可承担 iOS/Tauri macOS 构建，但 Apple 账号/证书仍需独立安全管理。

## 7. 常见问题

- “Tauri 前端页面可运行但 invoke 不通”：检查命令注册、capabilities、Rust DTO 和前端类型。
- “Android WorkManager 没有准点执行”：它不是闹钟，参考提醒文档使用适当系统 API。
- “iOS 模拟器能收提醒但真机没有”：排查权限、Focus、通知配置、设备/证书差异。
- “两设备任务数量不一致”：先检查 outbox、owner revision、cursor、conflict，禁止手动清表。
- “AI 能解析但没有创建任务”：正常；草案必须用户确认，再调用应用服务提交。

## 8. 相关官方入口

- .NET：https://learn.microsoft.com/dotnet/core/install/
- EF Core：https://learn.microsoft.com/ef/core/
- Dapper：https://github.com/DapperLib/Dapper
- Rust（Tauri 桌面）：https://www.rust-lang.org/tools/install
- Tauri 2：https://v2.tauri.app/start/
- Android：https://developer.android.com/studio
- Apple：https://developer.apple.com/xcode/
- PostgreSQL：https://www.postgresql.org/docs/
