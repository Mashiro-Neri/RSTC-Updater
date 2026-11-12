# RSTC Updater · 红石镇客户端更新器 v5

红石镇客户端（Redstone Town Client）的安装 / 更新 / 迁移 / 回滚工具，替代 v4 的 PowerShell 脚本。

## 下载

从 [Releases](https://github.com/Mashiro-Neri/RSTC-Updater/releases) 下载最新 `RSTC-Updater-<版本>.zip`，解压后双击 `启动.bat`（或 `RstcUpdater.exe`）。

- 解压目录自选；**不要**放进 `.minecraft` 里也能运行。
- v4 用户：v4 自更新只认旧脚本资产，不会自动升级到 v5；请手动下载 zip 替换旧更新器目录。
- 程序暂未签名，Windows SmartScreen 提示时请核对 Release 页 SHA256 后选择“仍要运行”。

## 使用

主菜单方向键操作：`↑↓` 移动、`Enter` 确认、`Esc` / `Ctrl+C` 退出；也可按数字键直选。

| 菜单 | 功能 |
|---|---|
| 安装到新目录 | 5 步向导：游戏目录 → 实例名 → 整合包来源 → 下载方式 → 确认；支持 Windows 原生文件夹/zip 选择器 |
| 更新现有实例 | 拉取最新整合包并应用，保留存档/设置，可预览与 dry-run |
| 从旧实例迁移 | 白名单迁移存档/截图/地图等；默认拒绝覆盖，覆盖前备份 |
| 检查更新器更新 | 检查并自更新（退出后自动替换、失败回滚） |
| 检查关键文件 | 状态面板 + 26.3/Fabric/资源完整性检查 |
| 回滚未完成更新 | 按 journal + 备份回滚，fail-closed |

向导中列表步骤可 `b`/`Esc` 返回；手动输入步骤输入 `b` 返回。

### 下载与镜像
- 整合包下载：自动测速选最快镜像 / 手动选镜像 / 自定义地址，支持 HTTP 代理。
- 原版 MC：自动优先 BMCLAPI 镜像（清单/版本/客户端），不可用自动回退 Mojang 官方；`RSTC_NO_MIRROR=1` 可关闭。
- 原版与整合包并行下载，同一行显示两条进度条。
- 已完整下载的整合包（sha256 一致）重跑会直接复用；原版由 CmlLib 断点复用。
- 下载中 `Ctrl+C` 取消会中止并保留现场，重跑继续复用。

### 整合包来源
- 稳定版：`releases/latest`；
- Beta 版（内测）：包含 Pre-release；
- 本地 zip：手动选择，不下载、不显示镜像步骤。
- 当前 6.0 为 Beta：稳定版 5.5 基于 1.21.10，与 26.3 不兼容时更新器会自动改用 Beta；6.0 正式发布后自动走稳定版。

### 安装后
- 启动器版本列表只保留「红石镇客户端」实例与原版 26.3；多出来的 `fabric-loader-*` 目录会自动清理（被其它实例引用时保留）。
- 安装校验输出 `INSTALL_VERIFY=PASS`；整合包 sha256 强校验，校验失败拒绝应用。

## 命令行

```powershell
RstcUpdater.exe --install-v1 <游戏目录> [--instance <实例名>] [--zip <本地包>] [--beta]
RstcUpdater.exe --update-v1 <游戏目录> --instance <实例名> [--zip <本地包>] [--dry-run] [--force] [--beta]
RstcUpdater.exe --migrate-v1 <游戏目录> --from <源实例> --to <目标实例> [--categories saves,screenshots,maps,dh,settings] [--overwrite]
RstcUpdater.exe --check-self          # 检查更新器自身更新
RstcUpdater.exe --status [游戏目录]   # 状态面板 + 关键文件检查（非交互）
RstcUpdater.exe --rollback <游戏目录> --instance <实例名>
RstcUpdater.exe                       # 无参数：交互菜单
```

测试/自动化辅助：`--menu-script <脚本>`、`--self-update-apply`。

## 开发

- .NET Framework 4.8 控制台程序，用 .NET SDK 8 构建；依赖 CmlLib.Core、Spectre.Console、System.Text.Json（见 `packages.lock.json`）。
- 构建：`dotnet build src\RstcUpdater\RstcUpdater.csproj -c Release`
- 测试：`scripts/v1/smoke.ps1`（32 断言）、`menu-smoke.ps1`（12）、`language-smoke.ps1`（12）、`scripts/spike/run-provision-0-2.ps1`
- 开发文档：`requirements/RSTC-Updater-5.0-开发文档.md`；验收指引：`docs/验收指引.md`；发布清单：`docs/发布检查清单.md`；发布说明：`docs/更新说明-v5.0.0.md`
- 证据原件：`evidence/`（本地保留，不入库）
- 玩家真实目录只读，测试只用独立临时目录。

## v4 旧版

`updater/` 保留 v4.11 的 `update_modpack.ps1` / `update_modpack.bat`，只读参考，不再更新。
