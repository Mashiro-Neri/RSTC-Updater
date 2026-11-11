# 变更开工卡（CHANGE_BRIEF）

> 任务：阶段 0-1 · .NET Framework 4.8 + CmlLib.Core 4.1.0 + Spectre.Console 0.57.2 可行性（含真实安装 26.3） · 日期：2026-11-11 · contractVersion：1.0

## 目标（用户语言）

- 用户现在能做什么：只能用 v4.11 PowerShell 更新器；v5 尚无可用程序。
- 本次要让用户看到什么：
  1. 一个能在 .NET Framework 4.8 上编译运行的控制台程序，能拉到 26.3 元数据并打印关键字段；
  2. 能用 CmlLib 在**独立临时目录**把原版 26.3 真正装出来（versions/26.3、client jar、assets/indexes/34.json、libraries）。
- 明确不做（non-goals）：不写实例 json（0-2）、不装 Fabric（0-2）、不碰任何玩家实例（`E:\其他文件\红石镇客户端\**` 只读）、不下载 Java 运行时（产品语义：只检测）。

## 验收（可执行）

| 操作/命令 | 期望可观察结果 | 环境 | 通过阈值 |
|---|---|---|---|
| `dotnet build src\RstcUpdater\RstcUpdater.csproj -c Release` | exit 0，0 错误 | 本机 SDK 8.0.405 / net48 | exit 0 且 exe 存在 |
| `RstcUpdater.exe --probe` | exit 0；输出 `java=25`、`assetIndex=34`、`mainClass=net.minecraft.client.main.Main` | 有外网 | 三字段与官方 JSON 一致 |
| `RstcUpdater.exe --probe --url https://127.0.0.1:9/x` | exit 5；stderr 含 URL 与原因 | 本机 | 非 0 且信息可读 |
| `RstcUpdater.exe --install <独立临时目录>` | exit 0；`INSTALL_VERIFY=PASS` | 本机 + 外网 | `versions\26.3\26.3.json` 存在；client jar = 41,483,720 B；`assets\indexes\34.json` = 5149 对象；`libraries\*.jar` ≥ 74（Windows 客户端规则过滤；用户已确认 q-accept-74） |
| `scripts\spike\verify-install-26.3.ps1 -GameDir <同一目录>` | 输出 `VERIFY=PASS` | 本机 | exit 0 |
| 中文表格 | 不错位、不乱码 | Windows 控制台 | 目视 + 文本证据 |

## 风险

- 风险级别：**元数据探针 L1**（只读网络）；**安装 26.3 L2**（写 `versions/`、`libraries/`、`assets/`，外部下载）。
- 涉及数据/迁移/权限/部署/外部副作用：安装阶段有外部下载与磁盘写入，仅限独立临时目录。
- 最坏结果：下载失败/超时、临时目录占用约 1.3 GB；不影响玩家实例与 v4。
- 回滚方式：删除该临时目录（安装前目标必须为空目录，代码强制校验）。
- **用户确认（L2）**：2026-11-11，用户原话「继续（范围：仅临时目录安装 26.3，约 646 MB）」。

## 语义与兼容

- 是否有语义变化：新增 v5 命令；v4 完全不动。
- 旧数据/旧调用方是否兼容：兼容（v4 目录未改）。

## 需要用户决定

- 无（L2 安装已获用户原话确认；验收口径变更已获用户确认：2026-11-11「确认改 74（Windows 客户端规则）」，来源 q-accept-74）。

## 证据计划

- 自检命令：`dotnet build`；`--probe`；`--probe --url`（失败路径）；`--install`；`verify-install-26.3.ps1`。
- 真实入口/副作用：exe 真实运行；安装产生真实文件与下载。
- 失败/重启路径：无效 URL（exit 5）；非空目录（exit 2）；已安装目录重跑（warm，应快速跳过已存在文件）。
- 冷/热性能：首次安装（cold）与第二次运行（warm）的耗时、下载量对比。
- 证据绑定：`evidence/stage0/<task>/<commit>/`，含 `commit.txt`、`SHA256SUMS`、`ProductVersion`。
