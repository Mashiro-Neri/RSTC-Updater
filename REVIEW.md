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
| 3 | f0c3fae（代码 `f5e8467`） | **PASS**：无未关闭 BLOCKER/HIGH/MEDIUM；M6 PARTIAL 不阻断（替换路径在无 v5 资产时不可达） | `evidence/v1/review/20261112_0052/REVIEW_REPORT.md` |

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

## 10. 内测通道 + Beta 在线验收（2026-11-12，待复审）

- 新增 `RSTC_RELEASE_CHANNEL=beta`（`ReleaseClient.GetLatestBetaAsync` 取 releases 列表里 tag 最新含 Pre-release；默认仍 `/releases/latest`）。
- 菜单状态新增「通道」行与「（Pre-release）」标记。
- 真实在线：空目录安装 6.0 Beta 成功（120 mods / sha256 一致 / PACK_APPLY=PASS），update 报已最新。
- 证据 `evidence/v1/online-beta-20261112_210729/`；需一轮快速复审（对 62bde99 之后 diff）。

## 11. R9 复审发现与真正修复（2026-11-12）

- R9 复审在 `27fd895` 上复现：越界 zip 经预览失败后 `.rstc-tmp` 仍残留（ZipArchive 句柄在 `BuildPlan` 抛错时未释放，删除被占用而静默失败）→ R8-1 实际未关闭。
- 修复：`BuildPlan` 打开句柄即负责释放（抛错时 Dispose）、`Preview` finally 释放、`DeleteFileQuiet` 增加重试；复现脚本（伪造 release 缓存 + 本地 HTTP + 越界 zip + 无效 token）实测 exit 7、实例未动、`zip_left=False`。
- 证据：`evidence/v1/review/20261112_2153/REVIEW_REPORT.md`（R9 报告）+ 本节复现输出。

- R9 复审结论：`27fd895` **FAIL**（R8-1 未真关闭）；`086e554` 修复后 R9 在固定 commit 独立重放确认 **R8-1 closed**，建议 V1 代码冻结于 `086e554`。

## 12. PCL2 语言 quirk 真正修复（2026-11-12，第 11 轮待复审）

- 之前 `State:9` 预写 Setup.ini 的方案**未被证实有效**（用户实测仍英文），根因在 PCL `GetVanillaName` 的 `jar` 优先级。
- 修复：实例 json `clientVersion=26.3` + 空 `saves` 目录 + 更新时自动补（备份 `.rstc-bak`）；用户实测中文。
- 代码 commit 见 git log；需第 11 轮复审确认无回归后冻结。

## 13. 第 11 轮复审收尾（2026-11-12，冻结 11a4a19）

- R11-1（LOW，docs）：根因文案按 PCL 源码更正为“0.0.0/无可识别版本”（A2 先命中 fabric intermediary 0.0.0），非“jar 优先”。已改开发文档更正段。
- R11-2（LOW，test gap）：新增 `scripts/v1/language-smoke.ps1`（新装 clientVersion/saves、删字段强制更新补回+备份、二次幂等）与 `smoke.ps1` 两条断言（实例 json clientVersion=26.3、saves 目录）。
- NIT（保留）：`--fetch-pack`/`--pack` 两个阶段 0 CLI 未接 Ensure（内部遗留，不影响玩家路径）；`.rstc-bak` 在只读 json 场景可能继承只读属性，需人工清属性后重试；README/交付说明轮次与基线已更正。
- 冻结：V1 代码基线 `11a4a19`；此后 test/docs-only 变更不改变代码基线。

## 14. UI 改版（ASCII 终端风格，2026-11-12，待复审）

- 新增 `Ui.cs`：ASCII 横幅（宽/窄自适应）、`-- 状态 --` 分段线、对齐的中文状态行、页脚（版本/通道）。
- 主菜单改为编号菜单（`[1] 安装到新目录 ...` + 输入编号），脚本模式仍走原 SelectPrompt（测试兼容）。
- `--status` 与「检查关键文件」改用同一套 ASCII 面板；长路径按显示宽度截断（`...`），避免中文宽度错位。
- 纯 UI 改动，不改任何业务逻辑/退出码；smoke 32/32、menu 12/12、language 12/12 复跑通过。

## 15. UI 二次改版（致敬 v4.11 红黑风格，2026-11-12，待复审）

- 用户反馈首版 ASCII 风格不如 v4.11；据用户截图重做：红色像素块 `RST` 标志 + 红色边框标题区 + 版本/通道 + 紧凑状态区 + 经典 `▲ ▼ 移动 Enter 确认` 方向键菜单（Spectre SelectionPrompt 自定义高亮：白字暗红底）。
- `--status`/关键文件检查复用同一主题；状态行压缩为 6 行减少噪音；长路径按显示宽度截断。
- 预览构建输出在 `bin\Preview\`（正式 exe 被用户运行占用，待关闭后重建）。
- 待：用户关闭运行中的更新器 → 重建正式 exe → smoke/menu/language 回归 → 复审。

## 16. UI 精致版（2026-11-12，待复审）

- 双线边框标题区（`RSTC // UPDATER` + 版本/通道），像素块 RST 标志（每字母 8×5，居中）。
- 状态区加状态圆点（实例/远端/Java/隔离），行数压到 6 行；长路径截断。
- 菜单改为全宽反白方向键菜单（`▶` 标记、白字暗红高亮、`↑↓ 移动 Enter 确认`），脚本模式仍走 SelectPrompt 不受影响。
- 预览构建在 `bin\Preview\`（正式 exe 被用户运行中的旧进程占用，待关闭后重建并回归）。

## 17. UI HUD 终稿（2026-11-12，待复审）

- 满宽红底白字标题条（版本/通道右对齐）+ 5 行 RST 像素字（上亮红/下暗红渐变）+ 标题行 + 双线规则线；boot 时逐行出现 + 扫描线动画。
- 状态区改为反白红底标签徽章（游戏目录/当前实例/…），值固定列对齐；交互模式压缩为 PATH/ENV/NET 三行 HUD。
- 菜单为全宽反白选中条（▶ + 说明），底部 `↑ ↓ 移动 Enter 确认`；交互模式每轮清屏重绘，避免滚动把标题挤出屏幕。
- GitHub Release 查询加 60s 进程内缓存 + 首次查询显示 spinner，减少菜单重绘卡顿。
- 窄终端/矮终端降级：高度 <24 行时只显示标题条+规则线。

## 18. UI 重做（纯 Spectre 渲染，2026-11-12，待复审）

- R13 复审结论：c7293b2 的“手写光标 HUD”在 80×30 真实控制台花屏（R13-1 HIGH，UI-only）；业务无回归。
- 决策：放弃手写光标/背景色混合方案，改为**纯 Spectre 渲染**——重型红框标题面板（居中 RST 像素字）、圆角红框状态表、SelectionPrompt 方向键菜单；不再手工 SetCursorPosition/ConsoleColor，避免渲染污染。
- 已删除 `Ui.ArrowMenu` 的手写实现（改为内部 SelectionPrompt），删除扫描线/满宽红条/徽章等易污染元素。
- 待：冻结干净 commit → 重建 exe → 用 ConPTY 80×30/120×30 重新验收后再冻结 UI。

## 19. UI 架构定稿：固定宽度滚动式布局（2026-11-12，待复审）

- R13/R14 暴露的根因：Windows conhost 在窗口拖动/缩放时会重排整个屏幕缓冲区；全屏 HUD（Panel Expand、满宽边框、绝对光标、Clear+重绘）必然变形/花屏。
- 定稿方案：**固定 76 列画布的滚动式输出**——所有行 ≤76 显示列；不用 Clear、不用绝对光标、不用满宽 Panel/Table；`RST` 像素标志+红条静态打印一次；状态块与编号菜单顺序追加；菜单改编号输入（无 LiveDisplay 重绘）。
- 远端版本改为只读磁盘缓存（`ReleaseClient.TryGetCached`），菜单不再阻塞网络请求；`--status` 同样不联网。
- 实测（ConCapture 读取真实控制台缓冲区，120×30）：启动 2.5s 内完整显示；模拟窗口 resize 到 100×35 后文字布局保持完整（无重排/串行）。证据：`evidence/v1/ui-layout/`。
- 已知边界：窗口宽度 < 80 列时 76 列内容软换行（建议 ≥80 列）；方向键菜单改为编号输入（为彻底消除重绘）。

## 20. TUI 正式实现（方向键 + 全屏 + 缩放安全，2026-11-12，待复审）

- 按用户要求恢复 TUI：ANSI/VT 全屏布局、方向键菜单、选中条反白；每帧整屏重绘，监听窗口尺寸变化后 `2J` 清屏重绘，拖动/缩放自动恢复正确排版（ConCapture 真实控制台缓冲区验证：120×30 初始、DOWN×3、RESIZE 到 100×35 后布局完整、Enter 进入动作后清屏输出）。
- 输入：↑↓ 移动、Enter 确认、Esc/Ctrl+C 退出、数字键 1-8 直选；动作执行时退出 TUI 进入普通滚动输出（Spectre 表格等照常），完成后按任意键重新进入 TUI。
- 回退：VT 不可用或输出被重定向时，自动退回固定 76 列滚动式布局（`--menu-script`/测试路径不变）。
- 状态数据改为本地快照（缓存版本/Java/隔离检测），不阻塞菜单绘制。
- 构建：正式 exe 待用户关闭占用后重建；预览构建 `bin\Preview\RstcUpdater.exe` 已验证 TUI。
