# 项目 Review 指南（REVIEW.md）

> last-reviewed: 2026-11-11 · owner: 项目负责人

## 1. 这个项目“出错”长什么样

- 典型故障：路径拼接错导致写进玩家真实实例；删除清单误删 `saves/`；下载未校验就解压；启动器配置写坏导致 PCL2 不认版本；镜像 URL 写死进实例 json。
- 最坏后果：玩家存档丢失；实例无法启动；旧实例被覆盖。
- 用户会看到什么：`.minecraft` 目录被改乱、启动器版本列表少了/多了版本、游戏启动报错、更新器报错退出。

## 2. 必看路径

- 高风险的目录/文件模式：`versions/**`、`libraries/**`、`assets/**`、`**/PCL/**`、`**/hmclversion.cfg`、`**/options.txt`、`**/servers.dat`、`.minecraft/rstc-backup/**`。
- 本任务触及这些路径时必须升级风险、找独立 Reviewer：删除/覆盖玩家文件、迁移、启动器配置写入 → 最低 L2；批量删除/不可逆 → L3。

## 3. 必跑命令

```text
verify：   见 delivery.config.json
smoke：    RstcUpdater.exe --probe（或 delivery.config.json 定义）
identity： git rev-parse HEAD
rollback dry-run： 阶段 0 为 N/A（只写临时目录）
```

## 4. 验收必须覆盖

- 真实入口：`RstcUpdater.exe` 实际启动（不是单测）。
- 数据/文件/队列副作用：安装/更新后目标目录的文件增删清单。
- 失败/取消/超时/重启：断网、Ctrl+C、下载中断后重跑。
- 冷/热性能（如适用）：首次安装（cold）与复用已有文件（warm）的耗时/流量。

## 5. 已知噪音 / 跳过

- 可忽略的预期错误：NuGet 源 302 到 `nuget.azure.cn`；GitHub 资产下载偶发超时（重试即可）；`NETSDK1138` 之类的框架支持警告；GitHub REST 未认证 60 次/小时，实测已触发 403（用 gh 认证或 ETag 缓存）。
- 已知设计约束（v5）：CmlLib 默认安装器会下载 Java，v5 已显式移除 `JavaFileExtractor`；26.3 按 Windows 客户端规则过滤后库是 74 个（114 个 artifact 中的 40 个属于其他 OS/native）。
- 证据脚本需要 PowerShell 7+（`#Requires -Version 7.0`），Windows PowerShell 5.1 跑不了。
- 禁止当作 finding 的风格项：控制台配色、表格边框样式、日志里的中文标点。
- 历史事故→检查项：v4.11 曾因 UTF-8 编码导致启动闪退（`62788cd`），v5 所有中文输出必须显式 UTF-8；v4 自更新依赖两个资产名，v5 变更资产名时必须验证 v4 用户路径。

## 6. V1 审查记录（2026-11-12）

| 轮次 | 审查对象 | 结论 | 报告 |
|---|---|---|---|
| 1 | 9c67a6b | review-open：H1（迁移覆盖）+ M1/M2/M3/M5/M6/M8 | `evidence/v1/review/20261112_0023/REVIEW_REPORT.md` |
| 2 | 9fea547 | 6 项 closed；新 R2-M1（迁移遇只读目标崩溃/部分写入） | `evidence/v1/review/20261112_0038/REVIEW_REPORT.md` |
| 3 | f0c3fae（代码 `f5e8467`） | **PASS**：无未关闭 BLOCKER/HIGH/MEDIUM；M6 PARTIAL 不阻断（替换路径在无 v5 资产时不可达） | `evidence/v1/review/20261112_0052/REVIEW_REPORT.md` |

- 冒烟：`scripts/v1/smoke.ps1` 30 条断言，最近一次 PASS 绑代码 commit `29f02b2`（证据 `evidence/v1/smoke/29f02b2…/`）。
- 开放 LOW/NIT（不阻断）：自更新 `/PURGE` 与 E1/E2 实际替换（**发布 v5 自更新资产前必须补验**）、TUI 完整动作按键、真实网络卡死/403。
- 未验证（不得 verified）：A1b（PCL2/HMCL 实际启动/mod 生效）、真实 6.0/v5 Release 成功路径、0-7、用户验收。

## 7. 已接受 NIT（V1 代码基线 `62bde99`，第 6 轮复审 PASS）

- R4-N4（partial）：MultiPrompt 未匹配 token 静默忽略（journal 提示的 FlowFailureException 已在 `62bde99` 修复上抛）。
- R4-N2/R4-N5（open）：删除清单 PageSize、SelfUpdater 其他早期 abort 的 %TEMP% 清理。
- 未来出现 v5 自更新资产后，需把 menu-smoke 脚本输入扩为 12+ 行并补 E1/E2 实测。

## 8. A1b 用户验收记录（2026-11-12）

- 用户实测：`PCL2: A2=ok A1=ok B=ok；HMCL: A2=ok A1=ok B=ok`。
- 日志（evidence/acceptance/20261112_193644/a1b/）：三实例均 Loading Minecraft 26.3 with Fabric Loader 0.20.1、Loading 7 mods、进 Render thread、Stopping!，无 Exception。
- 结论：0-2/0-3 用户验收 PASS；V1 默认实例方案维持 A2。

## 9. V1 后补修复（PCL2 语言，2026-11-12）

- 根因：PCL2 首次启动把新实例语言写成大写 `zh_CN`，26.3 只认 `zh_cn` → 英文。
- 修复：安装时预写 PCL `Setup.ini`（`State:9` 等），并在安装/更新后把语言码规范小写。
- 用户实测：重装 RSTC-6.0-TEST 后启动为中文（120 mods 正常）。
- 该改动已由第 6 轮复审 PASS（`evidence/v1/review/20261112_2100/`）。
