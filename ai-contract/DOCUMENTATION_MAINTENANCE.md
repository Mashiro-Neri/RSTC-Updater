# 项目文档维护规范（个人通用版 v1-draft）

> status: draft（用户确认后 confirmed）· contractVersion: 1.0 · last-reviewed: 2026-10-29
> 适用：任何语言、任何规模的个人项目；AI 维护文档，用户不读代码。
> 配套：`AI_DEVELOPMENT_CONTRACT.md`（交付流程）；规则冲突时以更严的为准。

## 0. 一句话

**文档是项目状态的可执行说明，不是写给人看的装饰；每个事实只写一处，其他位置链接。**
**AI 必须主动维护文档；用户不应该通过读代码或翻日志来理解项目。**

## 1. 最小文档集

每个项目至少这些文件，放在项目根或统一目录：

| 文件 | 用途 | 谁写 | 唯一事实源 |
|---|---|---|---|
| `README.md` | 用户视角：是什么、怎么用、怎么启动、怎么恢复 | AI | 用户入口 |
| `PROJECT_PROFILE.md` | 技术画像：栈、命令、环境、数据、风险、UNKNOWN | AI | 项目事实 |
| `REVIEW.md` | 本项目“出错长什么样”、必看路径、必跑命令、已知噪音 | AI + 用户确认 | 质量规则 |
| `delivery.config.json` | verify/smoke/deploy/rollback/identity 命令 | AI | **命令唯一来源** |
| `DECISIONS.md` 或 `docs/decisions/` | 重要取舍：为什么选/不选，代价，重审条件 | AI | 决策原因 |
| `TASKS.md` 或任务目录 | 当前/历史任务、验收、状态、交接 | AI | 工作状态 |
| `EVIDENCE.md` 或 `evidence/<task>/` | 每次交付的证据卡/原始输出 | AI | 证明 |
| `RUNBOOK.md` | 部署、回滚、故障排查、恢复 | AI | 运维步骤 |
| `MISTAKES.md` 或 incident 记录 | 事故、根因、回归检查、REVIEW 更新 | AI | 失败知识 |
| `CHANGELOG.md` | 用户可见变更 | AI | 发布记录 |
| `API.md` / `DATA.md` | 只有项目有外部接口/数据契约时才建 | AI | 契约 |

文档超过 10 个时加 `docs/INDEX.md`；文档超过 200 行必须拆分，硬上限 300。
不要为了“齐全”建空文件；不存在的文档写 `N/A`，不要假装有。

## 2. 单一事实源

- `delivery.config.json` 是**命令唯一来源**；README/REVIEW/RUNBOOK 只链接，不复制命令值。
- `PROJECT_PROFILE.md` 是项目事实唯一来源；其它文档引用，不重复。
- `EVIDENCE.md` 是证明唯一来源；文档里写“已验证”必须链接到证据。
- `REVIEW.md` 是质量规则唯一来源；事故复盘只追加检查项，不另造一套。
- 一个事实有变化时：改唯一源 → 修所有链接的含义 → 删除旧副本；禁止两处同时维护。
- 历史文档不删；标 `superseded` 并链接新版本，保留 git 历史。

## 3. 什么时候必须更新

任何变更只要影响以下任一项，就必须在同一 commit/PR 更新文档：

- 用户可见行为、操作步骤、验收方式
- 命令、配置、环境变量、依赖、端口、权限
- 数据格式、迁移、存储、备份、retention
- 部署/回滚/健康检查/日志位置
- 已知限制、风险、默认值、兼容窗口
- 事故/故障的根因与回归检查
- 新增/删除的旧路径、feature flag、临时探针

如果确实不影响文档，必须在交付说明写：`文档影响：无，因为…`。
禁止“先合并，文档以后补”；以后就是永远不会。

## 4. 文档生命周期

1. **首次进入项目**：AI 创建 README、PROJECT_PROFILE、REVIEW、delivery.config、RUNBOOK、MISTAKES 骨架；用户只看 3 行摘要。
2. **每次任务**：更新受影响的文档 + 证据 + TASKS；reviewer 检查文档 diff。
3. **每次事故**：写 incident 记录 + 根因 + 回归检查；更新 REVIEW.md；必要时更新 RUNBOOK。
4. **每次发布**：检查 README 是否仍准确、命令是否可复制、回滚步骤是否演练过、过期文档是否标 superseded。
5. **周期性**：每次里程碑用 `last-reviewed` 扫陈旧文档；超期或跨 20 个 commit 未验证 → 视为 UNKNOWN。

## 5. 文档头与状态

所有非一次性文档必须有：

```text
status: draft | confirmed | superseded
last-reviewed: YYYY-MM-DD
owner: AI / 用户
```

- `confirmed` 只能由用户或独立 Reviewer 确认；AI 不能自标。
- 改行为却不改 `last-reviewed` = 文档漂移。
- 过期文档先标 `superseded`，再链接新文档；不要静默删除。

## 6. 连续 Bug / 根因规则

这条规则在 `AI_DEVELOPMENT_CONTRACT.md` §10，文档维护时按同样规则落盘：

- **同一现象连续两次修复无效，必须停手查根因，禁止第三次直接试补丁。**
- 根因四问：**现象层（用户看到什么）/ 触发层（最小复现）/ 缺陷层（缺哪个 invariant）/ 设计层（是否两套状态/入口打架）**。
- 修完必须验证同一类路径，不只当前入口。
- 每次事故必须写进 `MISTAKES.md` 或 incident 记录，包含：
  - 现象、影响、最小复现
  - 根因（代码/设计/流程）
  - 哪一层本应拦住
  - fail-before / pass-after 证据
  - 新增回归检查（加入 verify/CI）
  - REVIEW.md 增加必查项
- **只写“已修复”不算闭环；没有可执行回归检查 = 事故还会再来。**

## 7. 用户视角写作

用户不读代码，因此：

- README 第一屏写：这是什么、怎么启动、怎么看到成功、出问题怎么恢复。
- 操作步骤必须可复制：命令、预期输出、失败时看哪里。
- 不要用类名/内部术语解释用户可见行为。
- 所有验收写成：**操作 → 期望看到 → 超时/失败怎么办**。
- 截图/证据必须带目标身份（窗口标题/URL/记录 id），不能只截局部。
- 不写密钥、token、用户隐私、原始大日志。

## 8. 自动化检查

每个项目应有一个 `docs:check` 或 `verify` 步骤：

- 文档头 status/last-reviewed/owner 存在
- 内部链接/文件引用存在
- 行数 soft/hard 限制
- 命令引用只指向 `delivery.config.json`，无重复命令副本
- 变更涉及代码/迁移/部署但文档 diff 为空 → 警告
- 禁止未标注的 `TODO/FIXME` 长期存在
- `MISTAKES.md` 与 REVIEW.md 的回归检查项对应
- 文档中不出现密钥/隐私模式（secret scan）

没有自动检查时，至少每次交付手工跑一遍 checklist；不要靠记忆。

## 9. 反模式（看到就停）

- README 只有“安装依赖”，没有“怎么用/怎么恢复”
- 同一个命令在 README、REVIEW、RUNBOOK 各写一遍，改一处漏两处
- 文档写“已完成/已验证”，但找不到证据
- 只更新代码，不更新文档；或者文档只写实现，不写用户行为
- 事故只写“已修复”，没有根因/回归检查
- 长期 `TODO`、`临时方案`、`以后补`，没有 owner/日期
- 大段原始日志、密钥、用户隐私粘进文档
- AI 自己标 `confirmed`，没有用户确认
- 文档过期不标 superseded，继续引用旧结论

## 10. 每次交付的文档 checklist

- [ ] 受影响的 README/PROJECT_PROFILE/REVIEW/RUNBOOK 已更新
- [ ] 命令只出现在 delivery.config，其他位置链接
- [ ] 行为/配置/迁移/部署变化都有文档 diff
- [ ] 用户可见变更写了“怎么看到结果”
- [ ] 证据链接到 EVIDENCE/evidence 目录
- [ ] 事故/坑写入 MISTAKES 并更新 REVIEW
- [ ] 过期文档标 superseded / 链接新版本
- [ ] 文档头 status/last-reviewed/owner 正确
- [ ] `docs:check` 通过；没有新增 secret/TODO 残留

## 11. 来源

- 本规范与 `AI_DEVELOPMENT_CONTRACT.md` 配套；事故→eval/所有权/证据原则参考 Anthropic Code Review 文章，个人版已裁剪为“AI 维护 + 用户只看产品结果”。
- 连续 Bug 规则直接对应 Sora 2026-10-29 审计与既有 delivery-discipline 规则 10；不在两个文档里写不同版本。
