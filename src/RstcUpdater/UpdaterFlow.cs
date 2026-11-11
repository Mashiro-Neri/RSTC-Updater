using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    internal sealed class UpdateCheck
    {
        public string LocalVersion;
        public string LocalTag;
        public string LocalMinecraft;
        public string RemoteVersion;
        public string RemoteTag;
        public bool UpdateAvailable;
        public bool LocalInstanceMissing;
        public string Message;
    }

    internal sealed class UpdateOptions
    {
        public string GameDir;
        public string Instance;
        public string LocalZipPath;
        public List<string> Keep = new List<string>();
        public string OptionsMode = "keep";
        public string ServersMode = "keep";
        public bool DryRun;
        public bool Force;
    }

    internal sealed class PreparedUpdate
    {
        public string GameDir;
        public string Instance;
        public string ZipPath;
        public bool Downloaded;
        public string LocalVersion;
        public string RemoteVersion;
        public string RemoteTag;
        public PackPlan Plan;
        public PackApplier.PackOptions PackOptions;
        public bool UpToDate;
    }

    /// <summary>V1 整包更新：版本比较 → 下载/校验 → 计划预览 → 应用。</summary>
    internal static class UpdaterFlow
    {
        public static bool TryReadLocal(string gameDir, string instanceName, out string versionId, out string tag, out string minecraft)
        {
            versionId = null;
            tag = null;
            minecraft = null;
            string instanceDir = Path.Combine(gameDir, "versions", instanceName);
            string index = Path.Combine(instanceDir, "modrinth.index.json");
            if (File.Exists(index))
            {
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(index, Encoding.UTF8)) as JsonObject;
                    versionId = root?["versionId"]?.GetValue<string>();
                    minecraft = root?["dependencies"]?["minecraft"]?.GetValue<string>();
                }
                catch (Exception)
                {
                    return false;
                }
            }

            string tagFile = Path.Combine(instanceDir, "RSTC_version.txt");
            if (File.Exists(tagFile))
            {
                tag = File.ReadAllText(tagFile, Encoding.UTF8).Trim();
            }

            return Directory.Exists(instanceDir) && (versionId != null || tag != null);
        }

        /// <summary>只读检查（不下载包）。</summary>
        public static async Task<UpdateCheck> CheckAsync(string gameDir, string instanceName)
        {
            var check = new UpdateCheck();
            if (!TryReadLocal(gameDir, instanceName, out string versionId, out string tag, out string mc))
            {
                check.LocalInstanceMissing = true;
                check.Message = "本地实例不存在或状态文件缺失";
                return check;
            }

            check.LocalVersion = versionId;
            check.LocalTag = tag;
            check.LocalMinecraft = mc;

            ReleaseInfo release = await ReleaseClient.GetLatestAsync(ReleaseClient.PackRepo).ConfigureAwait(false);
            if (release == null)
            {
                check.Message = "无法获取远端发布信息（网络/限流且无缓存）";
                return check;
            }

            ReleaseAsset asset = ReleaseClient.FindPackAsset(release);
            check.RemoteVersion = ReleaseClient.VersionFromAssetName(asset?.Name);
            check.RemoteTag = release.Tag;

            if (!string.Equals(mc, Provisioner.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                check.Message = "本地 MC 版本为 " + (mc ?? "未知") + "，新版本基于 " +
                                Provisioner.TargetVersion + "，请用「安装到新目录」";
                return check;
            }

            check.UpdateAvailable = check.RemoteVersion != null &&
                                    !string.Equals(versionId, check.RemoteVersion, StringComparison.OrdinalIgnoreCase);
            if (!check.UpdateAvailable)
            {
                check.Message = "已是最新版本";
            }

            return check;
        }

        /// <summary>下载 + 计划预览；失败返回 null（错误已打印）。</summary>
        public static async Task<PreparedUpdate> PrepareAsync(UpdateOptions options)
        {
            string gameDir = Path.GetFullPath(options.GameDir);
            if (!Directory.Exists(gameDir))
            {
                Console.Error.WriteLine("游戏目录不存在: " + gameDir);
                return null;
            }

            if (!PackApplier.IsSafeInstanceName(options.Instance))
            {
                Console.Error.WriteLine("实例名不合法: " + options.Instance);
                return null;
            }

            if (!TryReadLocal(gameDir, options.Instance, out string versionId, out string tag, out string mc))
            {
                Console.Error.WriteLine("本地实例不存在或状态文件缺失: " + options.Instance);
                return null;
            }

            if (!string.Equals(mc, Provisioner.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("本地 MC 版本为 " + (mc ?? "未知") + "，不是 " + Provisioner.TargetVersion +
                                        "，请用「安装到新目录」");
                return null;
            }

            AnsiConsole.MarkupLine("[grey]本地: " + Markup.Escape(options.Instance) + " / 版本 " +
                                   Markup.Escape(versionId ?? "?") + " / MC " + Markup.Escape(mc ?? "?") + "[/]");

            var prepared = new PreparedUpdate
            {
                GameDir = gameDir,
                Instance = options.Instance,
                LocalVersion = versionId
            };

            if (!string.IsNullOrEmpty(options.LocalZipPath))
            {
                prepared.ZipPath = Path.GetFullPath(options.LocalZipPath);
            }
            else
            {
                ReleaseInfo release = await ReleaseClient.GetLatestAsync(ReleaseClient.PackRepo).ConfigureAwait(false);
                if (release == null)
                {
                    Console.Error.WriteLine("无法获取远端发布信息（网络/限流且无缓存）");
                    return null;
                }

                prepared.RemoteTag = release.Tag;
                ReleaseAsset asset = ReleaseClient.FindPackAsset(release);
                prepared.RemoteVersion = ReleaseClient.VersionFromAssetName(asset?.Name);
                if (!options.Force && string.Equals(versionId, prepared.RemoteVersion, StringComparison.OrdinalIgnoreCase))
                {
                    prepared.UpToDate = true;
                    AnsiConsole.MarkupLine("[green]已是最新版本[/]（本地 " + Markup.Escape(versionId ?? "?") +
                                           " / 远端 " + Markup.Escape(prepared.RemoteVersion ?? "?") + "）");
                    return prepared;
                }

                string tmpDir = Path.Combine(gameDir, ".rstc-tmp");
                Directory.CreateDirectory(tmpDir);
                string releaseJson = Path.Combine(tmpDir, "latest-release.json");
                File.WriteAllText(releaseJson, release.RawJson, new UTF8Encoding(false));
                PackAssetRef assetRef = PackFetcher.ResolveAsset(releaseJson, null);
                if (assetRef == null)
                {
                    return null;
                }

                prepared.ZipPath = await PackFetcher.DownloadAssetAsync(gameDir, assetRef).ConfigureAwait(false);
                prepared.Downloaded = true;
                prepared.RemoteTag = assetRef.Tag;
            }

            var meta = PackApplier.ReadMetadata(prepared.ZipPath);
            if (meta == null)
            {
                Console.Error.WriteLine("包缺少 modrinth.index.json");
                return null;
            }

            if (prepared.RemoteVersion == null)
            {
                prepared.RemoteVersion = meta.VersionId;
            }

            if (string.IsNullOrEmpty(prepared.RemoteTag))
            {
                prepared.RemoteTag = "local-" + (meta.VersionId ?? "test");
            }

            if (!string.Equals(meta.Minecraft, Provisioner.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("该包基于 Minecraft " + meta.Minecraft + "，与 V1 目标 " +
                                        Provisioner.TargetVersion + " 不符");
                return null;
            }

            if (!options.Force && !string.IsNullOrEmpty(prepared.RemoteVersion) &&
                string.Equals(versionId, prepared.RemoteVersion, StringComparison.OrdinalIgnoreCase))
            {
                prepared.UpToDate = true;
                AnsiConsole.MarkupLine("[green]已是最新版本[/]（版本 " + Markup.Escape(versionId ?? "?") + "）");
                return prepared;
            }

            var packOptions = new PackApplier.PackOptions
            {
                GameDir = gameDir,
                ZipPath = prepared.ZipPath,
                Instance = options.Instance,
                Update = true,
                Keep = options.Keep,
                OptionsMode = options.OptionsMode,
                ServersMode = options.ServersMode,
                DryRun = options.DryRun,
                Tag = prepared.RemoteTag
            };
            prepared.PackOptions = packOptions;
            prepared.Plan = PackApplier.Preview(gameDir, prepared.ZipPath, options.Instance, packOptions);
            PrintPlan(prepared);
            return prepared;
        }

        /// <summary>按（可能由用户调整过的）保留清单/冲突策略应用。</summary>
        public static int ApplyPrepared(PreparedUpdate prepared, List<string> keep, string optionsMode, string serversMode)
        {
            prepared.PackOptions.Keep = keep ?? new List<string>();
            prepared.PackOptions.OptionsMode = optionsMode;
            prepared.PackOptions.ServersMode = serversMode;
            prepared.PackOptions.DryRun = false;

            int code = PackApplier.Run(prepared.PackOptions);
            CleanupZip(prepared);
            if (code == 0)
            {
                AnsiConsole.MarkupLine("[green]更新完成[/] 版本 " + Markup.Escape(prepared.LocalVersion ?? "?") +
                                       " → " + Markup.Escape(prepared.RemoteVersion ?? "?"));
            }

            return code;
        }

        public static void CleanupZip(PreparedUpdate prepared)
        {
            if (prepared == null || !prepared.Downloaded || string.IsNullOrEmpty(prepared.ZipPath))
            {
                return;
            }

            try
            {
                if (File.Exists(prepared.ZipPath))
                {
                    File.Delete(prepared.ZipPath);
                }
            }
            catch (IOException)
            {
            }
        }

        /// <summary>非交互入口（CLI/测试）：Prepare + 应用。</summary>
        public static async Task<int> RunAsync(UpdateOptions options)
        {
            PreparedUpdate prepared = await PrepareAsync(options).ConfigureAwait(false);
            if (prepared == null)
            {
                return 5;
            }

            if (prepared.UpToDate)
            {
                CleanupZip(prepared);
                return 0;
            }

            if (options.DryRun)
            {
                AnsiConsole.MarkupLine("[yellow]DRY-RUN[/] 未做任何修改");
                CleanupZip(prepared);
                return 0;
            }

            return ApplyPrepared(prepared, options.Keep, options.OptionsMode, options.ServersMode);
        }

        private static void PrintPlan(PreparedUpdate prepared)
        {
            PackPlan plan = prepared.Plan;
            var preview = new Table().Border(TableBorder.Rounded).Title("[cyan]更新计划[/]");
            preview.AddColumn("项");
            preview.AddColumn("值");
            preview.AddRow("本地版本", Markup.Escape(prepared.LocalVersion ?? "?"));
            preview.AddRow("远端版本", Markup.Escape(prepared.RemoteVersion ?? "?") +
                                   (prepared.RemoteTag == null ? "" : "（" + Markup.Escape(prepared.RemoteTag) + "）"));
            preview.AddRow("写入", plan.Writes.Count + "（覆盖 " + plan.Overwrites.Count + "，新建 " + plan.NewFiles.Count + "）");
            preview.AddRow("复用", plan.Reused.ToString());
            preview.AddRow("删除", plan.Deletes.Count.ToString());
            preview.AddRow("保留（keep）", plan.KeptByUser.Count.ToString());
            preview.AddRow("options/servers 冲突（保留本地）", plan.Conflicts.Count.ToString());
            AnsiConsole.Write(preview);

            if (plan.Deletes.Count > 0)
            {
                AnsiConsole.MarkupLine("[yellow]删除清单（默认删除）:[/]");
                foreach (string d in plan.Deletes.Take(40))
                {
                    AnsiConsole.MarkupLine("  [red]-[/] " + Markup.Escape(d));
                }

                if (plan.Deletes.Count > 40)
                {
                    AnsiConsole.MarkupLine("  … 共 " + plan.Deletes.Count + " 个");
                }
            }
        }
    }
}
