# 67｜DESK-01H：网络恢复同步与桌面发布构建

状态：代码已实现并本地验证；`pnpm test`（15 项）、`pnpm build`、`cargo build --release`（3m08s，exit 0）通过；[Desktop core 43113570657](https://github.com/yml-qq/Planna/actions/runs/43113570657) 与 [Server integration 43113570578](https://github.com/yml-qq/Planna/actions/runs/43113570578) 均 **success**。签名安装包/自动更新属发布部署任务（`DESK-01I`），需要发布密钥，不在仓库内执行。

## 范围与验收

- **网络恢复同步**（`src/session.ts`）：登录/恢复成功后除周期同步外，还订阅 `online` 事件，网络恢复时若持有 access token 立即同步；登出/恢复失败时取消订阅。事件订阅通过 `watchOnline` 注入，测试不依赖真实浏览器事件。
- 测试：前端 **15 项**（新增网络恢复后同步一次、登出时退订；既有 14 项保持通过）。
- **桌面发布构建验证**：在 `apps/desktop/src-tauri` 执行 `cargo build --release`，Tauri 2.12 依赖（含 reqwest/keyring/rusqlite）完成 release 编译与链接（3m08s，exit 0）。验证的是应用二进制，不含打包/签名。
- **发布清单文档化**（见下节），明确需要发布方凭据、不得提交仓库。

## 发布清单（需发布环境执行）

1. 前端：`pnpm install --frozen-lockfile && pnpm test && pnpm build`；Rust：`cargo test && cargo clippy --all-targets -- -D warnings && cargo fmt --check`（core 与 shell）。
2. 安装包：安装 `tauri-cli` 后执行 `cargo tauri build`（Windows NSIS/WiX、macOS `.app`/`.dmg`、Linux AppImage/deb）；产物不入库。
3. 签名：Windows Authenticode 证书、macOS Developer ID + notarization、Linux 仓库密钥；通过 CI secrets/发布机环境变量提供，禁止写入仓库或前端。
4. 自动更新：配置 `plugins.updater.pubkey` 与 endpoints；私钥仅存发布环境；更新包需签名并校验。
5. 冒烟：安装包在干净系统安装/升级/卸载，登录→离线建任务→联网同步→登出撤销会话→重装保留本地数据。
6. 商店/合规：隐私政策、AI 授权说明、删除/导出入口与 `23-LEGAL-RISKS.md` 一致。

## 边界与后续

- `DESK-01I`：在具备发布凭据的环境产出签名安装包、配置自动更新与真机安装冒烟；本轮不声称安装包已生成或签名。
- 网络恢复同步依赖前台进程；OS 级后台任务与省电策略仍为后续项。
