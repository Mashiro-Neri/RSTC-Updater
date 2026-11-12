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

    internal sealed class FlowFailureException : Exception
    {
        public readonly int Code;

        public FlowFailureException(int code, string message) : base(message)
        {
            Code = code;
        }
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

            int versionCompare = CompareVersions(check.RemoteVersion, versionId);
            check.UpdateAvailable = check.RemoteVersion != null && versionCompare > 0;
            if (!check.UpdateAvailable)
            {
                check.Message = versionCompare == 0
                    ? "已是最新版本"
                    : "远端版本（" + (check.RemoteVersion ?? "?") + "）不高于本地（" + (versionId ?? "?") +
                      "），无需更新（Beta 安装的实例在默认通道可能更低）";
            }

            return check;
        }

        /// <summary>版本比较：优先按版本号（5.5 &lt; 6.0，v4.9 &lt; v4.11），无法区分时退回字符串比较。</summary>
        private static int CompareVersions(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            {
                return 0;
            }

            if (string.IsNullOrEmpty(a))
            {
                return -1;
            }

            if (string.IsNullOrEmpty(b))
            {
                return 1;
            }

            try
            {
                int byVersion = SelfUpdater.ParseTag(a).CompareTo(SelfUpdater.ParseTag(b));
                if (byVersion != 0)
                {
                    return byVersion;
                }
            }
            catch (Exception)
            {
            }

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteFileQuiet(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(150);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(150);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        /// <summary>下载 + 计划预览；失败抛 FlowFailureException（带退出码）。</summary>
        public static async Task<PreparedUpdate> PrepareAsync(UpdateOptions options)
        {
            try
            {
                return await PrepareCoreAsync(options).ConfigureAwait(false);
            }
            catch (FlowFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new FlowFailureException(7, "更新准备失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static async Task<PreparedUpdate> PrepareCoreAsync(UpdateOptions options)
        {
            string gameDir = Path.GetFullPath(options.GameDir);
            if (!Directory.Exists(gameDir))
            {
                throw new FlowFailureException(2, "游戏目录不存在: " + gameDir);
            }

            if (!PackApplier.IsSafeInstanceName(options.Instance))
            {
                throw new FlowFailureException(2, "实例名不合法: " + options.Instance);
            }

            if (!TryReadLocal(gameDir, options.Instance, out string versionId, out string tag, out string mc))
            {
                throw new FlowFailureException(2, "本地实例不存在或状态文件缺失: " + options.Instance);
            }

            if (!string.Equals(mc, Provisioner.TargetVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowFailureException(5, "本地 MC 版本为 " + (mc ?? "未知") + "，不是 " + Provisioner.TargetVersion + "，请用「安装到新目录」");
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
                    throw new FlowFailureException(5, "无法获取远端发布信息（网络/限流且无缓存）");
                }

                prepared.RemoteTag = release.Tag;
                ReleaseAsset asset = ReleaseClient.FindPackAsset(release);
                if (asset == null)
                {
                    throw new FlowFailureException(5, "Release 中没有 RSTC-*.zip 资产");
                }

                prepared.RemoteVersion = ReleaseClient.VersionFromAssetName(asset.Name);
                int remoteCompare = CompareVersions(prepared.RemoteVersion, versionId);
                if (!options.Force && remoteCompare <= 0)
                {
                    prepared.UpToDate = true;
                    AnsiConsole.MarkupLine(remoteCompare == 0
                        ? "[green]已是最新版本[/]（本地 " + Markup.Escape(versionId ?? "?") +
                          " / 远端 " + Markup.Escape(prepared.RemoteVersion ?? "?") + "）"
                        : "[green]本地版本更高[/]（本地 " + Markup.Escape(versionId ?? "?") +
                          " / 远端 " + Markup.Escape(prepared.RemoteVersion ?? "?") + "），无需更新");
                    return prepared;
                }

                string tmpDir = Path.Combine(gameDir, ".rstc-tmp");
                Directory.CreateDirectory(tmpDir);
                string releaseJson = Path.Combine(tmpDir, "latest-release.json");
                File.WriteAllText(releaseJson, release.RawJson, new UTF8Encoding(false));
                PackAssetRef assetRef = PackFetcher.ResolveAsset(releaseJson, null);
                if (assetRef == null)
                {
                    throw new FlowFailureException(5, "Release 中没有 RSTC-*.zip 资产");
                }

                prepared.ZipPath = await PackFetcher.DownloadAssetAsync(gameDir, assetRef).ConfigureAwait(false);
                prepared.Downloaded = true;
                prepared.RemoteTag = assetRef.Tag;
            }

            try
            {
                var meta = PackApplier.ReadMetadata(prepared.ZipPath);
                if (meta == null)
                {
                    throw new FlowFailureException(5, "包缺少 modrinth.index.json");
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
                    throw new FlowFailureException(5, "该包基于 Minecraft " + meta.Minecraft + "，与 V1 目标 " + Provisioner.TargetVersion + " 不符");
                }

                if (!options.Force && CompareVersions(prepared.RemoteVersion, versionId) <= 0)
                {
                    prepared.UpToDate = true;
                    AnsiConsole.MarkupLine("[green]已是最新版本[/]（版本 " + Markup.Escape(versionId ?? "?") + "）");
                    return prepared;
                }
            }
            catch (Exception)
            {
                if (prepared.Downloaded)
                {
                    DeleteFileQuiet(prepared.ZipPath);
                }

                throw;
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
            try
            {
                prepared.Plan = PackApplier.Preview(gameDir, prepared.ZipPath, options.Instance, packOptions);
            }
            catch (Exception)
            {
                // 预览失败（如 zip 含不安全条目）：清理已下载的临时包，避免 .rstc-tmp 残留
                if (prepared.Downloaded)
                {
                    DeleteFileQuiet(prepared.ZipPath);
                }

                throw;
            }

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
                Provisioner.EnsureLanguageCompatibility(prepared.GameDir, prepared.Instance);
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
            try
            {
                PreparedUpdate prepared = await PrepareAsync(options).ConfigureAwait(false);
                if (prepared == null)
                {
                    return 7;
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
            catch (FlowFailureException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ex.Code;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("update 失败: " + ex.GetType().Name + ": " + ex.Message);
                return 7;
            }
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
