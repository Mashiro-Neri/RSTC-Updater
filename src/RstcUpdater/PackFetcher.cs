using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
            Exception last = null;
            foreach (string mirror in Mirrors)
            {
                string candidate = mirror + url;
                string partial = dest + ".part";
                try
                {
                    TryDelete(partial);
                    using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                    using (var response = await http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
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
                            var lastReport = Stopwatch.StartNew();
                            int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                            {
                                output.Write(buffer, 0, read);
                                hash.AppendData(buffer, 0, read);
                                total += read;
                                if (lastReport.ElapsedMilliseconds > 1000)
                                {
                                    lastReport.Restart();
                                    Console.Out.Write("\r  " + (total / 1048576.0).ToString("F1") + " / " +
                                                      (expectedSize / 1048576.0).ToString("F1") + " MiB");
                                    Console.Out.Flush();
                                }
                            }

                            Console.Out.Write("\r");
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
                    Console.Error.WriteLine("镜像失败（" + (mirror.Length == 0 ? "直连" : mirror) + "）: " + ex.Message);
                    TryDelete(partial);
                }
            }

            throw new IOException("所有镜像均失败", last);
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
