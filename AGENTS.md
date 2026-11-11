# AGENTS.md

## AI 开发契约（开工前必读）

本项目实现遵循 `ai-contract/AI_DEVELOPMENT_CONTRACT.md`。

- 首次进入项目：先读 `ai-contract/README.md`，再读主契约全文；然后按契约第 12 节维护 `PROJECT_PROFILE.md`、`REVIEW.md`、`delivery.config.json`
- 每次开工前：至少读「启动协议」「风险分级」「Gate 出口」「禁止清单」四节
- 假绿映射、多 Agent、证据模板见 `ai-contract/APPENDIX.md` 与 `ai-contract/templates/`

## 项目布局

| 路径 | 内容 |
|---|---|
| `updater/` | v4.11 旧版 PowerShell 更新器（保留，不再改） |
| `src/RstcUpdater/` | v5 C# 控制台程序（当前阶段 0） |
| `requirements/` | 开发文档与需求台账 |
| `publish.bat` / `publish.ps1` | 更新器发布脚本（本地，不入库） |
| `evidence/` | 运行证据原件（不可编辑） |

## 产品基线

- 开发文档：`requirements/RSTC-Updater-5.0-开发文档.md`（2026-11-11 修订版 v2）
- 整合包仓库：`Mashiro-Neri/Redstone-Town-Client_update_modpack`
- 更新器仓库：`Mashiro-Neri/RSTC-Updater`
- **玩家实际实例目录（`E:\其他文件\红石镇客户端\**`）只读，任何测试不得写入；测试只用独立临时目录。**
