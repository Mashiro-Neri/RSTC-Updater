# AI 开发交付契约 · 附录（可选加载）

> 与 `AI_DEVELOPMENT_CONTRACT.md` 配套。主契约是每次开场必读；本附录按需读。
> status: draft · last-reviewed: 2026-10-29

## 1. 假绿信号 → 必须触发的防线

| 假绿信号 | 必须触发 |
|---|---|
| 单测/typecheck 绿 = 完成 | Gate 2 真实路径 + Gate 3 独立证据；bugfix 先红后绿 |
| 接口返回 200 | Gate 2 数据/文件/队列/用户可见结果实际变化 |
| 日志出现 started | Gate 2 入口真实触发 + 接线证据（入口→注册→副作用） |
| “真机验证过”但无法复现 | Gate 2 原始输出文件 + commit/产物绑定 + Verifier 重跑 |
| restart 后就认为部署成功 | Gate 4 身份五元组 + 不匹配回滚 |
| 性能只给单次数字 | 分 cold/warm、环境、样本、baseline、p50/p95 |
| “语义不变/只改性能” | 新旧路径差分；有差异即行为变更 |
| 审核说“已修复” | findings 台账 + Reviewer/Verifier 关闭 |
| 用户说“继续” | 只在决策卡范围内确认；不能替代不可豁免 finding 的修复 |
| 旧路径“以后再删” | 建任务/owner/到期日 + 无调用证据；到期阻断新功能 |
| 审批/权限“应该没问题” | 默认拒绝、未知能力审批、服务端执行点强制、bypass test |
| 测试被改弱/跳过 | BLOCKER；Reviewer 检查测试/CI/验收标准 diff |
| 事故只写文档 | fail-before/pass-after 可执行检查加入 verify/CI |

## 2. 独立审查输入包

Reviewer/Verifier 只接收：

```text
仓库路径、base..head commit
任务文件（用户原话 + 验收标准）
diff（自己从 git 读取）
证据卡 + evidence/<task>/<commit>/ 原始输出
项目约定（AGENTS/REVIEW/delivery.config）
```

不接收：Builder 的自评结论、辩解、已整理好的“通过理由”。

独立性定义：不同 Agent/Session、fresh context、不是 Builder 的后续 turn、prompt 不由 Builder 编写结论；Reviewer 必须自己看 diff。
L2/L3 必须两个独立验证者（审查 + 运行验证）；Builder 不得指定或挑选 Reviewer；L2/L3 Deployer ≠ Builder。

## 3. 多智能体角色表

| 角色 | 写权限 | 能做 | 不能做 |
|---|---|---|---|
| Lead/用户 | 最终合并/批准 | 定方向、创建 Reviewer/Verifier、写 verified | 代替独立验证 |
| Builder | 一个 worktree | 实现、自检、修 findings | 自审、写 verified、改风险规则 |
| Reviewer | 只写 review 报告 | 审 diff + 证据、开/关 findings | 改代码、给自己审 |
| Verifier | 只写 evidence | 独立跑验收、复跑关键证据 | 改代码、复述 Builder |
| Deployer | 部署目标 | 执行已批准部署/回滚 | 同时做 Builder（L2/L3） |

任务板写 `write_scopes`；审查/验证期间冻结 commit；新 commit 重新审受影响文件和证据；每 commit 最多 2 轮修复+复审，仍 HIGH → blocked/重新设计；证据目录 `evidence/<task>/<commit>/` 含命令、退出码、时间、stdout/stderr。
AI 投票不等于事实；Lead/用户按证据复核；父 Agent/其他 Agent 不能替用户批准。

## 4. 项目三件套

- `PROJECT_PROFILE.md`：持久画像。首次建完整版，之后只写变化 + last-reviewed + contractVersion。
- `REVIEW.md`：本项目“出错长什么样”、必看路径、必跑命令、已知噪音、历史事故→检查项。
- `delivery.config.json`：verify/smoke/deploy/rollback/identity/highRiskPaths/evidenceDir/neverTouch。
  - `verify/smoke/identity` 缺失或 UNKNOWN → 最高 `demo`。
  - `rollback` UNKNOWN → 禁止 L2/L3。
  - `highRiskPaths` UNKNOWN → 默认 L2。
  - 命令必须实际执行并记录退出码；配置变更 = L2，并随版本入库。
  - 跨仓库变更列全 repo、commit、回滚顺序。

模板见 `templates/PROJECT_PROFILE.md`、`templates/REVIEW.md`、`templates/EVIDENCE.md`；示例配置见 `delivery.config.example.json`。

## 5. 语义变更清单（Change Brief 用）

- 旧语义 / 新语义 / 用户可见差异（前后例子）
- 受影响：入口、调用方（仓库内外）、配置/env、文档、旧数据、缓存
- 兼容：版本号/开关/双写/回填校验/默认值保留
- 回滚：旧版本能否读新数据？不能则写破坏窗口
- 测试：旧数据/旧调用方在新语义下的结果是否仍正确

## 6. 性能/差分测试模板

```text
指标：p50 / p95 / 吞吐 / 内存
环境：机器/容器/数据规模/冷启动 or 热缓存
baseline：旧实现 commit + 测量方法
候选：新实现 commit + 测量方法
样本：N；重复次数；异常值处理
结论：是否达到目标；用户可感知差异
```

## 7. 事故→eval 模板

```text
现象 / 影响 / 最小复现
根因（代码/设计/流程）
错误防线：哪一层应该抓到
fail-before：旧 commit/脚本失败输出
pass-after：修复后通过输出
新增回归检查（路径 + 加入 verify/CI）
REVIEW.md 新增必查项
```

## 8. 用户可见变更与部署迁移补充

- 用户可见变更：交付后必须有用户**实际看到**的结果（原话/时间/操作），否则状态最多 `demo/awaiting-user`；AI Verifier 只能写 `verified` 的工程证据，不能代替用户看到结果。
- 部署前必须校验：artifact/镜像中 migration 文件集合 == 仓库；目标 DB `schema_migrations` == artifact 应含版本；缺失、跳跃、顺序不一致 → 禁止部署。
- 单 Agent 环境：无法满足 fresh Reviewer/Verifier 时，L0 可自检，L1 最高 demo，L2/L3 blocked；不得轮换角色冒充独立。
- 日志证据：路径、时区、时间范围、started 与 terminal 事件配对；只有 started 不算。
- 命令只在 `delivery.config.json` 维护；`REVIEW.md` 写“为什么/看什么”，不复制命令值，避免两处漂移。
- 用户叫停时输出：已完成 / 未完成 / 风险 / 回滚；不继续后台动作。

## 9. 设计依据

- [Anthropic, “How the Claude Code team does code review”, 2026-10-15](https://claude.dev/blog/solving-code-review-at-anthropic/)：验证前置、分层 review、风险分级、findings 闭环、回滚、incident→eval；个人版只取原则，去掉多人审批/merge queue。
- 2026-10-29 Sora 审计（项目内记录，可选）：假绿、部署漂移、错误目录删除、语义变化、审批绕过、日志不可观测等真实样本。
