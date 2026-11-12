using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>Fabric profile 数据（供实例 json 生成使用）。</summary>
    internal sealed class FabricProfile
    {
        public JsonObject Root;
        public string MainClass;
        public JsonArray Libraries;
        public List<string> JvmArgs;
    }

    /// <summary>
    /// Fabric profile/库安装 + 实例 json 生成（阶段 0-2 的实现，V1 安装流程复用）。
    /// </summary>
    internal static class Provisioner
    {
        public const string TargetVersion = "26.3";
        public const string FabricLoader = "0.20.1";
        public const string FabricVersionId = "fabric-loader-0.20.1-26.3";
        private const string FabricProfileUrl = "https://meta.fabricmc.net/v2/versions/loader/26.3/0.20.1/profile/json";
        private const string IntermediaryUrl = "https://maven.fabricmc.net/net/fabricmc/intermediary/0.0.0/intermediary-0.0.0.jar";

        private static readonly string[] Variants = { "A2", "A1", "B" };

        // ==================== 阶段 0-2 CLI（保留） ====================

        public static async Task<int> RunAsync(ProvisionOptions options)
        {
            try
            {
                return await RunCoreAsync(options).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("provision 失败: " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine("  内层: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                }

                return 7;
            }
        }

        private static async Task<int> RunCoreAsync(ProvisionOptions options)
        {
            string gameDir = Path.GetFullPath(options.GameDir);
            if (!Directory.Exists(gameDir))
            {
                Console.Error.WriteLine("游戏目录不存在: " + gameDir + "（先用 --install 安装 26.3）");
                return 2;
            }

            string vanillaJsonPath = Path.Combine(gameDir, "versions", TargetVersion, TargetVersion + ".json");
            if (!File.Exists(vanillaJsonPath))
            {
                Console.Error.WriteLine("缺少 " + vanillaJsonPath + "（先用 --install 安装 26.3）");
                return 2;
            }

            FabricProfile fabric = await EnsureFabricAsync(gameDir, options.Reuse).ConfigureAwait(false);
            if (fabric == null)
            {
                return 5;
            }

            var created = new List<string>();
            foreach (string variant in Variants)
            {
                string instanceName = "RSTC-0-2-" + variant;
                if (!CreateInstance(gameDir, instanceName, variant, fabric))
                {
                    return 7;
                }

                string dir = Path.Combine(gameDir, "versions", instanceName);
                if (options.SampleMods && Directory.Exists(options.ModSource))
                {
                    foreach (string mod in Directory.EnumerateFiles(options.ModSource, "*.jar")
                                 .OrderBy(f => new FileInfo(f).Length)
                                 .Take(3))
                    {
                        File.Copy(mod, Path.Combine(dir, "mods", Path.GetFileName(mod)), true);
                    }
                }

                File.WriteAllText(Path.Combine(dir, "RSTC_version.txt"), "test-0-2-" + variant, new UTF8Encoding(false));
                created.Add(variant + " -> " + dir);
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("变体");
            table.AddColumn("实例目录");
            table.AddColumn("要点");
            table.AddRow("A2", "RSTC-0-2-A2", "平铺自包含：复制 41 MB jar + 122 库");
            table.AddRow("A1", "RSTC-0-2-A1", "平铺：jar 指向 26.3，不复制 jar");
            table.AddRow("B", "RSTC-0-2-B", "inheritsFrom=26.3，只带 Fabric 库");
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("样例 mod: " + (options.SampleMods ? "已复制 3 个（源 " + Markup.Escape(options.ModSource) + "）" : "未复制"));
            AnsiConsole.MarkupLine("[green]PROVISION=PASS[/]");
            return 0;
        }

        // ==================== V1 可复用 API ====================

        /// <summary>确保 Fabric profile + 7 库 + intermediary 就绪；失败返回 null。</summary>
        public static async Task<FabricProfile> EnsureFabricAsync(string gameDir, bool reuse)
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("RSTC-Updater/5.0-provision");
                    FabricProfile fabric = null;
                    for (int attempt = 1; attempt <= 3 && fabric == null; attempt++)
                    {
                        fabric = await FetchProfileAsync(http).ConfigureAwait(false);
                        if (fabric == null)
                        {
                            await Task.Delay(1500).ConfigureAwait(false);
                        }
                    }

                    if (fabric == null)
                    {
                        return null;
                    }

                    string fabricDir = Path.Combine(gameDir, "versions", FabricVersionId);
                    Directory.CreateDirectory(fabricDir);
                    File.WriteAllText(
                        Path.Combine(fabricDir, FabricVersionId + ".json"),
                        fabric.Root.ToJsonString(),
                        new UTF8Encoding(false));

                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            await DownloadLibrariesAsync(gameDir, http, fabric, reuse).ConfigureAwait(false);
                            return fabric;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("Fabric 库下载失败（" + attempt + "/2）: " + ex.GetType().Name + ": " + ex.Message);
                            if (attempt == 2)
                            {
                                return null;
                            }

                            await Task.Delay(1500).ConfigureAwait(false);
                        }
                    }

                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fabric 准备失败: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>创建实例目录（json + A2 jar 副本 + mods/config + 启动器配置）。</summary>
        public static bool CreateInstance(string gameDir, string instanceName, string variant, FabricProfile fabric)
        {
            try
            {
                string vanillaJsonPath = Path.Combine(gameDir, "versions", TargetVersion, TargetVersion + ".json");
                if (!File.Exists(vanillaJsonPath))
                {
                    Console.Error.WriteLine("缺少 26.3 版本 json: " + vanillaJsonPath);
                    return false;
                }

                var vanilla = JsonNode.Parse(File.ReadAllText(vanillaJsonPath, Encoding.UTF8)) as JsonObject;
                if (vanilla == null)
                {
                    Console.Error.WriteLine("26.3.json 解析失败");
                    return false;
                }

                string dir = Path.Combine(gameDir, "versions", instanceName);
                Directory.CreateDirectory(dir);

                JsonObject instance = BuildInstance(instanceName, variant, vanilla, fabric.MainClass, fabric.Libraries, fabric.JvmArgs);
                File.WriteAllText(Path.Combine(dir, instanceName + ".json"), instance.ToJsonString(), new UTF8Encoding(false));

                if (variant == "A2")
                {
                    File.Copy(
                        Path.Combine(gameDir, "versions", TargetVersion, TargetVersion + ".jar"),
                        Path.Combine(dir, instanceName + ".jar"),
                        true);
                }

                Directory.CreateDirectory(Path.Combine(dir, "mods"));
                Directory.CreateDirectory(Path.Combine(dir, "config"));
                WriteLauncherConfigs(dir, instanceName, variant, vanilla["releaseTime"]?.GetValue<string>());
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("创建实例失败: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static async Task<FabricProfile> FetchProfileAsync(HttpClient http)
        {
            try
            {
                string profileJson = await http.GetStringAsync(FabricProfileUrl).ConfigureAwait(false);
                var root = JsonNode.Parse(profileJson) as JsonObject;
                if (root == null)
                {
                    return null;
                }

                string mainClass = root["mainClass"]?.GetValue<string>();
                if (string.IsNullOrEmpty(mainClass))
                {
                    return null;
                }

                return new FabricProfile
                {
                    Root = root,
                    MainClass = mainClass,
                    Libraries = root["libraries"] as JsonArray ?? new JsonArray(),
                    JvmArgs = GetStringArray(root["arguments"]?["jvm"])
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("获取 Fabric profile 失败: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static async Task DownloadLibrariesAsync(string gameDir, HttpClient http, FabricProfile fabric, bool reuse)
        {
            int downloaded = 0;
            int skipped = 0;
            const string defaultMaven = "https://maven.fabricmc.net/";
            foreach (var libNode in fabric.Libraries)
            {
                var lib = libNode as JsonObject;
                string name = lib?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string mavenUrl = lib["url"]?.GetValue<string>() ?? defaultMaven;
                string relative = MavenRelativePath(name);
                string dest = Path.Combine(gameDir, "libraries", relative.Replace('/', '\\'));
                long size = lib["size"] != null ? lib["size"].GetValue<long>() : 0;
                string sha1 = lib["sha1"]?.GetValue<string>();
                if (await DownloadAsync(http, mavenUrl.TrimEnd('/') + "/" + relative, dest, size, sha1, reuse).ConfigureAwait(false))
                {
                    downloaded++;
                }
                else
                {
                    skipped++;
                }
            }

            string intermediaryDest = Path.Combine(gameDir, "libraries", "net", "fabricmc", "intermediary", "0.0.0", "intermediary-0.0.0.jar");
            if (await DownloadAsync(http, IntermediaryUrl, intermediaryDest, 572, null, reuse).ConfigureAwait(false))
            {
                downloaded++;
            }
            else
            {
                skipped++;
            }

            Console.WriteLine("Fabric 库: 新增 " + downloaded + "，复用 " + skipped + "（含 intermediary）");
        }

        private static JsonObject BuildInstance(
            string name,
            string variant,
            JsonObject vanilla,
            string fabricMainClass,
            JsonArray fabricLibs,
            List<string> fabricJvmArgs)
        {
            if (variant == "B")
            {
                var b = new JsonObject
                {
                    ["id"] = name,
                    ["inheritsFrom"] = TargetVersion,
                    ["type"] = "release",
                    ["mainClass"] = fabricMainClass,
                    ["releaseTime"] = vanilla["releaseTime"]?.GetValue<string>(),
                    ["time"] = vanilla["time"]?.GetValue<string>()
                };

                var bArgs = new JsonObject();
                if (fabricJvmArgs.Count > 0)
                {
                    var bJvm = new JsonArray();
                    foreach (string arg in fabricJvmArgs)
                    {
                        bJvm.Add(arg);
                    }

                    bArgs["jvm"] = bJvm;
                }

                b["arguments"] = bArgs;
                b["libraries"] = MergeLibraries(null, fabricLibs);
                return b;
            }

            var node = (JsonObject)vanilla.DeepClone();
            node.Remove("inheritsFrom");
            node["id"] = name;
            node["type"] = "release";
            node["mainClass"] = fabricMainClass;
            node["jar"] = variant == "A2" ? name : TargetVersion;
            node["libraries"] = MergeLibraries(vanilla["libraries"] as JsonArray, fabricLibs);

            var arguments = node["arguments"] as JsonObject;
            arguments = arguments == null ? new JsonObject() : (JsonObject)arguments.DeepClone();
            var jvm = arguments["jvm"] as JsonArray;
            if (jvm == null)
            {
                jvm = new JsonArray();
                arguments["jvm"] = jvm;
            }

            foreach (string arg in fabricJvmArgs)
            {
                bool exists = jvm.Any(n => n is JsonValue value && value.TryGetValue<string>(out string s) && s == arg);
                if (!exists)
                {
                    jvm.Add(arg);
                }
            }

            node["arguments"] = arguments;
            return node;
        }

        private static JsonArray MergeLibraries(JsonArray baseLibs, JsonArray fabricLibs)
        {
            var merged = new JsonArray();
            if (baseLibs != null)
            {
                foreach (var lib in baseLibs)
                {
                    merged.Add(lib?.DeepClone());
                }
            }

            foreach (var lib in fabricLibs)
            {
                merged.Add(lib?.DeepClone());
            }

            merged.Add(new JsonObject
            {
                ["name"] = "net.fabricmc:intermediary:0.0.0",
                ["url"] = "https://maven.fabricmc.net/"
            });
            return merged;
        }

        private static List<string> GetStringArray(JsonNode node)
        {
            var result = new List<string>();
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue<string>(out string s))
                    {
                        result.Add(s);
                    }
                }
            }

            return result;
        }

        private static string MavenRelativePath(string mavenName)
        {
            string[] parts = mavenName.Split(':');
            if (parts.Length < 3)
            {
                throw new FormatException("无法解析 Maven 坐标: " + mavenName);
            }

            string group = parts[0].Replace('.', '/');
            string artifact = parts[1];
            string version = parts[2];
            string classifier = parts.Length > 3 ? "-" + parts[3] : "";
            return group + "/" + artifact + "/" + version + "/" + artifact + "-" + version + classifier + ".jar";
        }

        private static async Task<bool> DownloadAsync(
            HttpClient http,
            string url,
            string dest,
            long expectedSize,
            string expectedSha1,
            bool reuse)
        {
            if (reuse && File.Exists(dest) && VerifyFile(dest, expectedSize, expectedSha1))
            {
                return false;
            }

            byte[] bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (expectedSize > 0 && bytes.LongLength != expectedSize)
            {
                throw new InvalidDataException("大小不符: " + url + " 期望 " + expectedSize + " 实际 " + bytes.LongLength);
            }

            if (!string.IsNullOrEmpty(expectedSha1))
            {
                string actual = ToHex(SHA1.Create().ComputeHash(bytes));
                if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SHA1 不符: " + url);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
            File.WriteAllBytes(dest, bytes);
            return true;
        }

        private static bool VerifyFile(string path, long expectedSize, string expectedSha1)
        {
            var info = new FileInfo(path);
            if (expectedSize > 0 && info.Length != expectedSize)
            {
                return false;
            }

            if (string.IsNullOrEmpty(expectedSha1))
            {
                return true;
            }

            using (var stream = File.OpenRead(path))
            {
                string actual = ToHex(SHA1.Create().ComputeHash(stream));
                return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// 写启动器隔离/识别配置。关键点：PCL2 只在“首次启动”（Setup.ini 缺 State）时改写
        /// 游戏语言，且对无法识别版本的实例会写大写语言码（如 zh_CN），26.3 只认小写 → 英文。
        /// 因此安装时就把 PCL 所需的完整字段（含 State:9）写好，让 PCL 走“并非首次启动”。
        /// </summary>
        private static void WriteLauncherConfigs(string instanceDir, string instanceName, string variant, string releaseTime)
        {
            string pclDir = Path.Combine(instanceDir, "PCL");
            Directory.CreateDirectory(pclDir);
            string setupPath = Path.Combine(pclDir, "Setup.ini");

            var values = new List<string>();
            if (File.Exists(setupPath))
            {
                values.AddRange(File.ReadAllLines(setupPath, Encoding.UTF8));
            }

            UpsertIni(values, "VersionLiteLoader", "False");
            UpsertIni(values, "VersionFabric", FabricLoader);
            UpsertIni(values, "VersionVanillaName", TargetVersion);
            UpsertIni(values, "VersionVanilla", TargetVersion + ".0");
            UpsertIni(values, "Info", "正式版 " + TargetVersion + ", Fabric " + FabricLoader);
            UpsertIni(values, "VersionArgumentIndieV2", "True");
            UpsertIni(values, "VersionArgumentIndie", "1");
            UpsertIni(values, "State", "9");
            UpsertIni(values, "Logo", "pack://application:,,,/Plain Craft Launcher 2;component/Images/Blocks/Fabric.png");
            if (!string.IsNullOrEmpty(releaseTime) &&
                DateTime.TryParse(releaseTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime released))
            {
                UpsertIni(values, "ReleaseTime", released.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            File.WriteAllText(setupPath, string.Join("\r\n", values) + "\r\n", new UTF8Encoding(false));

            string hmclPath = Path.Combine(instanceDir, "hmclversion.cfg");
            if (!File.Exists(hmclPath))
            {
                File.WriteAllText(
                    hmclPath,
                    "{\r\n  \"usesGlobal\": false,\r\n  \"gameDirType\": 1\r\n}\r\n",
                    new UTF8Encoding(false));
            }
        }

        private static void UpsertIni(List<string> lines, string key, string value)
        {
            string prefix = key + ":";
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = prefix + value;
                    return;
                }
            }

            lines.Add(prefix + value);
        }

        internal sealed class ProvisionOptions
        {
            public string GameDir;
            public string ModSource;
            public bool SampleMods;
            public bool Reuse;
        }
    }
}
