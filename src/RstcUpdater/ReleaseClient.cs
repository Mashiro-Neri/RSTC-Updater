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
        public DateTime? FetchedAt;
    }

    /// <summary>V1：GitHub Release 读取（带本地缓存，403/断网时回退缓存并标记来源）。</summary>
    internal static class ReleaseClient
    {
        public const string PackRepo = "Mashiro-Neri/Redstone-Town-Client_update_modpack";
        public const string UpdaterRepo = "Mashiro-Neri/RSTC-Updater";

        public static async Task<ReleaseInfo> GetLatestAsync(string repo)
        {
            string cachePath = CachePath(repo);
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("RSTC-Updater/5.0");
                    string token = Environment.GetEnvironmentVariable("RSTC_GITHUB_TOKEN");
                    if (!string.IsNullOrEmpty(token))
                    {
                        http.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }

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

                return info;
            }
        }

        private static string CachePath(string repo)
        {
            string safe = repo.Replace('/', '_');
            return Path.Combine(AppConfig.ConfigDir, "cache", safe + ".json");
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
