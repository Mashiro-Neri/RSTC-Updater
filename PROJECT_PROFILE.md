# 项目画像（PROJECT_PROFILE）

> contractVersion: 1.0 · last-reviewed: 2026-11-12 · owner: AI（用户确认）

## 1. 项目是什么

- 目标用户 / 唯一核心价值：红石镇客户端玩家。从空目录装出可启动的「Minecraft 26.3 + Fabric + 整合包」实例，并能整包更新、迁移旧实例玩家数据。唯一价值是**装得上、玩得了**。
- 语言/框架/入口：C# 控制台；目标框架 `.NET Framework 4.8`（本机实测运行在 4.8.1 / Release 533325）；入口 `src/RstcUpdater/Program.cs`。
- 运行方式：Windows 10/11 本机双击 `.bat` 拉起程序；无服务端。

## 2. 命令

| 用途 | 命令 | 最近验证 |
|---|---|---|
| install | `dotnet restore src/RstcUpdater/RstcUpdater.csproj` | 2026-11-11 通过 |
| build | `dotnet build src/RstcUpdater/RstcUpdater.csproj -c Release` | 2026-11-11 通过（0 警告 0 错误） |
| verify | 见 `delivery.config.json` 的 `verify` | 2026-11-11 exit 0 |
| test | `pwsh -File scripts/v1/smoke.ps1 -GameDir <有 26.3 的临时目录>` | 2026-11-12 **30/30 PASS**（安装/更新/迁移/自更新检查/状态） |
| test-menu | `pwsh -File scripts/v1/menu-smoke.ps1 -GameDir <同上>` | 2026-11-12 **12/12 PASS**（`--menu-script` 驱动真实菜单：安装/检查/迁移/自更新/退出 + 缺/空/耗尽脚本 exit 2） |
| smoke | `RstcUpdater.exe --status [游戏目录]` | 2026-11-12 非交互状态面板 exit 0 |
| install-test | `RstcUpdater.exe --install <空目录> [--reuse]` | 2026-11-11 L2 通过：cold 2:20/588.9 MiB，warm 3.6 s/0 MiB |
| a1b | PCL2/HMCL 启动 `RSTC-0-2-A2/A1/B` | 2026-11-12 用户实测 **6/6 全部启动**（7 mods 加载，无异常），证据 `evidence/acceptance/20261112_193644/a1b/` |
| v1-install | `RstcUpdater.exe --install-v1 <游戏目录> [--instance <名>] [--zip <本地包>]` | 2026-11-12 夹具包成功；Release 路径在 6.0 前正确拦截 5.5（exit 5，不建实例） |
| v1-update | `RstcUpdater.exe --update-v1 <游戏目录> --instance <名> [--zip <本地包>] [--dry-run]` | 2026-11-12 计划预览/实际更新/越界 zip exit 7 均通过 |
| v1-migrate | `RstcUpdater.exe --migrate-v1 <游戏目录> --from <源> --to <目标> [--categories ...] [--overwrite]` | 2026-11-12 默认拒绝覆盖/覆盖前备份/失败回滚通过 |
| v1-self | `RstcUpdater.exe --check-self` | 2026-11-12 5.0.0 vs v4.11 正确报已最新；替换 E1/E2 待 v5 资产 |
| provision | `RstcUpdater.exe --provision <游戏目录> [--reuse] [--mod-src <mods>]` | 2026-11-11：A2/A1/B 结构 + 隔离配置保留实测通过 |
| pack | `RstcUpdater.exe --pack <游戏目录> --zip <包> --instance <名> [--update] [--dry-run]` | 2026-11-11：0-5 **47 条**断言通过（含 H5/M3 回归） |
| fetch-pack | `RstcUpdater.exe --fetch-pack <游戏目录> --release-json <json> --instance <名>` | 2026-11-11：真实 5.5 包下载+sha256+422 文件内容校验通过 |
| rollback | `RstcUpdater.exe --rollback <游戏目录> --instance <名>` | 2026-11-11：fail-closed 实测 |
| deploy / rollback | N/A（玩家本机控制台程序，无部署） | — |

## 3. 运行与部署身份

- 生产拓扑：玩家本机单进程控制台；不存在服务器/容器。
- 产物/镜像/容器：`RstcUpdater.exe` + 依赖 DLL（单文件与否由阶段 0 决定）。
- 配置来源 / 密钥来源（不写值）：`%APPDATA%\RSTC-Updater\config.json`（计划）；无密钥。
- 健康检查 / build identity：无健康检查；身份 = git commit + 产物清单。
- 数据/卷/备份：`.minecraft/versions| libraries| assets` 与实例目录；备份在 `.minecraft/rstc-backup/`。

## 4. 数据与外部依赖

- 数据库/文件/队列/缓存：只有文件系统；缓存清单与下载临时目录在 `.minecraft/.rstc-tmp/`。
- 迁移方式与顺序：v4→v5 手动（用户已确认）；玩家数据迁移按类别复制，不移动。
- 外部 API/设备/凭据（只写来源）：GitHub Releases API、Mojang piston-meta、Fabric Meta、BMCLAPI；无凭据。

## 5. 高风险区

- 路径：`versions/**`、`libraries/**`、`assets/**`、`**/PCL/**`、`**/hmclversion.cfg`、`**/options.txt`、`**/servers.dat`、`.minecraft/rstc-backup/**`。
- 操作（删除/迁移/权限/部署/外部副作用）：删除包外文件、覆盖写入、启动器配置写入、玩家数据迁移。

## 6. 约定与坑

- 目录/命名/错误/日志/测试风格：代码在 `src/RstcUpdater/`；证据在 `evidence/<task>/<commit>/`；文档在 `requirements/`；退出码 0 成功 / 2 参数 / 3 版本缺失 / 4 字段不符 / 5 网络 / 6 依赖 / 7 安装失败 / 8 校验失败 / 130 取消；日志 `update_log_<ts>.txt`。
- 已知噪音、历史事故→必须检查：NuGet 走国内镜像可能慢；GitHub 资产下载易超时（已多次踩到）；Spectre 在 net48 下 CJK 宽度需自测；v4.11 自更新要求 release 同时含 ps1+bat。
- UNKNOWN / 待确认：HMCL `gameDirType` 枚举、PCL `Setup.ini` 最小字段集、`jar:"26.3"` 兼容性、`-DFabricMcEmu` 是否必需。

## 7. 契约映射

- 项目的 G0–G4 / task-protocol 与本契约 Gate 如何对应：Gate 0=开发文档+开工卡；Gate 1=最小闭环实现；Gate 2=自检+运行证据；Gate 3=独立 Reviewer；Gate 4=N/A（无部署，阶段 0 只写临时目录）。
- 项目更严的规则：发布包禁止包含 `PCL/`；玩家实例目录只读（除用户明确指定的安装/更新目标）；证据目录只追加不修改。
