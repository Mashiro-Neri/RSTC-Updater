# RSTC-Updater 5.0 开发文档（修订版 v2）

> **开发 AI 使用说明**：本文档记录了与用户讨论后形成的目标、偏好和当前建议，仅作为开发依据与决策上下文，不应被盲目视为绝对正确、完整或不可变的事实。开发前及开发中，应结合实际代码库、运行环境、目标系统、官方文档与可执行测试验证关键假设。若发现本文档与实际情况、接口限制、性能表现或更合适的实现方案不一致，应说明差异、给出可行选项，并与用户讨论后再决定；不要默默偏离已确认的用户体验，也不要为了机械遵守文档而采用已验证不合适的实现。

> **实现契约**：`ai-contract/AI_DEVELOPMENT_CONTRACT.md`（风险分级、Gate 流程、证据卡、独立审查；实现 AI 必须遵循）。当前契约包只有 `E:\ZM\ai-contract.zip`，**实现前必须解压到仓库根目录**，并按契约建立 `PROJECT_PROFILE.md`、`delivery.config.json`、`REVIEW.md`（见附录 D）。

> **版本关系**：更新器 5.0 ↔ 整合包 6.0（Minecraft 26.3 / Fabric 0.20.1）。两者版本号独立演进，状态面板必须同时显示两者，避免玩家和运维混淆。

> **文档状态**：这是 2026-11-11 的修订版，取代 `E:\ZM\RSTC-Updater-开发文档.md` 的技术部分；§2 的用户体验结论沿用原文档且未被改动。原文档保留为历史版本。所有"已核实"标记都附证据来源；无法复现的写成"待实测"，不冒充事实。

> **开放变量**：整合包 6.0 仍在选 mod（约 100–200 个），依赖包内容的描述以 5.5 实包为基线。6.0 定稿后必须重核 §1.2、§4.2、§4.3、附录 A。

---

## 0. 术语与归类标准

### 0.1 术语

| 术语 | 含义 |
|---|---|
| 更新器 / v5 | 本项目的 C# 控制台程序，版本号 5.x |
| 整合包 / 6.0 | GitHub Release 里的 `RSTC-<版本>.zip`（mrpack 结构），基于 MC 26.3 |
| 实例 | `.minecraft\versions\<实例名>\`，玩家可见的版本 |
| 游戏目录 | `.minecraft` 根，含 `versions/`、`libraries/`、`assets/` |
| 包覆盖清单 | `overrides/` 下的顶层路径集合（5.5 为 `config/ mods/ options.txt servers.dat`） |
| 阶段 0 | 正式编码前的 spike / 真机验证阶段（§9） |

### 0.2 归类判定标准（本文档的一致性口径）

| 归类 | 判定标准 |
|---|---|
| 【已确认】 | 用户在结构化提问中选中某一项；选项描述文本同样计为已确认。来源 id 见附录 C |
| 【用户偏好】 | 用户以自由文本作答，我据此解读出的结论 |
| 【技术建议】 | 我提出的候选方案，含理由、代价与替代方案，不伪装成定稿 |
| 【已核实】 | 本机命令、公共 API 或源码实测得到的事实，附证据 |
| 【待实测】 | 需要阶段 0 跑出来的结论，不得当成事实使用 |
| 【开发时决定】 | 不影响产品目标的内部实现问题 |

---

## 1. 背景与目标

### 1.1 现状（v4.11，已核对源码）

源码位置：`updater/update_modpack.ps1`（954 行）。v4.11 Release 资产 `update_modpack.ps1` 为 52,567 B，本地工作副本 53,521 B——差值恰好 954 B（每行 1 B），是 **CRLF / LF 换行差异**，内容一致，实现以 Release 资产为准即可（§7.1 记为已解释项）。

| 项 | 值 | 证据 |
|---|---|---|
| 版本常量 | `$Script:UpdaterVersion = "4.11"` | `update_modpack.ps1:9` |
| 仓库 | `Mashiro-Neri/Redstone-Town-Client_update_modpack` / `Mashiro-Neri/RSTC-Updater` | `:7`、`:10` |
| 同步目录 | `mods`、`config`、`resourcepacks`、`shaderpacks`、`PCL` | `:11` |
| 保护文件 | `options.txt`、`servers.dat` | `:13` |
| 备份目录 | `_modpack_backup` | `:15` |
| 下载镜像 | 5 个（GitHub 直连 + 4 个 gh-proxy 反代） | `:17–23` |
| 测速参数 | 预热 128 KB、采样上限 3 MB / 2.0 秒、重试 3 次 | `:25–28` |
| 版本状态 | `RSTC_version.txt`，内容为 Release Tag | 读 `:192–195`，写 `:201–204` |
| 自更新 | 必须同时存在 `update_modpack.ps1` 与 `.bat` 资产，否则返回 null；Tag 相等即视为最新 | `:505–516`、`:535` |
| McRoot 语义 | 含 `options.txt` 的目录（脚本放在版本文件夹里时就是版本文件夹本身），**不是** `.minecraft` 根 | `:785–811` |
| 解压方式 | 下载 zip → `ZipFile.ExtractToDirectory` | `:280`、`:344` |
| 首次安装 | 只把 zip 下载到桌面，由玩家手动拖进启动器 | `:724–725` |
| 日志 | `<McRoot>\update_log_<时间戳>.txt` | `:816` |

> 注意：`README.md` 里 `--mc-root "D:\.minecraft"` 的示例只有在 `options.txt` 位于 `.minecraft` 根时才成立。包走版本隔离时，`--mc-root` 应指向版本文件夹。v5 的"游戏目录"改为 `.minecraft` 根，**这是一次语义变更**，必须在 UI 与文档里写清（§4.2）。

### 1.2 5.5 实包结构（已核验）

发布仓库共 8 个 release，最新 5.5：

| 项 | 值 |
|---|---|
| 资产名 | `RSTC-5.5.zip` |
| 大小 | 609,837,724 B（581.6 MiB） |
| sha256 | `cda10e2dc9a4b61a9a96fd18e5cccbab8d1705c76ce837f746e76cda4341085f`（Release 资产 `digest` 字段） |
| tag | `v20260922-001655`（本地时间戳） |
| published_at | `2026-09-21T16:13:38Z`（用于新旧判定） |
| ZIP 结构 | 427 个条目，中央目录 41,608 B，位于 609,796,094；服务器返回 `Accept-Ranges: bytes` |
| 顶层 | `modrinth.index.json` + `overrides/`（426 个文件，含 `config/`、`mods/`、`options.txt`、`servers.dat`） |
| `PCL/` | **用户要求：发布包里不要有 `PCL/`**。5.5 实包按原文档核验为不存在；但 PCL 导出配置 `IncludeLauncherCustom:True` 会打包个性化内容，6.0 前必须复核导出结果 |

`modrinth.index.json` 全文：

```json
{
  "game": "minecraft",
  "formatVersion": 1,
  "versionId": "5.5",
  "name": "红石镇客户端",
  "summary": "正式版 1.21.10, Fabric 0.19.5",
  "files": [],
  "dependencies": {
    "minecraft": "1.21.10",
    "fabric-loader": "0.19.5"
  }
}
```

由此确定三件事：

1. 包是标准 `.mrpack` 形态；`files` 为空表示全自包含，所有内容都在 `overrides/` 里。
2. **`PCL/` 要从发布包里去掉（用户明确要求）**：PCL 的导出配置 `E:\其他文件\整合包上传\客户端导出配置.txt` 里 `IncludeLauncherCustom:True` 会打包 PCL 个性化内容，6.0 发布前必须关掉它或在导出规则里排除 `PCL/`（附录 A）。因此更新器**不能依赖包内 `PCL/`**：启动器隔离配置由更新器在安装/更新后自己写（§4.2.1）。注意原文档"5.5 实包不含 `PCL/`"的核验结果与用户反馈的发布行为有出入，阶段 0 需要复核 5.5 的 `overrides/` 顶层清单。
3. 整合包版本、MC 版本、Fabric 版本都能从 index 直接读到，**不需要额外的包内元数据文件**；但下载前拿不到，需要 `pack-meta.json`（§5）。

发布链路：`E:\其他文件\整合包上传\upload_modpack.ps1`（183 行）——取目录内第一个 `*.zip`、从文件名提版本、用记事本编辑 `changelog.txt`、复制成 `RSTC-<ver>.zip`、tag 取本地时间 `v<yyyyMMdd-HHmmss>`、经 GitHub REST 建 Release 并带进度条上传。**这条链路是 6.0 改造的落点**（本次不实现，见附录 A）。

### 1.3 触发变化

1. **用户原话**："现在国内头部的 MC 启动器关闭了下载功能"。玩家既拿不到游戏本体与加载器，也没有导入整合包的入口。安装这一环必须由更新器补上。
2. 整合包从 Minecraft 1.21.10 升级到 26.3。Mojang 已改用「年份.序号」版本号。**已核实**：26.3 于 2026-09-15 发布，`javaVersion = {component: java-runtime-epsilon, majorVersion: 25}`，`mainClass = net.minecraft.client.main.Main`。

### 1.4 目标

- 能从空目录装出符合 §4.2 定义的标准 `.minecraft` 结构实例：Minecraft 26.3 + Fabric + 整合包内容。
- 支持从旧实例迁移玩家数据。
- 更新器从 PowerShell 重写为 C# 控制台程序。

**V1 范围**：安装、整包更新、自更新、白名单迁移、关键文件存在性检查。

- 【已确认】整包更新留在 V1，用测试包造"同 MC 的下一版"来验收（q-update-scope）。
- 【已确认】v5 自身的自更新保留（q-selfupdate-scope）。
- 「PCL2 与 HMCL 都能加载整合包 mods」是**阶段 0 必测项**（§9），不是事后抽检。

### 1.5 非目标

- 不做图形界面。
- 不实现启动器功能（不启动游戏、不管理账号）。
- V1 不做增量下载、不做基于文件清单的精细迁移。
- 不下载 Java，只检测（缺失时放行并提示，见 §6.3）。
- 冻结 1.21.10，只维护 26.3。
- 本次不实现整合包发布器（6.0 发布脚本另开任务，见附录 A）。
- 只支持 Windows 10/11。

---

## 2. 已确认的用户体验

> 本节只写用户明确答复过的体验点。流程顺序属于实现建议，标注在各小节末尾。

### 2.1 玩家拿到什么

一个 `.bat` 加程序本体。`.bat` 检查 .NET Framework 4.8：缺失则打开 `https://dotnet.microsoft.com/download/dotnet-framework/net48` 并提示安装后重试；存在则拉起程序。

程序本体的文件形态（单文件 / exe + 若干 DLL）**留给阶段 0 实测决定，实测后回报用户**（§6.7）。

*来源：deploy-mode（选项 1 原文为「依赖系统 .NET Framework 4.8 的小 exe」，用户追加「NET 的时候要能打开 NET 下载页面」）；artifact-shape。*

### 2.2 主界面（讨论示意，非最终视觉稿）

状态面板 + 操作菜单。面板六个字段：游戏目录、本地整合包版本、本地 MC 版本、最新版本、Java 状态、启动器隔离状态（新增，§4.2.1）。

*来源：main-ui。新增字段为本轮技术建议（【技术建议】），因为隔离直接决定能不能玩。*

### 2.3 安装流程

**已确认的体验点**：

- 尽量少让玩家选，能自动就自动（install-steps）
- 完成后给出启动步骤和实例路径（post-install）
- 版本目录名默认「红石镇客户端」、可自定义（version-dir-name）
- 缺 Java 25 时**放行 + 提示**，不阻断（q-java）

**实现建议**：

1. 检测环境（.NET / Java / 候选游戏目录 / 已装启动器）
2. 选游戏目录（多候选时才停；自动扫描 + 手动 + 记住上次）
3. 检查 Java（缺 Java 25 时提示并给下载页，**允许继续**）
4. 镜像自动测速（不在流程中停顿）
5. 最终确认（含空间预检结果）
6. 执行
7. 完成（启动步骤 + 实例路径 + 启动器选择与隔离提示；完成前自动跑一次关键文件自检，把结论直接显示出来）

交互停点三处：目录、Java、确认。空间预检**并入确认页**，不单独停顿——确认页显示「需要 X GB / 可用 Y GB」，不足时该页不可继续。

`PCL2/HMCL` 的隔离配置在**执行阶段**自动写入（§4.2.1），不占用交互停点。

### 2.4 更新流程

**已确认的体验点**：

- 整包覆盖更新（v1-scope；q-update-scope 确认留在 V1）
- 只备份被覆盖的文件（backup-strategy）
- 删除前列出可排除项，玩家可勾选保留（mirror-delete）
- `options.txt` / `servers.dat` 每次发现变化时问玩家（options-handling）

**适用条件**：本地实例的 `dependencies.minecraft` 与目标相同。跨 MC 版本不走此流程，只能安装到新目录。

**实现建议**：

1. 读本地状态 → 2. 取远端发布清单与 `pack-meta.json` → 3. 按发布时间比较 → 4. 确认 → 5. 下载 → 6. 校验 → 7. 解压到实例外的临时目录 → 8. 生成增删清单 → 9. 备份 → 10. 删除 / 写入 → 11. 校验结果（含隔离配置）→ 12. 清理

其中第 8 步同时产出「玩家配置文件变更提示」：包内 `options.txt` / `servers.dat` 与实例内同名文件的哈希不同时，在确认页列出「本次更新修改了 options.txt，是否采用包内版本？」，玩家可选「保留我的」或「用包里的」。

**没有真实输入的阶段**：6.0 发布前没有"同 MC 的下一版"，所以更新流程必须用 `pack-min` / `pack-min-v2` 测试包验收（§9）。

### 2.5 迁移流程

**已确认**：

- 按类别勾选：存档 / 截图 / 地图 / DH 缓存 / 游戏设置与键位，类别内全选（migration-granularity）
- 迁移前警告旧存档可能不兼容，玩家自己决定（save-warning）
- 输出结果清单（migration-ux 的演化）

**迁移范围**：只作用于上述五个类别，**不迁移 `mods/`、`config/`、`PCL/`、`hmclversion.cfg`**（前两者是整合包自带内容，后两者是启动器/机器相关配置）。

**实现建议**：迁移前显示每个类别的体积（DH 缓存/地图可能达数 GB），允许跳过、允许完成后再补；一律复制 + 校验，不移动源文件。

*来源：big-version-layout（「独立 + 可选数据迁移」）；不迁移启动器配置是本轮技术建议（【技术建议】）。*

### 2.6 实例目录命名

默认「红石镇客户端」，安装时可自定义。同名已存在时提示改名，不覆盖。

*来源：version-dir-name。*

### 2.7 启动器隔离（新增，已确认方向）

【已确认】PCL2 与 HMCL **都要正确加载整合包 mods**（q-launcher-scope 选项 2）。落地方式见 §4.2.1：**发布包不含 `PCL/`**，由更新器在安装/更新后写入最小启动器配置（PCL2 `PCL/Setup.ini`、HMCL `hmclversion.cfg`），并在两个启动器里各启动一次验收（A1b）。

---

## 3. 界面、状态与操作

### 3.1 状态面板（六个字段）

| 字段 | 取值示例 |
|---|---|
| 游戏目录 | `D:\Minecraft\.minecraft` / 「未定位」 |
| 本地整合包版本 | `5.5` / 「未安装」 / 「状态文件缺失」 |
| 本地 MC 版本 | `1.21.10` / 「未知」 |
| 最新版本 | `6.0`（Minecraft 26.3） / 「无法检查更新」/「清单来自缓存」 |
| Java | 「Java 25 已就绪」/「需要 25，当前最高 21」/「未检测到任何 Java」 |
| 启动器隔离 | 「PCL2 已启用」/「HMCL 已启用」/「未检测到启动器配置」 |

### 3.2 菜单项与可用条件

| 菜单项 | 可用条件 | 不可用时 |
|---|---|---|
| 安装到新目录 | 已定位游戏目录 | 灰显「未定位游戏目录」 |
| 更新现有实例 | 本地实例存在 **且** 本地 MC == 目标 MC | 灰显「本地为 1.21.10，新版本基于 26.3，请用安装到新目录」 |
| 从旧实例迁移 | 新旧实例同时存在 | 灰显「未同时检测到新旧实例」 |
| 检查关键文件 | 本地实例存在 | 灰显「本地实例不存在」 |
| 检查更新器更新 | 总是 | — |
| 设置 | 总是 | — |
| 退出 | 总是 | — |

**目标 MC 未知时的降级**（新增）：拿不到 `pack-meta.json` 且未缓存时，「更新现有实例」灰显为「无法确定目标版本，请检查网络后重试」，不猜测。

菜单名用「检查关键文件」而非「校验完整性」，因为 V1 只查存在性（v1-verify）。

### 3.3 加载、中断与失败

**中断**：Ctrl+C 第一次触发回滚，第二次强制退出；回滚过程中忽略新的 Ctrl+C。

**回滚三态**：

| 结果 | 文案 |
|---|---|
| 成功 | 「已回滚到操作前状态」 |
| 失败 | 「回滚未完成，请从 `<备份路径>` 手动恢复」，并保留现场 |
| 无备份可回滚 | 「无法回滚，请从 `<备份路径>` 手动恢复」 |

**失败场景表**：

| 场景 | 检测方式 | 提示要点 | 可继续 | 回滚 |
|---|---|---|---|---|
| 磁盘空间不足 | 确认页计算（§4.6） | 需要 X GB / 可用 Y GB，分项显示 | 否 | 无需 |
| 下载校验失败 | sha256 或大小不符 | 指出具体文件、重试次数 | 否 | 是 |
| 镜像全部失败 | 逐文件重试后 | 列出失败镜像与最后错误 | 否 | 是 |
| 文件被占用 | 写入时 IOException | 「请关闭游戏与启动器后重试」 | 否 | 是 |
| 权限不足 | UnauthorizedAccessException | 建议换目录或以管理员运行 | 否 | 是 |
| 杀软拦截 | 下载后文件消失 / 大小突变 | 提示加入白名单 | 否 | 是 |
| 路径含中文/空格 | 不拦截 | 正常处理 | 是 | — |
| GitHub API 403 / 限流 | HTTP 状态码 | 提示稍后重试；有缓存时用缓存并标注时间 | 否 | 无需 |
| 只有 Java 21 | 检测 | 「需要 Java 25，当前 21」+ 下载页 + 放行（q-java） | **是** | — |
| 关键文件缺失 | 检查 | 列出缺失项 | 否 | — |
| 本地状态文件损坏/缺失 | 读取失败 | 给出诊断；有 `RSTC_version.txt` 时按 Tag 反查，否则允许「覆盖安装」 | 视情况 | — |
| 多个旧实例 | 扫描 `versions/*/RSTC_version.txt` | 让玩家选一个，按 published_at 排序标注 | 是 | — |
| 启动器配置写失败 | 写入异常 | 不影响安装完成，完成页给出手动设置指引 | 是 | — |
| 目标 MC 无法确定 | 无 `pack-meta.json` 且无缓存 | 禁止就地更新，只允许装新目录 | 视情况 | — |

### 3.4 设置

| 项 | 状态 |
|---|---|
| 下载镜像自动测速 | 已确认 |
| 默认游戏目录（记住上次） | 已确认 |
| 手动指定镜像 | 技术建议，未获确认（保留为高级项） |
| 退出前保留窗口 | 技术建议，未获确认 |
| 备份保留最近 3 次 | 已确认（backup-retention） |
| 下载并发数（8–16） | 技术建议（§4.5） |
| 测速结果缓存 24h | 技术建议（§4.5） |

配置文件位置：`%APPDATA%\RSTC-Updater\config.json`（【技术建议】；装在 Program Files 也能写）。

### 3.5 键盘操作

方向键移动、Enter 确认、Esc 返回、Ctrl+C 中断。

---

## 4. 数据、写入与回滚

### 4.1 外部依赖清单（新增）

| 依赖 | 地址 | 用途 | 已核实状态 |
|---|---|---|---|
| 整合包仓库 | `https://github.com/Mashiro-Neri/Redstone-Town-Client_update_modpack` | Releases API、资产下载 | 8 个 release；资产名 `RSTC-<版本>.zip`；全部带 sha256 `digest` |
| 更新器仓库 | `https://github.com/Mashiro-Neri/RSTC-Updater` | 自更新 | v4.11 资产 `update_modpack.ps1` + `.bat`；tag `v4.11` |
| 资产选择规则 | 正则 `^RSTC-(?<ver>.+)\.zip$` | 选包 | 5.5 实测 |
| tag 规则 | 整合包 `v<本地时间戳>`；更新器 `v<major.minor>` | 只用 `published_at` 排序 | 实测 |
| 远端元数据 | Release 资产 `pack-meta.json` | 下载前拿 versionId / MC / Fabric / size / sha256 | 规划中（§5、附录 A） |
| GitHub API | `https://api.github.com` | 清单 | 未认证 60 次/小时，需 ETag + 缓存 |
| Mojang | `https://piston-meta.mojang.com` | 26.3 version json / jar / libraries / assets | 实测 200 |
| BMCLAPI | `https://bmclapi2.bangbang93.com` | 镜像 | 版本清单、26.3 version json、Fabric profile 均 200；`/assets/34.json` 404（资源索引用官方或另测） |
| Fabric Meta | `https://meta.fabricmc.net` | profile json | 实测：7 库 + `arguments.jvm` |
| NuGet | `CmlLib.Core 4.1.0`、`Spectre.Console 0.57.2` | 安装与控制台 UI | 均有 `netstandard2.0` 组（§6.1） |

**限流（403）时**使用最近一次成功获取的缓存清单，并在界面标注「清单来自缓存，时间 YYYY-MM-DD」。缓存目录与格式见 §7.2（开发时决定）。

### 4.2 实例结构、实例 json 与状态文件

**目录结构**（目录名 = 实例名 = 玩家可见名称）：

```
.minecraft/
├── versions/
│   ├── <实例名>/                       ← 玩家自定义，默认「红石镇客户端」
│   │   ├── <实例名>.json               ← 平铺自包含（方案 A，见下）
│   │   ├── <实例名>.jar                ← A2：26.3 client.jar 的副本；A1：不复制，jar 指 "26.3"
│   │   ├── mods/  config/  options.txt  servers.dat ...
│   │   ├── PCL/Setup.ini               ← 更新器写入的 PCL2 版本配置（隔离 True）
│   │   ├── hmclversion.cfg             ← HMCL 版本配置（gameDirType）
│   │   ├── modrinth.index.json         ← 本地版本状态（canonical，安装时复制）
│   │   └── RSTC_version.txt            ← v4 兼容状态（Tag）
│   └── 26.3/                          ← A2 可选（保留作原版回退）；A1 必需
├── libraries/  assets/                ← 共享
└── rstc-backup/<时间戳>/               ← 备份，在实例目录之外
```

**版本列表说明**：启动器会同时列出 `26.3`（A1/A2 保留的原版回退）与 `<实例名>`；完成页必须写明「启动请选 <实例名>」，避免玩家误启动原版。

**实例 json 三种方案**（阶段 0 二选一定稿，默认 A2）：

| 方案 | inheritsFrom | libraries | jar | 说明 |
|---|---|---|---|---|
| **A2 自包含平铺（默认）** | 不写 | 26.3 的 114 个 + Fabric 7 个 + `intermediary:0.0.0` | 复制 41.5 MB client.jar 到实例目录，`jar` = 实例名 | 与现网两个可用实例（`红石镇客户端.json`、`26.3-Fabric.json`）完全同形，最稳 |
| **A1 薄 jar** | 不写 | 同上 | `jar` = `"26.3"`，不复制 jar | 省 41.5 MB，依赖 `versions/26.3/`，需双启动器实测 |
| **B 继承式** | `"26.3"` | 只写 Fabric 7 个 + intermediary | 不写 | 最省，但两级继承在 PCL2/HMCL 下未验证；阶段 0 通过后才允许切换 |

**已核实的现网实况**：`26.3-Fabric.json` 是 122 个库、自带 41,483,720 B jar、无 `inheritsFrom`、无 `FabricMcEmu`；`红石镇客户端.json` 同为自包含形态。文档旧版写的"`inheritsFrom=26.3` + 合并 Fabric 与原版库表"两者互相冗余，已按上表改写。

**`arguments` 规则**：照抄 26.3 的 `game` 与 `jvm`，并追加 Fabric profile 里的 `-DFabricMcEmu= net.minecraft.client.main.Main`（实测存在；是否必需见 §7.1）。**不要省略**，也不要凭空发明参数。

**状态文件**（双写）：

- `modrinth.index.json`：canonical，读取 `versionId`、`dependencies.minecraft`、`dependencies.fabric-loader`。
- `RSTC_version.txt`：v4 兼容，内容为 Release Tag（v4 的读法见 `update_modpack.ps1:192–204`）。
- **v4 兼容读取**：实例目录若存在 `RSTC_version.txt`，按「Tag → 发布清单中该 Tag 对应 Release 的 `published_at`」映射；映射失败则显示「状态文件缺失」并禁用就近更新。已核实 v4 实例确实会写这个文件（本机测试实例内容为 `v20260712-103024`，对应 5.4.5）。
- **旧实例发现**：扫描 `versions/*/RSTC_version.txt`；多个候选时按 published_at 倒序列出，让玩家选（不自动挑）。

**日志**：游戏目录下 `update_log_<时间戳>.txt`，含每阶段耗时、所选镜像、重试次数、已写 N/待写 M、回滚动作。
**备份**：`.minecraft/rstc-backup/<时间戳>/`，含被覆盖与被删除的文件；**保留最近 3 次**，超出删除最旧。v4 的 `_modpack_backup/` 只读兼容，不删除、不迁移。

### 4.2.1 启动器隔离配置（新增）

包内 `mods/ config/ ...` 写在实例目录下，**只有启动器启用版本隔离才会被加载**。两个启动器各有自己的配置文件：

| 启动器 | 文件 | 关键字段 | 证据 |
|---|---|---|---|
| PCL2 | `versions/<实例名>/PCL/Setup.ini` | `VersionArgumentIndieV2:True`（旧版另需 `VersionArgumentIndie:1`），并保留 `VersionVanillaName`、`VersionFabric`、`Info`、`ReleaseTime` 等 | 本机测试实例为此形态；源仓库也有同名文件，但**发布包将不再包含 `PCL/`**，改由更新器写入 |
| HMCL | `versions/<实例名>/hmclversion.cfg`（JSON） | `gameDirType: 1`（版本目录）；`usesGlobal` 等其余字段保持原值 | WeeAxe 1.21.10 包实测；枚举值待阶段 0 确认 |

**写入策略**：

1. **不随包发布**：发布包禁止包含 `PCL/`（用户要求，附录 A）；更新器也不把 `PCL/` 当作同步内容。
2. **更新器写入**：安装/更新后由更新器生成或补写配置。`PCL/Setup.ini` 只写最小必要字段（至少 `VersionArgumentIndieV2:True`，旧版 PCL 另加 `VersionArgumentIndie:1`），文件已存在时保留玩家原有字段、只补隔离键；`hmclversion.cfg` 缺失时补写最小 JSON，**已存在时只读不改**（玩家的内存/Java 设置不能被覆盖）。最小字段集待阶段 0 实测（§7.1）。
3. 写失败不算安装失败：完成页降级为「请手动在启动器里开启版本隔离」并给出路径。

**验收**：A1b 必须在 PCL2 与 HMCL 里各启动一次，并确认游戏内 mod 生效。

### 4.3 同步语义（白名单镜像）

**只对包 `overrides/` 里出现过的顶层路径做镜像。** 5.5 实包的顶层路径为 `config/`、`mods/`、`options.txt`、`servers.dat`；6.0 的顶层路径以实际包为准。**`PCL/` 一律不参与镜像**——即使包里出现也跳过、不写入、不删除（用户要求发布包不含 `PCL/`；玩家本地的 `PCL/` 属于启动器配置）。

规则：

- 对每个顶层路径：目标目录中该路径下、新包没有的文件 → 删除。删除前先备份、先生成清单、玩家可勾选保留。
- **不在包内的路径一律不动。**
- `options.txt` / `servers.dat` 不直接覆盖：先比对哈希，不同则在确认页问玩家（options-handling）。
- `PCL/` 与 `hmclversion.cfg` 是更新器生成的本地配置（§4.2.1），不参与「删除清单」，也不被包内容覆盖。

**永不删除集合**（即使将来出现在包内也不参与删除）：

`saves/`、`screenshots/`、`logs/`、`crash-reports/`、地图类目录（`xaero/`、`journeymap/`、`XaeroWaypoints*`）、Distant Horizons 数据（`Distant_Horizons_server_data/`、`DistantHorizons.toml`）、`_modpack_backup`、`update_log_*`、`modrinth.index.json`、`RSTC_version.txt`、`<实例名>.json`、`<实例名>.jar`、`.minecraft/rstc-backup/`。

**回滚三类**：被覆盖 → 从备份还原；被删除 → 从备份还原；新建 → 按写入清单删除。

**journal**：每一步写入或删除前先把计划落盘；程序启动时检测到未完成操作，给出处置入口。

### 4.4 下载与完整性（新增）

| 项 | 规则 |
|---|---|
| 校验来源 | Release 资产 `digest`（sha256，已确认存在）+ `pack-meta.json` 的 sha256；两者不一致时以 meta 为准并告警 |
| 校验时机 | 下载完成后校验一次；解压时由 ZIP CRC 兜底（全量读取每个条目） |
| 断点续传 | 服务器支持 `Range`（已实测 `Accept-Ranges: bytes`）；单文件失败后从已下载偏移继续 |
| 大文件下载 | 580 MB 单包可分 4–8 段并行 Range（【技术建议】，§6.5 验证） |
| 小文件下载 | 资源对象 5149 个、库 114 个，限流 8–16 并发 |
| 镜像回退 | **按文件回退**：单文件失败立即换下一个镜像重试，不整流程重来 |
| 复用已有文件 | 目标目录已有同大小且哈希一致的原版 jar / 库 / 资源对象时跳过下载（最多省 646 MB） |
| 临时目录 | `.minecraft/.rstc-tmp/<时间戳>/`；成功后清理，失败保留供重试，过期 7 天清理 |

### 4.5 空间预检（已核实数值）

| 项 | 大小（实测，2026-11-11） |
|---|---|
| 26.3 client jar | 41,483,720 B（约 39.6 MiB） |
| 26.3 libraries | 74 个 jar / 83.7 MiB（按 Windows 客户端规则过滤；版本 json 共 114 个 artifact，含其他平台/服务端） |
| 26.3 assets | 5149 个对象 / 465.4 MiB（索引 `34`） |
| 原版合计 | **588.9 MiB / 5228 个文件**（无 `runtime/`，确认未下载 Java） |
| 整合包 zip | 609,837,724 B（581.6 MiB） |
| 包展开后 | 约 580 MiB |
| 备份（只备份被覆盖/删除的文件） | 最坏约 580 MiB |

**峰值**（zip + 解压 + 备份 + 原版同时存在）约 **2.3 GiB**；稳态约 1.15 GiB。预检阈值取 **3 GiB**，确认页分项显示，并按目标卷（不是系统盘）的剩余空间判定。

**实测**：全新安装 26.3 原版耗时 2 分 29 秒、新增 588.9 MiB，4 项检查全部通过（版本 json、client jar、asset index 5149 对象、74 个库）；同目录 warm 复跑 3.6 秒、新增 0.0 MiB，证明跳过逻辑有效。

### 4.6 隐私

不采集任何遥测，不上传玩家数据。

---

## 5. 元数据与素材

### 5.1 `pack-meta.json`（新增，发布侧产出）

放在整合包 Release 里的**独立小资产**（不是包内文件，不违背"包内不加元数据"的结论）：

| 字段 | 类型 | 示例 | 用途 |
|---|---|---|---|
| `tag` | string | `v20260922-001655` | 与 Release 对应 |
| `versionId` | string | `5.5` | 面板显示 |
| `minecraft` | string | `1.21.10` | 安装/更新分支判定 |
| `fabricLoader` | string | `0.19.5` | Fabric profile 请求 |
| `assetName` | string | `RSTC-5.5.zip` | 资产选择校验 |
| `size` | number | `609837724` | 空间预检、下载校验 |
| `sha256` | string | `cda10e…` | 完整性校验 |

**生成方式**：由整合包发布器在打包时生成（附录 A，后续任务）。在发布器到位前，可用 `gh release upload <tag> pack-meta.json` 手动补一个几百字节的文件；再退一步，用 Range 读 zip 尾部中央目录 + `modrinth.index.json` 兜底（已实测可行，但拿不到 sha256，只能靠 `digest`）。

### 5.2 其他

- Java 版本要求由更新器内置映射表提供：`26.3 → 25`（已核实）。映射按 Mojang `version_manifest_v2` 的 `javaVersion` 生成，可更新。
- 不需要账号。
- 无图标素材需求（控制台程序）。

---

## 6. 技术建议与取舍

### 6.1 技术选型（库名已确认，版本号为建议）

【已确认】C# + CmlLib.Core + Spectre.Console（stack-confirm）；部署依赖系统 .NET Framework 4.8（deploy-mode）。

| 库 | 已核实 | 用途 |
|---|---|---|
| CmlLib.Core 4.1.0 | 有 `lib/netstandard2.0`；依赖 CmlLib.Core.Commons 4.0.0、LZMA-SDK 19.0.0、SharpZipLib 1.4.2、System.Text.Json 8.0.5、System.Threading.Tasks.Dataflow 8.0.1；DLL 内确有 `LibraryServer`、`AssetServer`、`IVersionLoader`、`FabricInstaller` | 原版安装、版本解析、可替换的下载服务器 |
| Spectre.Console 0.57.2 | 有 `netstandard2.0` 组；依赖 Spectre.Console.Ansi、Microsoft.Bcl.TimeProvider 10.0.8、System.Memory 4.6.3 | 控制台界面 |

**GitHub 上 CmlLib 只有 v4.0.4 tag**，4.1.0 源码不可对照，接口细节以 NuGet 包内 DLL 与阶段 0 spike 为准。

**net48 产物代价**（已核实依赖树）：`System.Text.Json` + `System.Text.Encodings.Web` + `System.Memory` + `System.Buffers` + `System.Numerics.Vectors` + `System.Runtime.CompilerServices.Unsafe` + `Microsoft.Bcl.AsyncInterfaces` + `System.Threading.Tasks.Extensions` + `Microsoft.Bcl.TimeProvider` + SharpZipLib + LZMA-SDK（其 `SevenZip.dll` 为**托管 AnyCPU，非原生**；PE 检查 ILOnly）。**阶段 0 实装产物**：`RstcUpdater.exe` + `RstcUpdater.exe.config`（binding redirects）+ 16 个 DLL，共 19 个文件。

**CmlLib 4.x 实际 API（源码核对 master，2026-11-11）**：`MinecraftLauncher.InstallAsync(versionName, ct)`；`MinecraftLauncherParameters.CreateDefault` 默认使用 `MojangJsonVersionLoaderV2` + `ParallelGameInstaller.CreateAsCoreCount`（**默认并行下载**，并发 = CPU 核数）+ `DefaultFileExtractors`（含 `JavaFileExtractor`，见 §6.3）。

**本项目自研的三块**（CmlLib 不提供或不打算用）：

1. Fabric 安装（§6.2）
2. Java 检测（§6.3）
3. 整合包铺开、白名单镜像与迁移——CmlLib 核心包不含 mrpack / CurseForge 导入能力

**未采纳的替代方案**：Node + `@xmcl/*` 打单 exe；纯 C# 自实现安装逻辑。

### 6.2 Fabric 安装

按 `https://meta.fabricmc.net/v2/versions/loader/<mc>/<loader>/profile/json` 取 profile。**26.3 + 0.20.1 实测返回**：`id = fabric-loader-0.20.1-26.3`、`inheritsFrom = 26.3`、`mainClass = net.fabricmc.loader.impl.launch.knot.KnotClient`、`arguments.jvm = ["-DFabricMcEmu= net.minecraft.client.main.Main "]`、**7 个 libraries**（asm ×5、sponge-mixin、fabric-loader，**不含 intermediary**）。

但现网可用的 `26.3-Fabric.json` 有 **8 个 Fabric 库**，多一个 `net.fabricmc:intermediary:0.0.0`（572 B 占位 jar）。安装时按 8 个补齐，避免与现网实例产生未知差异。

不使用 CmlLib 内置 FabricInstaller 的原因：它不走 `LibraryServer`，且强制用原生 id 建目录（DLL 中存在 `FabricInstaller`，但无法从 4.1.0 源码核对行为，阶段 0 可顺带验证是否可复用）。

### 6.3 Java 检测

扫描范围：`.minecraft/runtime`、注册表、`Program Files` 下常见 JDK 目录、`JAVA_HOME`、`PATH`。记录版本号与位数，供状态面板显示。

**缺失策略（已确认 q-java）**：只提示不阻断。面板持续显示「需要 25，当前最高 21」+ 下载页；安装继续。理由：PCL2/HMCL 对现代版本通常会自动补对应运行时，硬拦会造出"能装但不让装"的死路（启动器自动补 Java 的行为由 A1b 实测确认）。

CmlLib 内置 `MinecraftJavaPathResolver` 只枚举 `.minecraft/runtime`，不够用。

**安装器默认会下 Java（重要）**：`DefaultFileExtractors` 含 `JavaFileExtractor`，`InstallAsync` 会顺带安装 Mojang 的 `java-runtime-epsilon`（26.3 → Java 25）。这违反"不下载 Java，只检测"，实现时必须显式移除该 extractor（阶段 0 已按此实现：自建 `MinecraftLauncherParameters`，把 `DefaultFileExtractors.Java` 置空）。

### 6.4 版本与分支判定

- **新旧**：按 Release 的 `published_at`，不按版本号字符串（6.4.7 那条 Release 内容实为 5.x，已核实存在：tag `v20260719-191154`）。
- **安装 / 更新分支**：比较本地 `dependencies.minecraft` 与 `pack-meta.json` 的 `minecraft`；不同 → 只能装到新目录；相同 → 允许就地更新。
- **本地版本缺失或损坏** → 禁用就地更新并给诊断；有 `RSTC_version.txt` 时按 Tag 反查 `pack-meta`/发布清单。
- **目标版本未知** → 不猜测（§3.2）。

### 6.5 镜像（两层，不能合并）

1. **GitHub 资产**：沿用 v4 的 5 个镜像与测速参数（预热 128 KB、采样上限 3 MB / 2 秒、重试 3 次），并改为**按文件回退**。
2. **Mojang / Fabric 资源**：Mojang 官方 + BMCLAPI（已实测 26.3 version json 与 Fabric profile 可用；资源索引 `/assets/34.json` 404，需用官方或另测其他路径）。

**本机实测（2026-11-11）**：官方 client.jar 8 MB Range = 1.35 MB/s；BMCLAPI 跟随 302 后仅 67 KB/s（30 s 只下 2.03 MB），且 `/assets/34.json` 404。结论：**默认官方源，BMCLAPI 仅作失败备用，且不用于索引**。

接法：替换 `LibraryServer` / `AssetServer`，并用自定义 `IVersionLoader` 与 `HttpClient` 改写 URL。**只影响安装期的下载，不改共享的 `26.3.json`，也不把镜像 URL 写进实例 json**——原方案"改实例 json 保留原始 URL 作为回退"在启动器侧不成立（启动器失败即失败，没有逐 URL 回退），已删除。

【技术建议】单包 4–8 段 Range 并行、小文件 8–16 并发、测速结果缓存 24h（§4.4）。

### 6.6 自更新

运行中的 exe 无法自我替换。做法：下载到临时目录 → 生成临时 `.bat` 等主进程退出 → 替换文件 → 重启；失败时保留旧文件。

【已确认】v5 自身保留自更新（q-selfupdate-scope）。

**v4 → v5 过渡**（【已确认】q-transition：不自动搬，发公告手动换）。注意 v4.11 的实现细节：`Get-UpdaterRelease()` 只有在 Release **同时含 `update_modpack.ps1` 与 `.bat`** 时才返回结果，否则返回 null，界面显示「无法获取更新器版本信息」。因此：

- 若 v5 发在同一仓库且只带 exe，老用户点更新会看到这个错误而不是升级提示；
- 规避方式（任选，文档按公告方案写）：① 公告 + Release 说明里给手动下载步骤；② v5 Release 里仍放两个占位资产（ps1/bat 只打印"v5 已改为独立程序，请下载 xxx"）；③ v5 换仓库发布。
- 实现时在 §8 E1/E2 之外单列"v4 用户手动迁移"的验收（见 A0-7）。

### 6.7 产物形态（阶段 0 定）

候选：exe + 若干 DLL（默认）或 Costura/ILMerge 单文件。**阶段 0-1 实测**：产物 = `RstcUpdater.exe`（安装模式加入后约 25 KB，精确值以证据 `SHA256SUMS.binary.txt` 为准）+ `RstcUpdater.exe.config` + 16 个 DLL（含托管 `SevenZip.dll`），19 个文件；未发现原生 x86/x64 DLL。单文件（Costura/ILMerge）未实测；`.exe.config` 的 binding redirects 在 .NET Framework 下是必需的，合并需额外处理。产物身份用 `ProductVersion`（内嵌 commit）+ SHA256 记录，**不保证字节级可复现**（同源码不同环境重建 hash 可能不同，已知限制）。杀软误报率未测。

---

## 7. 待确认项与开发时决定

### 7.1 待实测（阶段 0 关闭，不冒充结论）

| 项 | 为什么还没定 | 影响 |
|---|---|---|
| HMCL `gameDirType` 的枚举值与最小 cfg 形态 | 只实测到 `1` = 版本目录这一例 | HMCL 隔离写入 |
| PCL `Setup.ini` 的最小字段集 | 现网值里 `VersionVanilla:21.0.10` 形态可疑，需确认 PCL 的生成规则 | 兜底补写是否安全 |
| 5.5 导出包里到底有没有 `PCL/` | 原文档核验为不含，用户反馈发布流程会自带；两者需要复核 | 发布侧改造的基线 |
| `jar:"26.3"`（A1）与复制 jar（A2）的启动器兼容 | 未在 PCL2/HMCL 实测 | 省 41.5 MB 能否成立 |
| `-DFabricMcEmu` 是否必需 | 官方 profile 有、PCL 实例没有 | 参数取舍 |
| 单包 4–8 段 Range 并行的实际提速 | 未压测 | 是否进 V1 |
| 杀软对未签名 exe 的误报率 | 未测 | 分发方式与提示 |
| 6.0 真实包结构与体量 | 6.0 未发布 | 验收补跑 |

### 7.2 开发时决定

配置文件格式与位置（建议 `%APPDATA%\RSTC-Updater\config.json`）、日志格式、「关键文件」的具体清单、单文件合并手段、镜像候选清单、CLI 参数（是否保留 `--mc-root` 等）。

### 7.3 本轮关闭的决策（来源见附录 C）

| 决策 | 结论 |
|---|---|
| 启动器 | PCL2 + HMCL 都要正确加载 mods |
| 缺 Java 25 | 放行 + 提示 |
| 整包更新 | 留在 V1，用测试包验收 |
| v4→v5 | 手动过渡 + 公告 |
| v5 自更新 | 保留 |
| 远端元数据 | `pack-meta.json` |
| 6.0 打包 | 写脚本自建 mrpack + 自动发布（后续任务） |
| 发布器排期 | 本次不做，先完成 v5 |
| 文档位置 | 仓库 `requirements/` |
| 发布包 `PCL/` | **不含**（用户纠正；隔离配置改由更新器写入） |
| 测试床 | 本机 `26.3-Fabric` 试验田可用 |
| 库数量验收口径 | 74（Windows 客户端规则过滤后的 artifact 数；用户确认 q-accept-74） |

---

## 8. 可验证的完成标准

| 编号 | 操作 | 期望结果 | 环境 | 通过阈值 |
|---|---|---|---|---|
| A0 | 阶段 0 spike（§9） | 全部通过标准达成 | 本机 | 每项有命令与输出 |
| A1 | 空目录安装（`pack-min` 26.3 → 再跑 5.5 真实包） | `versions/<名>/<名>.json` 存在，`id`/`mainClass`/`arguments` 正确；`libraries/` 含 Fabric 7+1 与 Mojang 114；`assets/indexes/34.json` 存在；`mods/`、`config/` 存在，且更新器已写入含隔离键的 `PCL/Setup.ini` | Win10/11 + .NET 4.8 + 网络 | 可脚本化判定 |
| A1b | 用 PCL2 与 HMCL 各启动一次 | 两个启动器都能列出该版本并进入游戏，mod 生效（游戏内 `/mods` 或日志含 mod 加载数） | 人工 | 两家都要过；不过则 §4.2 方案重选 |
| A2 | 已装 Java 25 | 面板显示「Java 25 已就绪」 | 本机 | 不误报缺失 |
| A3 | 只装 Java 21 | 显示「需要 25，当前 21」+ 下载页，**且安装可继续** | 需构造环境 | 不静默跳过、不阻断 |
| A4 | 缺 .NET 4.8 的机器双击 bat | 打开 .NET 下载页 | 用测试钩子模拟 | 不闪退、不乱码 |
| A5 | 目标目录已有同名实例 | 提示改名，不覆盖 | 本机 | 原实例文件哈希不变 |
| A6 | 实例内预置 `saves/test.dat` 与玩家自加 `mods/mytest.jar`，执行更新 | `saves/test.dat` 保留；`mytest.jar` 出现在删除清单且默认勾选删除，取消勾选后保留 | `pack-min` → `pack-min-v2` | 两种结果都符合预期 |
| A7 | 包内 `options.txt` 与实例内不同 | 确认页出现「本次更新修改了 options.txt」并给出二选一 | 同上 | 两个选项各自生效 |
| A8 | 安装完成后检查两个启动器配置 | `PCL/Setup.ini` 含 `VersionArgumentIndieV2:True`；`hmclversion.cfg` 的 `gameDirType` 存在且未覆盖玩家其他字段 | 本机 | 只补不改 |
| B1 | 本地 1.21.10 时点「更新现有实例」 | 灰显并说明原因 | 同上 | 不可进入覆盖流程 |
| B2 | 迁移选「存档」类别 | 目标出现 `saves/`，源未变；`PCL/`、`hmclversion.cfg` 未被迁移 | 同上 | 逐文件比对 |
| B3 | 迁移看到警告后取消 | 源与目标均无变化 | 同上 | 目录哈希不变 |
| C1 | 本地 5.5、远端 6.0 | 面板显示 5.5 → 6.0，目标 MC 26.3 | 同上 | 可进入安装流程 |
| C2 | 本地是 6.4.7 那个错误版本 | 按发布时间判为旧，提示可升级 | 同上 | 不显示「已最新」 |
| C3 | 本地状态文件缺失但存在 `RSTC_version.txt` | 按 Tag 反查并显示版本；两者都无 → 给诊断并禁止就地更新 | 同上 | 不误判为最新 |
| D1 | 下载中断后重跑 | 已下载部分按 Range 续传；未完成写入按 journal 清理，旧文件未被破坏 | 同上 | 重跑后状态与中断前一致 |
| D2 | 删除清单预览 | 列出待删文件，可勾选保留 | 同上 | 保留项不被删 |
| D3 | 备份目录位置与保留 | 位于 `.minecraft/rstc-backup/`，不在实例目录内；超过 3 次时最旧的被清理 | 同上 | 更新 4 次后只剩 3 份 |
| D4 | 复用已有文件 | 第二次安装到同一 `.minecraft` 时原版 jar/库/资源不再下载 | 本机 | 网络流量显著下降 |
| E1 | v5 自更新成功 | 下载、替换、重启，版本号变化 | 同上 | 能正常启动 |
| E2 | v5 自更新失败 | 旧 exe 仍可运行 | 同上 | 文件哈希不变 |
| F1 | 解压恶意/异常 zip（`../`、绝对路径、CP936 文件名、超长路径） | 拒绝越界条目；文件名不乱码；长路径可写入 | 构造样本 | 不写出目标目录、不崩溃 |

---

## 9. 阶段 0：前置验证（spike，新增）

所有 spike 通过后才进入正式实现。每项必须附命令与输出。

| # | Spike | 通过标准 |
|---|---|---|
| 0-1 | .NET Framework 4.8 控制台 + CmlLib 4.1.0 + Spectre 0.57.2，安装 26.3 原版 | 能装出 `versions/26.3/26.3.json`、client jar = 41,483,720 B、`assets/indexes/34.json`（5149 对象）、`libraries/*.jar` ≥ 74（Windows 客户端规则：114 个 artifact 中 40 个属其他 OS/native，被规则拒绝；用户确认 q-accept-74）；产物列出全部 DLL；中文界面不错位；证据绑定 commit |
| 0-2 | 实例 json 三方案（A2/A1/B）各做一份 | PCL2 与 HMCL 都能列出并启动；记录哪份能启动、启动日志差异 |
| 0-3 | 隔离配置写入（不依赖包内 `PCL/`） | PCL2 读到 `VersionArgumentIndieV2:True` 后 mod 生效；HMCL 读到 `gameDirType` 后 mod 生效；已存在的配置不被破坏；包内无 `PCL/` 时新装实例也能生效 |
| 0-4 | 5.5 真实包安装 | 完整跑通下载→校验→解压→写入→自检；记录耗时与峰值空间 |
| 0-5 | `pack-min` / `pack-min-v2` 更新链路 | A6/A7/D1/D2/D3 全部可执行 |
| 0-6 | 复用已有文件与并行下载 | 第二次安装流量下降；并行下载有可量化提速或明确放弃 |
| 0-7 | v4 用户手动迁移路径 | 按公告步骤能从 v4.11 换成 v5，旧实例数据不丢 |

**测试素材**（不等 6.0）：

1. `RSTC-5.5.zip`（真实包，1.21.10）——验证完整安装链路；本机需先装 Java 21；
2. `pack-min` / `pack-min-v2`（手工构造，26.3，几个文件，增删各一处）——验证更新/删除清单/保留勾选；
3. 本机 `versions/26.3-Fabric`（6.0 试验田，121 mods）——结构对照与复用验证，**只读，不修改**。

### 9.1 进度（2026-11-11）

| 项 | 状态 | 证据 |
|---|---|---|
| 0-1 工具链 + 元数据探针 | 已通过独立复跑（第 1 轮审查 FAIL 的 R-02/R-03/R-06/R-07/R-08 已修） | `evidence/stage0/net48-probe/085bb8f…/`（commit、SHA256SUMS、facts） |
| 0-1 真实安装 26.3 | **已实现，待第 3 轮审查确认**（L2，用户已确认范围：仅临时目录）：cold 2 分 29 秒 / 588.9 MiB，warm 3.6 秒 / 新增 0.0 MiB，两次校验均 PASS | `evidence/stage0/install-26.3/e5b700f…/20261111_211437/`；临时目录 `evidence/stage0/test-minecraft-20261111_211436/` |
| 0-2 实例 json 三方案 | 未开始 | — |
| 0-3 隔离配置写入 | 未开始 | — |
| 0-4 5.5 真实包安装 | 未开始 | — |
| 0-5 测试包更新链路 | 未开始 | — |
| 0-6 复用/并行 | 部分（CmlLib 默认并行已确认，实测待 0-1 warm 复跑） | — |
| 0-7 v4 手动迁移 | 未开始 | — |

**阶段 0-1 修正的设计假设**：① CmlLib 默认安装器会下 Java，必须移除 `JavaFileExtractor`；② `SevenZip.dll` 是托管 DLL，不是原生；③ BMCLAPI 实测比官方慢，只作备用；④ 产物是 exe + config + 16 DLL，"单文件"未验证。

---

## 附录 A：发布侧需求（本次不实现，只写契约）

> 用户已决定：先把 v5 更新器做完，再做 6.0 发布器；中途需求变化不至于把发布器定死（q-publish-scope）。本附录只写更新器依赖的接口约定。

1. **发布包去掉 `PCL/`（用户要求，优先）**：在 `E:\其他文件\整合包上传\客户端导出配置.txt` 里把 `IncludeLauncherCustom` 设为 `False`，并在导出规则里显式排除 `PCL/`；产物 zip 的 `overrides/` 顶层不得出现 `PCL/`。更新器不依赖它，隔离配置自己写（§4.2.1）。
2. **自建 mrpack + 自动发布（后续任务）**：目标是把"PCL 导出 → 手动上传"换成"脚本打包 → 校验 → 上传"，并顺带产出 `pack-meta.json`。范围包括：overrides 白名单、`PCL/` 必不含校验、资产改名 `RSTC-<ver>.zip`、tag `v<本地时间戳>`、`pack-meta.json`、sha256 自检。
3. **`pack-meta.json`**：字段见 §5.1；由发布器生成并作为 Release 的第二个资产上传（现有 `upload_modpack.ps1` 的上传逻辑可复用，在 zip 之后追加一次上传即可）。
4. **资产命名与 tag**：保持 `RSTC-<版本>.zip` 与 `v<yyyyMMdd-HHmmss>`；`versionId` 必须与资产名、`modrinth.index.json` 三者一致。
5. **6.4.7 那条 Release**：内容实为 5.x、标题错误，需标为 prerelease 或删除，否则"最新"判定会误导。
6. **v4 → v5 公告**：README 与 Release 说明补"更新器 5 已改为独立程序，请下载 xxx 并替换"，并说明旧实例数据不受影响。

## 附录 B：v4.11 行为基线（源码核对）

| 项 | v4.11 行为 | 行号 |
|---|---|---|
| 版本状态 | `<McRoot>\RSTC_version.txt`，存 Release Tag | `:8`、`:192–204` |
| 同步目录 | `mods`、`config`、`resourcepacks`、`shaderpacks`、`PCL` | `:11` |
| 同步方式 | `robocopy /MIR`（仅上述五个目录） | `:366–390` |
| 处理顺序 | 备份 → 镜像同步 → 恢复 | `:890–910` |
| 保护文件 | `options.txt`、`servers.dat` | `:13` |
| 下载校验 | 大小 ≥ 90% | `:280` 附近 |
| 首次安装 | 只下载 zip 到桌面 | `:724–725` |
| 自更新 | Tag 相等判断；要求 ps1+bat 两个资产 | `:505–535` |
| 镜像 | 5 个，自动测速 | `:17–28` |
| 日志 | `update_log_<ts>.txt` | `:816` |

## 附录 C：确认台账

### C.1 本轮决策（q-* 来源）

| 问题 id | 用户答复 | 落到 |
|---|---|---|
| q-263-instance | 26.3 实例是 6.0 试验田，可当测试床 | §9 |
| q-repos | 更新器仓库、整合包仓库 URL；源码在 `updater/` | §1.1、§4.1 |
| q-launcher-scope | 选 2：PCL2 与 HMCL 都要正确加载 mods | §2.7、§4.2.1、A1b |
| q-pack-publish | 写脚本自建 mrpack 并自动发布 | 附录 A |
| q-meta-asset | 可以加 `pack-meta.json` 小资产 | §5.1、附录 A |
| q-update-scope | 整包更新留在 V1，用测试包验收 | §1.4、§2.4、§9 |
| q-transition | 不管自更新，发公告手动换 | §6.6、附录 A-6 |
| q-java | 放行，只提示不阻断 | §2.3、§6.3、A3 |
| q-state | 本机实例是导出版本；测试实例另给路径 | §4.2 状态文件 |
| q-selfupdate-scope | v5 自身保留自更新 | §6.6 |
| q-doc-location | 文档放进仓库 `requirements/` | 本文档 |
| q-publish-scope | 发布器后置，先完成 v5 | 附录 A |
| pcl-exclusion | 用户纠正：「我要的不是 PCL/，反而是想去掉发布时自带的 PCL/」 | §1.2、§2.7、§4.2.1、§4.3、附录 A |

### C.2 原文档台账（沿用，未改动）

| 问题 id | 用户原话 / 选择 | 落到 |
|---|---|---|
| launcher-gap | 两者都缺（下不了本体/加载器，也没法导入整合包） | §1.3 |
| form-factor | 仍是双击即用的控制台程序，完全重写 | §1.4 |
| big-version | 整合包跨 MC 版本 + 更新器 4.x→5.x 重构 | §1.3、§1.5 |
| xmcl-runtime | 「可以不用 Pwsh 但是得想个适合的方案」 | §6.1 |
| target-launcher | 最终：不区分启动器，统一写标准结构 | §1.4、§4.2 |
| java | 只检测，缺失时提示并附下载链接 | §3.3、§6.3 |
| modpack-facts | 「现在是 1.21.10 升到 26.3」 | §1.3 |
| modloader | Fabric | §6.2 |
| big-version-layout | 「独立 + 可选数据迁移」 | §2.5 |
| legacy-maintenance | 冻结 1.21.10，只维护 26.3 | §1.5 |
| download-strategy / delta-strategy-confirm | 首次整包，后续增量（V1 先不做增量） | §1.5 |
| v1-scope | 安装 + 整包更新 + 自更新 + 白名单迁移 | §1.4 |
| migration-granularity | 按类别勾选，类别内全选 | §2.5 |
| save-warning | 迁移前警告，玩家自己选 | §2.5 |
| stack-confirm | C# + CmlLib.Core + Spectre.Console | §6.1 |
| deploy-mode | 「选 1 + NET 缺失时打开下载页」 | §2.1 |
| mirror-plan | 多家测速自动选 | §6.5 |
| new-version-number | 6.0 | §1.2 |
| six-four-seven | 标题写错了，内容其实是 5.x | §6.4 |
| version-compare | 按发布时间判断 + 处理掉 6.4.7 | §6.4 |
| main-ui | 状态面板 + 操作菜单 | §2.2 |
| release-metadata | 接受，但需要写发布流程修改需求 | 附录 A |
| self-update | 保留 | §6.6 |
| backup-strategy | 只备份被覆盖的文件 | §2.4、§4.3 |
| multi-mcroot | 自动扫描 + 让玩家选 + 记住 | §2.3 |
| v1-verify | 只查关键文件是否存在 | §3.2 |
| pack-status | 还在选 mod，文档要重点标记 | 文档头提示 |
| mod-count | 100–200 个 | 文档头提示 |
| player-data-dirs | saves / screenshots / 地图 / DH / 快捷键与游戏设置 | §2.5、§4.3 |
| pack-format-choice | B. 全自包含 | §1.2 |
| override-content | 有一部分文件必须随包 | §1.2 |
| pcl-dir | 「记修改意见就行」 | 附录 A |
| install-steps | 尽量少选，能自动就自动 | §2.3 |
| post-install | 给出启动步骤和路径 | §2.3 |
| java-detect-scope | 扫常见位置 + 注册表 | §6.3 |
| fabric-install-method | 自己按 Fabric Meta 写 | §6.2 |
| version-dir-name | 默认「红石镇客户端」，可自定义 | §2.6 |
| mirror-delete | 默认删除，但列给玩家选什么不删 | §4.3 |
| os-support | 只支持 Windows 10/11 | §1.5 |
| artifact-shape | 留给开发时决定 | §2.1、§6.7 |
| release-tooling | 「记到发布器的修改意见里面」 | 附录 A |
| options-handling | 每次发现变化时问玩家 | §2.4、§4.3 |
| backup-retention | 保留最近 3 次 | §3.4、§4.2 |
| confirm-doc | 可以开始整理开发文档 | 本文档 |

## 附录 D：实施契约与风险分级

- 风险分级：本项目**删改玩家文件、操作启动器配置、执行回滚**，按契约应为 **L2/L3**（迁移/删除/配置写入），需要独立 Reviewer + 回滚演练 + 用户确认。
- 交付前需补齐：`PROJECT_PROFILE.md`、`delivery.config.json`（verify / smoke / rollback / highRiskPaths）、`REVIEW.md`；`ai-contract/` 从 `E:\ZM\ai-contract.zip` 解压进仓库根目录。
- 验证命令必须真实存在且 exit 0；UNKNOWN 的 verify/smoke 视为 demo。
- 高风险路径建议：`versions/`、`libraries/`、`assets/`、`options.txt`、`servers.dat`、`PCL/`、`hmclversion.cfg`。

---

*本文档为需求确认阶段的产出，不包含实现代码。下一步是把本文档作为输入产出一份经批准的实施方案；实现必须在标准模式的新会话中进行。*
