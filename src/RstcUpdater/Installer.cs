using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>V1 安装流程：原版 26.3 → 校验整合包 → Fabric → 实例 → 应用 → 完成信息。</summary>
    internal static class Installer
    {
        public static async Task<int> InstallFlowAsync(string gameDir, string instanceName, string localZipPath = null, bool betaChannel = false)
        {
            gameDir = Path.GetFullPath(gameDir);
            if (!PackApplier.IsSafeInstanceName(instanceName))
            {
                Console.Error.WriteLine("实例名不合法: " + instanceName);
                return 2;
            }

            if (!Directory.Exists(gameDir))
            {
                Directory.CreateDirectory(gameDir);
            }

            Ui.Phase("准备游戏目录");
            AnsiConsole.MarkupLine("[grey]  目标: [/]" + Markup.Escape(gameDir) + "  [grey]实例: [/]" + Markup.Escape(instanceName));
            string instanceDir = Path.Combine(gameDir, "versions", instanceName);
            if (Directory.Exists(instanceDir))
            {
                Console.Error.WriteLine("同名实例已存在: " + instanceName + "（请换一个名字，或走「更新现有实例」）");
                return 2;
            }

            // 1) 原版 26.3（完整性判定 + 最多 3 次；CmlLib 会复用已下载文件，相当于断点续跑）
            Ui.Phase("下载原版 Minecraft " + Provisioner.TargetVersion);
            if (IsVanillaComplete(gameDir))
            {
                AnsiConsole.MarkupLine("[grey]26.3 原版已完整，跳过下载[/]");
            }
            else
            {
                if (File.Exists(Path.Combine(gameDir, "versions", Provisioner.TargetVersion, Provisioner.TargetVersion + ".json")))
                {
                    AnsiConsole.MarkupLine("[yellow]检测到 26.3 不完整（上次中断），继续补齐…[/]");
                }

                int code = -1;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    code = await Program.InstallVanillaAsync(gameDir).ConfigureAwait(false);
                    if (code == 0)
                    {
                        break;
                    }

                    AnsiConsole.MarkupLine("[yellow]原版安装失败（exit " + code + "），重试 " + attempt + "/3；已下载文件会复用[/]");
                    await Task.Delay(2000).ConfigureAwait(false);
                }

                if (code != 0)
                {
                    Console.Error.WriteLine("原版 26.3 安装失败（已重试 3 次），已中止");
                    return code;
                }
            }

            // 2) 先解析并校验整合包（避免"先建实例后才发现 MC 不符"）
            Ui.Note("准备整合包（下载后校验 sha256 与 MC 版本）…");
            string tag;
            string assetName;
            string zipPath;
            bool downloaded = false;
            if (!string.IsNullOrEmpty(localZipPath))
            {
                zipPath = Path.GetFullPath(localZipPath);
                assetName = Path.GetFileName(zipPath);
            }
            else
            {
                ReleaseInfo release = await ReleaseClient.GetLatestByChannelAsync(ReleaseClient.PackRepo, betaChannel).ConfigureAwait(false);
                if (release == null)
                {
                    Console.Error.WriteLine("无法获取整合包 Release（网络/限流，且无缓存）");
                    return 5;
                }

                string tmpDir = Path.Combine(gameDir, ".rstc-tmp");
                Directory.CreateDirectory(tmpDir);
                string releaseJson = Path.Combine(tmpDir, "latest-release.json");
                File.WriteAllText(releaseJson, release.RawJson, new UTF8Encoding(false));

                PackAssetRef asset = PackFetcher.ResolveAsset(releaseJson, null);
                if (asset == null)
                {
                    return 5;
                }

                zipPath = await PackFetcher.DownloadAssetAsync(gameDir, asset).ConfigureAwait(false);
                downloaded = true;
                assetName = asset.Name;
                tag = asset.Tag;
                goto ValidatePack;
            }

            {
                var localMeta = PackApplier.ReadMetadata(zipPath);
                if (localMeta == null)
                {
                    Console.Error.WriteLine("包缺少 modrinth.index.json: " + zipPath);
                    return 5;
                }

                tag = "local-" + (localMeta.VersionId ?? "test");
                assetName = Path.GetFileName(zipPath);
            }

        ValidatePack:
            var meta = PackApplier.ReadMetadata(zipPath);
            if (meta == null)
            {
                Console.Error.WriteLine("整合包缺少 modrinth.index.json");
                return 5;
            }

            if (!string.Equals(meta.Minecraft, Provisioner.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("该整合包基于 Minecraft " + meta.Minecraft + "，与 V1 目标 " +
                                        Provisioner.TargetVersion + " 不符。");
                if (downloaded)
                {
                    File.Delete(zipPath);
                }

                if (!betaChannel)
                {
                    Ui.Note("提示：可改用「在线下载 Beta 版（内测 6.0）」重试，或等待正式版发布。", true);
                }

                return 5;
            }

            // 3) Fabric profile + 库（网络抖动时重试）
            Ui.Phase("安装 Fabric " + Provisioner.FabricVersionId + " 与实例");
            Ui.Note("准备 Fabric 配置与依赖库…");
            FabricProfile fabric = null;
            for (int attempt = 1; attempt <= 3 && fabric == null; attempt++)
            {
                fabric = await Provisioner.EnsureFabricAsync(gameDir, true).ConfigureAwait(false);
                if (fabric == null)
                {
                    AnsiConsole.MarkupLine("[yellow]Fabric 获取失败，重试 " + attempt + "/3[/]");
                    await Task.Delay(2000).ConfigureAwait(false);
                }
            }

            if (fabric == null)
            {
                Console.Error.WriteLine("Fabric profile/库获取失败（已重试 3 次）");
                return 5;
            }

            // 4) 实例（默认 A2 自包含平铺）
            Ui.Note("创建实例 " + instanceName + "（A2 平铺）…");
            if (!Provisioner.CreateInstance(gameDir, instanceName, "A2", fabric))
            {
                return 7;
            }

            Provisioner.EnsureLanguageCompatibility(gameDir, instanceName);

            // 5) 应用整合包
            Ui.Phase("应用整合包文件");
            int apply = PackApplier.Run(new PackApplier.PackOptions
            {
                GameDir = gameDir,
                ZipPath = zipPath,
                Instance = instanceName,
                Update = false,
                Tag = tag
            });
            if (apply != 0)
            {
                Console.Error.WriteLine("整合包安装失败（exit " + apply + "），实例目录保留供排查: " + instanceDir);
                return apply;
            }

            if (downloaded)
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch (IOException)
                {
                }
            }

            PrintCompletion(gameDir, instanceName, tag, assetName);
            return 0;
        }

        private static bool IsVanillaComplete(string gameDir)
        {
            string versionJson = Path.Combine(gameDir, "versions", Provisioner.TargetVersion, Provisioner.TargetVersion + ".json");
            string jar = Path.Combine(gameDir, "versions", Provisioner.TargetVersion, Provisioner.TargetVersion + ".jar");
            string assetIndex = Path.Combine(gameDir, "assets", "indexes", "34.json");
            string objects = Path.Combine(gameDir, "assets", "objects");
            if (!File.Exists(versionJson) || !File.Exists(jar) || !File.Exists(assetIndex))
            {
                return false;
            }

            try
            {
                if (new FileInfo(jar).Length != 41483720L)
                {
                    return false;
                }

                return Directory.Exists(objects) &&
                       Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).Count() >= 5149;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void PrintCompletion(string gameDir, string instanceName, string tag, string assetName)
        {
            Ui.Card("安装完成", new[]
            {
                ("实例", Markup.Escape(instanceName)),
                ("路径", Markup.Escape(Path.Combine(gameDir, "versions", instanceName))),
                ("整合包版本", Markup.Escape(tag ?? "?")),
                ("资产", Markup.Escape(assetName ?? "?"))
            });
            AnsiConsole.MarkupLine("[grey]  启动方式：在 PCL2 / HMCL 里把游戏目录指向 [/]" + Markup.Escape(gameDir));
            AnsiConsole.MarkupLine("[grey]            选择实例 [/]" + Markup.Escape(instanceName) + "[grey] 启动；若启动器未开启版本隔离，需手动开启。[/]");
            AnsiConsole.MarkupLine("[grey]  提示：版本列表里还会出现 26.3 与 " + Provisioner.FabricVersionId + "，请选上面的实例。[/]");
        }
    }
}
