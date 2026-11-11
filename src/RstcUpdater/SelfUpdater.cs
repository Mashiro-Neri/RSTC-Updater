using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    internal sealed class SelfUpdateCheck
    {
        public Version Current;
        public string LatestTag;
        public Version LatestVersion;
        public string AssetName;
        public long AssetSize;
        public string AssetSha256;
        public string AssetUrl;
        public bool UpdateAvailable;
        public string Message;
    }

    /// <summary>V1 自更新：检查更新器仓库 Release（v5 资产形态待发布器任务）。</summary>
    internal static class SelfUpdater
    {
        public const string AssetPrefix = "RSTC-Updater-";

        public static Version CurrentVersion
        {
            get
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(5, 0, 0, 0);
                return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            }
        }

        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return null;
            }

            string raw = tag.TrimStart('v', 'V');
            string[] parts = raw.Split(new[] { '.', '-', '+' }, StringSplitOptions.RemoveEmptyEntries);
            var numbers = new List<int>();
            foreach (string part in parts)
            {
                if (numbers.Count >= 3)
                {
                    break;
                }

                if (int.TryParse(part, out int n))
                {
                    numbers.Add(n);
                }
                else
                {
                    break;
                }
            }

            while (numbers.Count < 3)
            {
                numbers.Add(0);
            }

            return new Version(numbers[0], numbers[1], numbers[2]);
        }

        public static async Task<SelfUpdateCheck> CheckAsync()
        {
            var check = new SelfUpdateCheck { Current = CurrentVersion };
            ReleaseInfo release = await ReleaseClient.GetLatestAsync(ReleaseClient.UpdaterRepo).ConfigureAwait(false);
            if (release == null)
            {
                check.Message = "无法获取更新器 Release（网络/限流且无缓存）";
                return check;
            }

            check.LatestTag = release.Tag;
            check.LatestVersion = ParseTag(release.Tag);

            foreach (var asset in release.Assets)
            {
                if (asset.Name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    check.AssetName = asset.Name;
                    check.AssetSize = asset.Size;
                    check.AssetUrl = asset.Url;
                    if (!string.IsNullOrEmpty(asset.Digest) &&
                        asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        check.AssetSha256 = asset.Digest.Substring("sha256:".Length);
                    }

                    break;
                }
            }

            if (check.LatestVersion != null && check.LatestVersion > check.Current)
            {
                check.UpdateAvailable = check.AssetName != null;
                check.Message = check.AssetName == null
                    ? "发现新版本 " + release.Tag + "，但 Release 中没有 " + AssetPrefix + "*.zip 资产（发布器未就绪）"
                    : "发现新版本 " + release.Tag;
            }
            else
            {
                check.Message = "已是最新版本（当前 " + check.Current + "）";
            }

            return check;
        }

        /// <summary>
        /// 下载并替换自身：sha256 强校验 → 解压 → 校验新 exe → 备份当前安装目录 →
        /// 用 .bat 在进程退出后 robocopy 覆盖，失败则回滚备份并重启。
        /// </summary>
        public static async Task<int> ApplyAsync(SelfUpdateCheck check)
        {
            if (check == null || !check.UpdateAvailable || string.IsNullOrEmpty(check.AssetUrl))
            {
                Console.Error.WriteLine("没有可应用的更新");
                return 2;
            }

            string exePath = Assembly.GetExecutingAssembly().Location;
            string installDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            string tempBase = Path.Combine(Path.GetTempPath(), "RSTC-Updater-selfupdate-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string zipPath = Path.Combine(tempBase, check.AssetName);
            Directory.CreateDirectory(tempBase);

            var asset = new PackAssetRef
            {
                Name = check.AssetName,
                Size = check.AssetSize,
                Sha256 = check.AssetSha256,
                Url = check.AssetUrl,
                Tag = check.LatestTag
            };
            await PackFetcher.DownloadAssetAsync(tempBase, asset).ConfigureAwait(false);

            string extractDir = Path.Combine(tempBase, "new");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            if (!File.Exists(Path.Combine(extractDir, "RstcUpdater.exe")))
            {
                Console.Error.WriteLine("新版本 zip 中缺少 RstcUpdater.exe，拒绝替换");
                return 7;
            }

            // 备份当前安装目录（仅文件，跳过 .selfupdate 临时目录）
            string backupDir = Path.Combine(tempBase, "backup");
            Directory.CreateDirectory(backupDir);
            int backedUp = 0;
            foreach (string file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                if (file.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string rel = file.Substring(installDir.Length).TrimStart('\\');
                string dst = Path.Combine(backupDir, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? backupDir);
                    File.Copy(file, dst, true);
                    backedUp++;
                }
                catch (IOException)
                {
                }
            }

            string bat = Path.Combine(tempBase, "replace.bat");
            var script = new StringBuilder();
            script.AppendLine("@echo off");
            script.AppendLine("timeout /t 2 /nobreak >nul");
            script.AppendLine("robocopy \"" + extractDir + "\" \"" + installDir + "\" /E /NFL /NDL /NJH /NJS /NC /NS >nul");
            script.AppendLine("if errorlevel 8 goto fail");
            script.AppendLine("start \"\" \"" + exePath + "\"");
            script.AppendLine("goto done");
            script.AppendLine(":fail");
            script.AppendLine("robocopy \"" + backupDir + "\" \"" + installDir + "\" /E /NFL /NDL /NJH /NJS /NC /NS >nul");
            script.AppendLine("start \"\" \"" + exePath + "\"");
            script.AppendLine(":done");
            script.AppendLine("timeout /t 3 /nobreak >nul");
            script.AppendLine("rd /s /q \"" + tempBase + "\"");
            script.AppendLine("del \"%~f0\"");
            File.WriteAllText(bat, script.ToString(), Encoding.Default);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + bat + "\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            AnsiConsole.MarkupLine("[green]已下载新版本（备份 " + backedUp + " 个文件），程序退出后将自动替换并重启；失败会回滚。[/]");
            return 0;
        }
    }
}
