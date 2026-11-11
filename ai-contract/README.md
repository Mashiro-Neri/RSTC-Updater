# 可移植 AI 开发契约包

这个目录可以整体复制到任何项目的根目录，**跟着项目走**：换机器、改路径、移动文件夹都不受影响，因为主文档对附录和模板的引用全是相对路径。

推荐放法：

```text
your-project/
├── AGENTS.md                        ← 入口：告诉所有 AI 先读这里
├── ai-contract/                     ← 本目录，整体复制过来
│   ├── AI_DEVELOPMENT_CONTRACT.md
│   ├── APPENDIX.md
│   ├── DOCUMENTATION_MAINTENANCE.md
│   ├── delivery.config.example.json
│   └── templates/
├── PROJECT_PROFILE.md               ← AI 首次进入项目时按模板创建
├── REVIEW.md                        ← 同上
└── delivery.config.json             ← 同上
```

目录名可以改（`ai-contract/`、`ai/`、`docs/contract/` 都行），只要**目录内保持相对结构**，主文档就能找到 `APPENDIX.md` 和 `templates/...`。

## 入口文件（关键）

在项目根的 `AGENTS.md` 里加一节，让任何 AI 一进项目就知道要先读契约：

```markdown
## AI 开发契约（开工前必读）

本项目实现遵循 `ai-contract/AI_DEVELOPMENT_CONTRACT.md`。

- 首次进入项目：先读 `ai-contract/README.md`，再读主契约全文；然后按契约第 12 节创建 `PROJECT_PROFILE.md`、`REVIEW.md`、`delivery.config.json`
- 每次开工前：至少读「启动协议」「风险分级」「Gate 出口」「禁止清单」四节
- 假绿映射、多 Agent、证据模板见 `ai-contract/APPENDIX.md` 与 `ai-contract/templates/`
```

`AGENTS.md`（以及 `CLAUDE.md`）会被 DSH 等支持该约定的工具在每次会话自动读取，所以这是**唯一需要的入口**——不用再造一个 "START_HERE" 文件。

项目已有 `AGENTS.md` 时**追加**这一节，不要覆盖原有内容。

## 两种用法

### A. 完整包（推荐）

把本目录整体复制到项目根，按上面的结构放好，再写 `AGENTS.md` 入口。

- 有附录（假绿映射、多 Agent、事故→eval）和四张现成模板
- 证据卡、开工卡格式固定，便于批量核对
- 要求：目录内相对结构不能改

### B. 只复制一个文件

把 `AI_DEVELOPMENT_CONTRACT_STANDALONE.md` 复制成 `ai-contract/AI_DEVELOPMENT_CONTRACT.md`，或直接粘贴给 AI。

- 不依赖目录、附录、模板
- 适合快速开始、聊天工具、临时项目
- 缺点：没有附录和模板，证据卡格式由 AI 自己设计

## 目录说明

| 文件 | 用途 |
|---|---|
| `AI_DEVELOPMENT_CONTRACT.md` | 完整包主文档，引用了附录/模板 |
| `AI_DEVELOPMENT_CONTRACT_STANDALONE.md` | 单文件自包含版 |
| `DOCUMENTATION_MAINTENANCE.md` | 项目文档维护规范 |
| `APPENDIX.md` | 假绿映射、多 Agent、事故→eval |
| `templates/CHANGE_BRIEF.md` | 开工卡 |
| `templates/PROJECT_PROFILE.md` | 项目画像 |
| `templates/REVIEW.md` | 项目审查指南 |
| `templates/EVIDENCE.md` | 证据卡 |
| `delivery.config.example.json` | 多项目命令/风险路径配置示例 |

## 项目内生成的文件

这三个是**项目根**里的文件，不是本包的一部分，AI 在**真正开始实现时**才创建（需求讨论阶段不建，避免留下空壳项目）：

- `PROJECT_PROFILE.md`
- `REVIEW.md`
- `delivery.config.json`

包里的 `templates/` 只是样例，复制成项目根文件后按项目填写。

## 如果放到自定义目录

主文档里的引用都是**相对于主文档所在目录**的：`APPENDIX.md`、`templates/CHANGE_BRIEF.md` 等。

只要保持这些文件和主文档的相对位置，就不用改任何路径。只想放主文档、或把模板放到别处时，用 `AI_DEVELOPMENT_CONTRACT_STANDALONE.md`，或者把主文档里的相对路径改成实际路径。
