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
- 禁止当作 finding 的风格项：控制台配色、表格边框样式、日志里的中文标点。
- 历史事故→检查项：v4.11 曾因 UTF-8 编码导致启动闪退（`62788cd`），v5 所有中文输出必须显式 UTF-8；v4 自更新依赖两个资产名，v5 变更资产名时必须验证 v4 用户路径。
