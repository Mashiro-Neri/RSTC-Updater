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
    /// <summary>
    /// 阶段 0-4：下载真实整合包（GitHub Release 资产）+ 校验 sha256 + 交给 PackApplier 铺开。
    /// Release 元数据由 `--release-json` 提供（脚本用 gh api 取，避免未认证 API 限流）；生产环境后续接入 API + 镜像。
    /// </summary>
    internal static class PackFetcher
    {
        private static readonly string[] Mirrors =
        {
            "",
            "https://gh-proxy.com/",
            "https://hk.gh-proxy.com/",
            "https://gh.llkk.cc/"
        };

        public static async Task<int> RunAsync(FetchOptions options)
        {
            string gameDir = Path.GetFullPath(options.GameDir);
            if (!Directory.Exists(gameDir))
            {
                Console.Error.WriteLine("游戏目录不存在: " + gameDir);
                return 2;
            }

            if (!File.Exists(options.ReleaseJson))
            {
                Console.Error.WriteLine("Release JSON 不存在: " + options.ReleaseJson);
                return 2;
            }

            try
            {
                return await RunCoreAsync(gameDir, options).ConfigureAwait(false);
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

        private static async Task<int> RunCoreAsync(string gameDir, FetchOptions options)
        {
            JsonDocument release;
            try
            {
                release = JsonDocument.Parse(File.ReadAllText(options.ReleaseJson, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Release JSON 解析失败: " + ex.Message);
                return 5;
            }

            using (release)
            {
                var root = release.RootElement;
                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : options.Tag;
                JsonElement? asset = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(name) || !name.StartsWith("RSTC-", StringComparison.OrdinalIgnoreCase) ||
                            !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(options.AssetName) &&
                            !string.Equals(name, options.AssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        asset = a;
                        break;
                    }
                }

                if (!asset.HasValue)
                {
                    Console.Error.WriteLine("Release 中找不到 RSTC-*.zip 资产");
                    return 5;
                }

                string assetName = asset.Value.GetProperty("name").GetString();
                long size = asset.Value.GetProperty("size").GetInt64();
                string digest = asset.Value.TryGetProperty("digest", out var d) ? d.GetString() : null;
                string url = asset.Value.GetProperty("browser_download_url").GetString();

                if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("资产缺少 sha256 digest，无法强校验: " + assetName);
                    return 5;
                }

                string expectedSha = digest.Substring("sha256:".Length);

                AnsiConsole.MarkupLine("[cyan]下载[/] " + Markup.Escape(assetName) + "（" +
                                       (size / 1048576.0).ToString("F1") + " MiB，tag " + Markup.Escape(tag ?? "?") + "）");

                string tmpDir = Path.Combine(gameDir, ".rstc-tmp");
                Directory.CreateDirectory(tmpDir);
                string zipPath = Path.Combine(tmpDir, assetName);

                long freeBefore = GetFreeSpace(gameDir);
                var sw = Stopwatch.StartNew();
                await DownloadWithMirrorsAsync(url, zipPath, size, expectedSha).ConfigureAwait(false);
                sw.Stop();
                double speed = sw.Elapsed.TotalSeconds > 0 ? size / 1048576.0 / sw.Elapsed.TotalSeconds : 0;

                long freeAfter = GetFreeSpace(gameDir);
                AnsiConsole.MarkupLine("[green]校验[/] sha256 一致；用时 " + sw.Elapsed.ToString("hh\\:mm\\:ss") +
                                       "，" + speed.ToString("F1") + " MiB/s；剩余空间 " +
                                       (freeAfter / 1073741824.0).ToString("F1") + " GiB（下载前 " +
                                       (freeBefore / 1073741824.0).ToString("F1") + " GiB）");

                var packOptions = new PackApplier.PackOptions
                {
                    GameDir = gameDir,
                    ZipPath = zipPath,
                    Instance = options.Instance,
                    Update = options.Update,
                    Keep = options.Keep,
                    OptionsMode = options.OptionsMode,
                    ServersMode = options.ServersMode,
                    DryRun = options.DryRun,
                    Tag = tag
                };

                int code = PackApplier.Run(packOptions);
                if (code != 0)
                {
                    return code;
                }

                if (!options.KeepZip)
                {
                    try
                    {
                        File.Delete(zipPath);
                    }
                    catch (IOException)
                    {
                    }
                }

                return 0;
            }
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
                    if (File.Exists(partial))
                    {
                        File.Delete(partial);
                    }

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
                    try
                    {
                        if (File.Exists(partial))
                        {
                            File.Delete(partial);
                        }
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            throw new IOException("所有镜像均失败", last);
        }

        private static long GetFreeSpace(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                var drive = new DriveInfo(root);
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
    }
}
