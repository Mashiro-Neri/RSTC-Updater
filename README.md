# RSTC Updater · 红石镇客户端更新器

红石镇客户端（Redstone Town Client）整合包自动更新工具。

## 下载

从 [Release](https://github.com/Mashiro-Neri/RSTC-Updater/releases) 下载：
- `update_modpack.ps1`
- `update_modpack.bat`

## 使用

1. 将两个文件放入 Minecraft 版本文件夹（通常路径类似 `.minecraft\versions\你的版本\`）
2. 双击 `update_modpack.bat`
3. 按菜单操作：

| 菜单 | 功能 |
|------|------|
| 检查整合包更新 | 比对本机版本与 GitHub 最新 Release，自动下载并同步 mods / config / resourcepacks 等 |
| 没有整合包（首次下载） | 下载整合包 ZIP 到桌面，手动拖入启动器安装 |
| 更新启动器 | 检查并更新 `update_modpack.ps1` / `update_modpack.bat` 自身 |

> 将文件放在版本文件夹里可以自动获取路径，无需手动输入 MC 根目录。

## 命令行

```powershell
.\update_modpack.ps1 --mc-root "D:\.minecraft"    # 指定 MC 目录
.\update_modpack.ps1 --non-interactive            # 跳过菜单
.\update_modpack.ps1 --preserve-config            # 保留本地配置
.\update_modpack.ps1 --help                       # 帮助
```

## 说明

- 自动备份 `options.txt`、`servers.dat` 及自定义 config
- 内置多个下载镜像，自动测速选最快
- 更新日志保存在 `update_log_*.txt`
