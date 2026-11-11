# 变更开工卡（CHANGE_BRIEF）

> 任务：阶段 0-1 · net48 + CmlLib.Core 4.1.0 + Spectre.Console 0.57.2 可行性 spike · 日期：2026-11-11 · contractVersion：1.0

## 目标（用户语言）

- 用户现在能做什么：只能用 v4.11 PowerShell 更新器；v5 一行代码都没有。
- 本次要让用户看到什么：一个能在 .NET Framework 4.8 上编译并运行的控制台程序，能真实拉到 26.3 的版本元数据并打印关键字段（javaVersion=25、mainClass、assetIndex=34），并确认 CmlLib/Spectre 两个库在 net48 下可用。
- 明确不做（non-goals）：不安装完整 26.3（650 MB 放到 0-2）、不写实例 json、不碰任何玩家实例、不做 UI 流程。

## 验收（可执行）

| 操作/命令 | 期望可观察结果 | 环境 | 通过阈值 |
|---|---|---|---|
| `dotnet build src/RstcUpdater/RstcUpdater.csproj -c Release` | exit 0，产出 `bin/Release/net48/RstcUpdater.exe` | 本机 SDK 8.0.405，net48 | exit 0 且 exe 存在 |
| `RstcUpdater.exe --probe` | 退出码 0；stdout 含 `java=25`、`assetIndex=34`、`mainClass=net.minecraft.client.main.Main` | 有外网 | exit 0 且三字段与官方 JSON 一致 |
| 中文/表格输出 | Spectre 表格中文对齐，无乱码 | Windows 控制台 | 目视 + 文本证据 |
| 依赖清单 | 输出目录 DLL 清单 | 同上 | 与 NuGet 依赖树一致（System.Text.Json 等） |

## 风险

- 风险级别 L1 / 命中规则：普通功能 + 只读网络请求 + 只写临时目录。
- 涉及数据/迁移/权限/部署/外部副作用：无；不写 `.minecraft`，不写玩家目录。
- 最坏结果：NuGet 还原失败/网络超时，产物不完整；不影响任何玩家数据。
- 回滚方式：删除 `src/RstcUpdater/bin`、`obj` 与临时目录即可。

## 语义与兼容

- 是否有语义变化：无（新增工程，不改 v4）。
- 旧数据/旧调用方是否兼容：v4 完全不动。

## 需要用户决定（≤2 个）

0 个。本任务无产品取舍；阶段 0 的实测结论会以报告形式回给用户。

## 证据计划

- 自检命令：`dotnet build -c Release`；`RstcUpdater.exe --probe`。
- 真实入口/副作用：exe 真实运行 + 真实 HTTP 请求 piston-meta；文件系统只写 `bin/obj`。
- 失败/重启路径：断网重跑 `--probe`（应非 0 且有可读错误）。
- 冷/热性能（如适用）：本任务不测；留给 0-2 的安装 spike。
