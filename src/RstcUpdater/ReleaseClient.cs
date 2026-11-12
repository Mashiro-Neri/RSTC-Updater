using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RstcUpdater
{
    internal sealed class ReleaseAsset
    {
        public string Name;
        public long Size;
        public string Digest;
        public string Url;
    }

    internal sealed class ReleaseInfo
    {
        public string Repo;
        public string Tag;
        public string Name;
        public string RawJson;
        public DateTime? PublishedAt;
        public List<ReleaseAsset> Assets = new List<ReleaseAsset>();
        public bool FromCache;
        public bool IsPrerelease;
        public DateTime? FetchedAt;
    }

    /// <summary>V1：GitHub Release 读取（带本地缓存，403/断网时回退缓存并标记来源）。</summary>
    internal static class ReleaseClient
    {
        public const string PackRepo = "Mashiro-Neri/Redstone-Town-Client_update_modpack";
        public const string UpdaterRepo = "Mashiro-Neri/RSTC-Updater";

        /// <summary>内测通道：设置 RSTC_RELEASE_CHANNEL=beta 时包含 Pre-release（默认只看正式版）。</summary>
        public static bool BetaChannel
        {
            get
            {
                return string.Equals(Environment.GetEnvironmentVariable("RSTC_RELEASE_CHANNEL"), "beta",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string ChannelName
        {
            get { return BetaChannel ? "beta" : "stable"; }
        }

        public static async Task<ReleaseInfo> GetLatestAsync(string repo)
        {
            if (BetaChannel)
            {
                return await GetLatestBetaAsync(repo).ConfigureAwait(false);
            }

            string cachePath = CachePath(repo, false);
            try
            {
                using (var http = CreateHttp())
                {
                    string json = await http.GetStringAsync("https://api.github.com/repos/" + repo + "/releases/latest")
                        .ConfigureAwait(false);
                    ReleaseInfo info = Parse(repo, json, false);
                    SaveCache(cachePath, json);
                    return info;
                }
            }
            catch (Exception)
            {
                ReleaseInfo cached = LoadCache(repo, cachePath);
                if (cached != null)
                {
                    return cached;
                }

                return null;
            }
        }

        /// <summary>内测通道：取 releases 列表里 tag 最新的非 draft 发布（含 Pre-release）。</summary>
        private static async Task<ReleaseInfo> GetLatestBetaAsync(string repo)
        {
            string cachePath = CachePath(repo, true);
            try
            {
                using (var http = CreateHttp())
                {
                    string json = await http.GetStringAsync(
                        "https://api.github.com/repos/" + repo + "/releases?per_page=30").ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        {
                            throw new FormatException("releases 不是数组");
                        }

                        JsonElement? best = null;
                        string bestTag = null;
                        foreach (var release in doc.RootElement.EnumerateArray())
                        {
                            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                            {
                                continue;
                            }

                            string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                            if (string.IsNullOrEmpty(tag))
                            {
                                continue;
                            }

                            if (bestTag == null || string.Compare(tag, bestTag, StringComparison.OrdinalIgnoreCase) > 0)
                            {
                                best = release;
                                bestTag = tag;
                            }
                        }

                        if (best == null)
                        {
                            throw new InvalidDataException("没有可用发布");
                        }

                        string chosen = best.Value.GetRawText();
                        ReleaseInfo info = Parse(repo, chosen, false);
                        SaveCache(cachePath, chosen);
                        return info;
                    }
                }
            }
            catch (Exception)
            {
                ReleaseInfo cached = LoadCache(repo, cachePath);
                if (cached != null)
                {
                    return cached;
                }

                return null;
            }
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RSTC-Updater/5.0");
            string token = Environment.GetEnvironmentVariable("RSTC_GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            return http;
        }

        public static ReleaseAsset FindPackAsset(ReleaseInfo info)
        {
            if (info == null)
            {
                return null;
            }

            foreach (var asset in info.Assets)
            {
                if (asset.Name.StartsWith("RSTC-", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }

            return null;
        }

        public static string VersionFromAssetName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            string trimmed = name;
            if (trimmed.StartsWith("RSTC-", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("RSTC-".Length);
            }

            if (trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 4);
            }

            return trimmed;
        }

        public static ReleaseInfo Parse(string repo, string json, bool fromCache)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var info = new ReleaseInfo { Repo = repo, RawJson = json, FromCache = fromCache };
                if (root.TryGetProperty("tag_name", out var tag))
                {
                    info.Tag = tag.GetString();
                }

                if (root.TryGetProperty("name", out var name))
                {
                    info.Name = name.GetString();
                }

                if (root.TryGetProperty("published_at", out var published) &&
                    DateTime.TryParse(published.GetString(), out DateTime publishedAt))
                {
                    info.PublishedAt = publishedAt;
                }

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var asset = new ReleaseAsset();
                        if (a.TryGetProperty("name", out var n))
                        {
                            asset.Name = n.GetString();
                        }

                        if (a.TryGetProperty("size", out var s))
                        {
                            asset.Size = s.GetInt64();
                        }

                        if (a.TryGetProperty("digest", out var d))
                        {
                            asset.Digest = d.GetString();
                        }

                        if (a.TryGetProperty("browser_download_url", out var u))
                        {
                            asset.Url = u.GetString();
                        }

                        if (!string.IsNullOrEmpty(asset.Name))
                        {
                            info.Assets.Add(asset);
                        }
                    }
                }

                if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                {
                    info.IsPrerelease = true;
                }

                return info;
            }
        }

        private static string CachePath(string repo, bool beta)
        {
            string safe = repo.Replace('/', '_');
            string suffix = beta ? ".beta.json" : ".json";
            return Path.Combine(AppConfig.ConfigDir, "cache", safe + suffix);
        }

        private static void SaveCache(string path, string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppConfig.ConfigDir);
                var root = new System.Text.Json.Nodes.JsonObject
                {
                    ["fetchedAt"] = DateTime.Now.ToString("o"),
                    ["json"] = json
                };
                File.WriteAllText(path, root.ToJsonString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }

        private static ReleaseInfo LoadCache(string repo, string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))
                    as System.Text.Json.Nodes.JsonObject;
                if (root == null || root["json"] == null)
                {
                    return null;
                }

                var info = Parse(repo, root["json"].GetValue<string>(), true);
                if (root["fetchedAt"] != null &&
                    DateTime.TryParse(root["fetchedAt"].GetValue<string>(), out DateTime fetchedAt))
                {
                    info.FetchedAt = fetchedAt;
                }

                return info;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
