using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// 阶段 0-2：自建 Fabric profile + 库，生成 A2/A1/B 三份实例，并写入启动器隔离配置。
    /// 只在独立临时 .minecraft 内工作；不触碰玩家实例。
    /// </summary>
    internal static class Provisioner
    {
        public const string TargetVersion = "26.3";
        public const string FabricLoader = "0.20.1";
        public const string FabricVersionId = "fabric-loader-0.20.1-26.3";
        private const string FabricProfileUrl = "https://meta.fabricmc.net/v2/versions/loader/26.3/0.20.1/profile/json";
        private const string IntermediaryUrl = "https://maven.fabricmc.net/net/fabricmc/intermediary/0.0.0/intermediary-0.0.0.jar";

        private static readonly string[] Variants = { "A2", "A1", "B" };

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

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("RSTC-Updater/5.0-provision");

                JsonObject profile;
                try
                {
                    string profileJson = await http.GetStringAsync(FabricProfileUrl).ConfigureAwait(false);
                    profile = JsonNode.Parse(profileJson) as JsonObject
                              ?? throw new FormatException("profile 不是 JSON 对象");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("获取 Fabric profile 失败: " + ex.GetType().Name + ": " + ex.Message);
                    return 5;
                }

                string fabricMainClass = profile["mainClass"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(fabricMainClass))
                {
                    Console.Error.WriteLine("Fabric profile 缺少 mainClass");
                    return 5;
                }

                var fabricLibs = profile["libraries"] as JsonArray ?? new JsonArray();
                var fabricJvmArgs = GetStringArray(profile["arguments"]?["jvm"]);

                // 1. 写 Fabric profile 版本目录（版本列表会多一项，完成页会说明）
                string fabricDir = Path.Combine(gameDir, "versions", FabricVersionId);
                Directory.CreateDirectory(fabricDir);
                string fabricJsonPath = Path.Combine(fabricDir, FabricVersionId + ".json");
                File.WriteAllText(fabricJsonPath, profile.ToJsonString(), new UTF8Encoding(false));
                AnsiConsole.MarkupLine("[green]Fabric profile[/] 已写入 " + Markup.Escape(fabricJsonPath));

                // 2. 下载 Fabric 库 + intermediary 占位
                int downloaded = 0;
                int skipped = 0;
                foreach (var libNode in fabricLibs)
                {
                    var lib = libNode as JsonObject;
                    string name = lib?["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    string mavenUrl = lib["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/";
                    string relative = MavenRelativePath(name);
                    string dest = Path.Combine(gameDir, "libraries", relative.Replace('/', '\\'));
                    long size = 0;
                    if (lib["size"] != null)
                    {
                        size = lib["size"].GetValue<long>();
                    }

                    string sha1 = lib["sha1"]?.GetValue<string>();
                    if (await DownloadAsync(http, mavenUrl.TrimEnd('/') + "/" + relative, dest, size, sha1, options.Reuse).ConfigureAwait(false))
                    {
                        downloaded++;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                string intermediaryDest = Path.Combine(gameDir, "libraries", "net", "fabricmc", "intermediary", "0.0.0", "intermediary-0.0.0.jar");
                if (await DownloadAsync(http, IntermediaryUrl, intermediaryDest, 572, null, options.Reuse).ConfigureAwait(false))
                {
                    downloaded++;
                }
                else
                {
                    skipped++;
                }

                AnsiConsole.MarkupLine("[green]Fabric 库[/] 新增 " + downloaded + "，复用 " + skipped + "（含 intermediary 占位）");

                // 3. 三份实例
                JsonObject vanilla = JsonNode.Parse(File.ReadAllText(vanillaJsonPath, Encoding.UTF8)) as JsonObject
                                     ?? throw new FormatException("26.3.json 不是 JSON 对象");

                var created = new List<string>();
                foreach (string variant in Variants)
                {
                    string instanceName = "RSTC-0-2-" + variant;
                    string dir = Path.Combine(gameDir, "versions", instanceName);
                    Directory.CreateDirectory(dir);

                    JsonObject instance = BuildInstance(instanceName, variant, vanilla, fabricMainClass, fabricLibs, fabricJvmArgs);
                    File.WriteAllText(
                        Path.Combine(dir, instanceName + ".json"),
                        instance.ToJsonString(),
                        new UTF8Encoding(false));

                    if (variant == "A2")
                    {
                        File.Copy(
                            Path.Combine(gameDir, "versions", TargetVersion, TargetVersion + ".jar"),
                            Path.Combine(dir, instanceName + ".jar"),
                            true);
                    }

                    Directory.CreateDirectory(Path.Combine(dir, "mods"));
                    Directory.CreateDirectory(Path.Combine(dir, "config"));

                    if (options.SampleMods && Directory.Exists(options.ModSource))
                    {
                        foreach (string mod in Directory.EnumerateFiles(options.ModSource, "*.jar")
                                     .OrderBy(f => new FileInfo(f).Length)
                                     .Take(3))
                        {
                            File.Copy(mod, Path.Combine(dir, "mods", Path.GetFileName(mod)), true);
                        }
                    }

                    WriteLauncherConfigs(dir, instanceName, variant);
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
                AnsiConsole.MarkupLine("下一步：用 PCL2 / HMCL 指向 " + Markup.Escape(gameDir) + "，分别启动三个实例，确认 mod 生效。");
                return 0;
            }
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

            // 深拷贝原版 arguments（保留 rules 对象等条件 JVM 参数），只追加 Fabric 的 JVM 参数
            var arguments = node["arguments"] as JsonObject;
            if (arguments == null)
            {
                arguments = new JsonObject();
            }
            else
            {
                arguments = (JsonObject)arguments.DeepClone();
            }

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

        private static void JArrayAppendDistinct(List<string> target, List<string> additions)
        {
            foreach (string arg in additions)
            {
                if (!target.Contains(arg))
                {
                    target.Add(arg);
                }
            }
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

        private static void WriteLauncherConfigs(string instanceDir, string instanceName, string variant)
        {
            string pclDir = Path.Combine(instanceDir, "PCL");
            Directory.CreateDirectory(pclDir);
            string setupPath = Path.Combine(pclDir, "Setup.ini");

            // PCL：已存在则只补隔离键，保留玩家/启动器原有字段；不存在才写最小模板
            var values = new List<string>();
            if (File.Exists(setupPath))
            {
                foreach (string line in File.ReadAllLines(setupPath, Encoding.UTF8))
                {
                    values.Add(line);
                }
            }

            UpsertIni(values, "VersionVanillaName", TargetVersion);
            UpsertIni(values, "VersionFabric", FabricLoader);
            UpsertIni(values, "Info", "测试实例 " + TargetVersion + " Fabric " + FabricLoader + " (" + variant + ")");
            UpsertIni(values, "VersionArgumentIndieV2", "True");
            UpsertIni(values, "VersionArgumentIndie", "1");
            File.WriteAllText(setupPath, string.Join("\r\n", values) + "\r\n", new UTF8Encoding(false));

            // HMCL：已存在则只读不改（玩家的内存/Java 设置不能被覆盖）
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
