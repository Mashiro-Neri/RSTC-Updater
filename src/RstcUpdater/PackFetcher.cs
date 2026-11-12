using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>解析后的整合包资产引用。</summary>
    internal sealed class PackAssetRef
    {
        public string Tag;
        public string Name;
        public long Size;
        public string Sha256;
        public string Url;
    }

    /// <summary>V1：从 Release JSON 选资产、下载并 sha256 校验；应用交给 PackApplier。</summary>
    internal static class PackFetcher
    {
        private static readonly string[] Mirrors =
        {
            "",
            "https://gh-proxy.com/",
            "https://hk.gh-proxy.com/",
            "https://gh.llkk.cc/"
        };

        /// <summary>与 Mirrors 一一对应的显示名。</summary>
        public static readonly string[] MirrorNames =
        {
            "GitHub 直连",
            "gh-proxy.com",
            "hk.gh-proxy.com",
            "gh.llkk.cc"
        };

        /// <summary>-1 = 自动测速；>=0 = 指定 Mirrors 下标。</summary>
        public static int PreferredMirror = -1;

        internal sealed class FetchOptions
        {
            public string GameDir;
            public string ReleaseJson;
            public string AssetName;
            public string Instance;
            public string Tag;
            public bool Update;
            public List<string> Keep = new List<string>();
            public string OptionsMode = "keep";
            public string ServersMode = "keep";
            public bool DryRun;
            public bool KeepZip;
        }

        /// <summary>CLI/测试入口：解析 → 下载 → 校验 → 应用。</summary>
        public static async Task<int> RunAsync(FetchOptions options)
        {
            string gameDir = Path.GetFullPath(options.GameDir);
            if (!Directory.Exists(gameDir))
            {
                Console.Error.WriteLine("游戏目录不存在: " + gameDir);
                return 2;
            }

            try
            {
                PackAssetRef asset = ResolveAsset(options.ReleaseJson, options.AssetName);
                if (asset == null)
                {
                    return 5;
                }

                if (!string.IsNullOrEmpty(options.Tag))
                {
                    asset.Tag = options.Tag;
                }

                string zipPath = await DownloadAssetAsync(gameDir, asset).ConfigureAwait(false);

                int code = PackApplier.Run(new PackApplier.PackOptions
                {
                    GameDir = gameDir,
                    ZipPath = zipPath,
                    Instance = options.Instance,
                    Update = options.Update,
                    Keep = options.Keep,
                    OptionsMode = options.OptionsMode,
                    ServersMode = options.ServersMode,
                    DryRun = options.DryRun,
                    Tag = asset.Tag
                });

                if (code == 0 && !options.KeepZip)
                {
                    TryDelete(zipPath);
                }

                return code;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("fetch 失败: " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine("  内层: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                }

                return 7;
            }
        }

        /// <summary>从 Release JSON 里选出 RSTC-*.zip 资产（可指定 assetName）。</summary>
        public static PackAssetRef ResolveAsset(string releaseJsonPath, string assetName)
        {
            if (string.IsNullOrEmpty(releaseJsonPath) || !File.Exists(releaseJsonPath))
            {
                Console.Error.WriteLine("Release JSON 不存在: " + releaseJsonPath);
                return null;
            }

            using (var doc = JsonDocument.Parse(File.ReadAllText(releaseJsonPath, Encoding.UTF8)))
            {
                var root = doc.RootElement;
                var asset = new PackAssetRef();
                if (root.TryGetProperty("tag_name", out var tag))
                {
                    asset.Tag = tag.GetString();
                }

                if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                {
                    Console.Error.WriteLine("Release JSON 中没有 assets");
                    return null;
                }

                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name) ||
                        !name.StartsWith("RSTC-", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(assetName) &&
                        !string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    asset.Name = name;
                    asset.Size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    asset.Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    string digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        asset.Sha256 = digest.Substring("sha256:".Length);
                    }

                    return asset;
                }

                Console.Error.WriteLine("Release 中找不到 RSTC-*.zip 资产");
                return null;
            }
        }

        /// <summary>下载资产到 &lt;gameDir&gt;/.rstc-tmp/，多镜像回退 + sha256 强校验；返回本地 zip 路径。</summary>
        public static async Task<string> DownloadAssetAsync(string gameDir, PackAssetRef asset)
        {
            if (asset == null || string.IsNullOrEmpty(asset.Url))
            {
                throw new InvalidDataException("资产 URL 为空");
            }

            if (string.IsNullOrEmpty(asset.Sha256))
            {
                throw new InvalidDataException("资产缺少 sha256 digest，拒绝无强校验下载: " + asset.Name);
            }

            AnsiConsole.MarkupLine("[cyan]下载[/] " + Markup.Escape(asset.Name) + "（" +
                                   (asset.Size / 1048576.0).ToString("F1") + " MiB，tag " +
                                   Markup.Escape(asset.Tag ?? "?") + "）");

            string tmpDir = Path.Combine(gameDir, ".rstc-tmp");
            Directory.CreateDirectory(tmpDir);
            string zipPath = Path.Combine(tmpDir, asset.Name);

            long freeBefore = GetFreeSpace(gameDir);
            var sw = Stopwatch.StartNew();
            await DownloadWithMirrorsAsync(asset.Url, zipPath, asset.Size, asset.Sha256).ConfigureAwait(false);
            sw.Stop();

            double seconds = sw.Elapsed.TotalSeconds;
            double speed = seconds > 0 && asset.Size > 0 ? asset.Size / 1048576.0 / seconds : 0;
            long freeAfter = GetFreeSpace(gameDir);
            AnsiConsole.MarkupLine("[green]校验[/] sha256 一致；用时 " + sw.Elapsed.ToString("hh\\:mm\\:ss") +
                                   "，" + speed.ToString("F1") + " MiB/s；剩余空间 " +
                                   (freeAfter / 1073741824.0).ToString("F1") + " GiB（下载前 " +
                                   (freeBefore / 1073741824.0).ToString("F1") + " GiB）");
            return zipPath;
        }
        private static async Task DownloadWithMirrorsAsync(string url, string dest, long expectedSize, string expectedSha)
        {
            List<KeyValuePair<string, string>> candidates = BuildCandidates(url);
            Exception last = null;
            foreach (var candidate in candidates)
            {
                string partial = dest + ".part";
                try
                {
                    TryDelete(partial);
                    using (var http = NetworkConfig.CreateHttpClient(TimeSpan.FromMinutes(10)))
                    using (var response = await http.GetAsync(candidate.Key, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException("HTTP " + (int)response.StatusCode);
                        }

                        long? contentLength = response.Content.Headers.ContentLength;
                        if (contentLength.HasValue && expectedSize > 0 && contentLength.Value != expectedSize)
                        {
                            throw new InvalidDataException("Content-Length 不符: " + contentLength.Value);
                        }

                        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = File.Create(partial))
                        {
                            var buffer = new byte[1048576];
                            long total = 0;
                            var segment = Stopwatch.StartNew();
                            var lastReport = Stopwatch.StartNew();
                            int read;
                            while (true)
                            {
                                using (var readCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60)))
                                {
                                    try
                                    {
                                        read = await input.ReadAsync(buffer, 0, buffer.Length, readCts.Token).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw new IOException("下载 60 秒无数据（卡死），切换下载源");
                                    }
                                }

                                if (read <= 0)
                                {
                                    break;
                                }

                                output.Write(buffer, 0, read);
                                hash.AppendData(buffer, 0, read);
                                total += read;
                                if (lastReport.ElapsedMilliseconds > 500)
                                {
                                    lastReport.Restart();
                                    RenderPackProgress(candidate.Value, total, expectedSize, segment.Elapsed);
                                }
                            }

                            Console.Out.Write("\r" + new string(' ', Math.Max(20, SafeWidth() - 2)) + "\r");
                            Console.Out.Flush();
                            long elapsedMs = Math.Max(1, segment.ElapsedMilliseconds);
                            double mibPerSecond = total / 1048576.0 / (elapsedMs / 1000.0);
                            AnsiConsole.MarkupLine("[green]下载完成[/] " + Markup.Escape(candidate.Value) + "，" +
                                                   (total / 1048576.0).ToString("F1") + " MiB，" +
                                                   TimeSpan.FromMilliseconds(elapsedMs).ToString("mm\\:ss") + "，" +
                                                   mibPerSecond.ToString("F1") + " MiB/s");

                            string actual = ToHex(hash.GetHashAndReset());
                            if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("sha256 不符: " + actual);
                            }

                            if (expectedSize > 0 && total != expectedSize)
                            {
                                throw new InvalidDataException("大小不符: " + total);
                            }
                        }
                    }

                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }

                    File.Move(partial, dest);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Console.Error.WriteLine("下载源失败（" + candidate.Value + "）: " + ex.Message);
                    TryDelete(partial);
                }
            }

            throw new IOException("所有下载源均失败", last);
        }

        /// <summary>构造候选下载源；自动模式下先测速再排序。</summary>
        private static List<KeyValuePair<string, string>> BuildCandidates(string url)
        {
            var all = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < Mirrors.Length; i++)
            {
                all.Add(new KeyValuePair<string, string>(Mirrors[i] + url, MirrorNames[i]));
            }

            if (PreferredMirror >= 0 && PreferredMirror < all.Count)
            {
                AnsiConsole.MarkupLine("[cyan]下载源[/] " + Markup.Escape(all[PreferredMirror].Value) + "（手动选择）");
                return new List<KeyValuePair<string, string>> { all[PreferredMirror] };
            }

            var measured = new List<(KeyValuePair<string, string> Candidate, double Speed)>();
            foreach (var candidate in all)
            {
                double speed = ProbeSpeed(candidate.Key);
                measured.Add((candidate, speed));
            }

            string report = string.Join(" · ", measured.Select(m =>
                m.Candidate.Value + " " + (m.Speed > 0 ? m.Speed.ToString("F1") + " MiB/s" : "失败")));
            AnsiConsole.MarkupLine("[grey]测速：[/]" + Markup.Escape(report));

            var ordered = measured
                .OrderByDescending(m => m.Speed)
                .Select(m => m.Candidate)
                .ToList();
            double best = measured.Max(m => m.Speed);
            AnsiConsole.MarkupLine("[cyan]下载源[/] 自动测速选择 " + Markup.Escape(ordered[0].Value) +
                                   (best > 0 ? "（" + best.ToString("F1") + " MiB/s）" : "（测速失败，按顺序尝试）"));
            return ordered;
        }

        /// <summary>测速：读取前 256 KiB，返回 MiB/s；失败返回 -1。</summary>
        private static double ProbeSpeed(string url)
        {
            try
            {
                using (var http = NetworkConfig.CreateHttpClient(TimeSpan.FromSeconds(8)))
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(0, 262143);
                    var watch = Stopwatch.StartNew();
                    using (var response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                        .GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 206)
                        {
                            return -1;
                        }

                        using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        {
                            var buffer = new byte[262144];
                            int read = stream.ReadAsync(buffer, 0, buffer.Length).GetAwaiter().GetResult();
                            watch.Stop();
                            if (read <= 0 || watch.ElapsedMilliseconds <= 0)
                            {
                                return -1;
                            }

                            return read / 1048576.0 / (watch.ElapsedMilliseconds / 1000.0);
                        }
                    }
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void RenderPackProgress(string source, long done, long total, TimeSpan elapsed)
        {
            double speed = elapsed.TotalSeconds > 0 ? done / 1048576.0 / elapsed.TotalSeconds : 0;
            int percent = total > 0 ? (int)Math.Min(100, done * 100L / total) : 0;
            const int barWidth = 24;
            int filled = Math.Max(0, Math.Min(barWidth, percent * barWidth / 100));
            string bar = new string('#', filled) + new string('-', barWidth - filled);
            string eta = "--:--";
            if (speed > 0.05 && total > done)
            {
                int seconds = (int)Math.Min(359999, (total - done) / 1048576.0 / speed);
                eta = TimeSpan.FromSeconds(seconds).ToString("mm\\:ss");
            }

            string line = string.Format(
                "\r  [{0}] {1,3}%  {2:F1}/{3:F1} MB  {4:F1} MB/s  剩余 {5}  {6}   ",
                bar, percent, done / 1048576.0, total / 1048576.0, speed, eta, source);
            int width = SafeWidth();
            if (width > 20 && line.Length > width - 1)
            {
                line = line.Substring(0, width - 2);
            }

            Console.Out.Write(line);
            Console.Out.Flush();
        }

        private static int SafeWidth()
        {
            try
            {
                return Console.WindowWidth > 20 ? Console.WindowWidth : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }

        private static long GetFreeSpace(string path)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)));
                return drive.AvailableFreeSpace;
            }
            catch
            {
                return 0;
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
