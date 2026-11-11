using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installers;
using CmlLib.Core.Java;
using CmlLib.Core.Natives;
using CmlLib.Core.Rules;
using CmlLib.Core.VersionLoader;
using Microsoft.Win32;
using Spectre.Console;

namespace RstcUpdater
{
    internal static class Program
    {
        private const string DefaultManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        private const string TargetVersion = "26.3";
        private const int ExpectedAssetObjects = 5149;
        private const int ExpectedClientLibraries = 74; // 26.3 按 Windows 客户端规则过滤后的 artifact 数（2026-11-11 实测）

        // 阶段 0 安全护栏：默认拒绝写入玩家真实实例（产品阶段由用户明确选择目标目录后移除）
        private static readonly string[] DefaultForbiddenTargets =
        {
            @"E:\其他文件\红石镇客户端"
        };
        private const long ExpectedClientJarSize = 41483720L;

        private const int ExitOk = 0;
        private const int ExitUsage = 2;
        private const int ExitVersionMissing = 3;
        private const int ExitFieldMismatch = 4;
        private const int ExitProbeFailed = 5;
        private const int ExitDependencyBroken = 6;
        private const int ExitInstallFailed = 7;
        private const int ExitInstallVerifyFailed = 8;
        private const int ExitCancelled = 130;

        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                return await Menu.RunAsync();
            }

            try
            {
                switch (args[0])
                {
                    case "--probe":
                        return await ProbeAsync(ParseProbeOptions(args));
                    case "--install":
                        return await InstallAsync(ParseInstallOptions(args));
                    case "--provision":
                        return await ProvisionAsync(ParseProvisionOptions(args));
                    case "--pack":
                        return Pack(ParsePackOptions(args));
                    case "--rollback":
                        return Rollback(ParseInstanceOptions(args));
                    case "--fetch-pack":
                        return await FetchPackAsync(ParseFetchOptions(args));
                    case "--install-v1":
                        return await InstallV1Async(args);
                    case "--update-v1":
                        return await UpdaterFlow.RunAsync(ParseUpdateOptions(args));
                    case "--migrate-v1":
                        return MigrateV1(args);
                    case "--check-self":
                        return await CheckSelfAsync();
                    case "--status":
                        return Menu.PrintStatus(args.Length > 1 ? args[1] : null);
                    case "--menu-script":
                        if (args.Length < 2)
                        {
                            throw new UsageException("--menu-script 需要脚本文件路径");
                        }

                        return await Menu.RunAsync(args[1]);
                    case "--help":
                    case "-h":
                        PrintUsage(Console.Out);
                        return ExitOk;
                    default:
                        Console.Error.WriteLine("未知模式: " + args[0]);
                        PrintUsage(Console.Error);
                        return ExitUsage;
                }
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("参数错误: " + ex.Message);
                PrintUsage(Console.Error);
                return ExitUsage;
            }
            catch (ProbeFormatException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitProbeFailed;
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]操作已取消[/]");
                return ExitCancelled;
            }
        }

        // ==================== probe ====================

        private static async Task<int> ProbeAsync(ProbeOptions options)
        {
            AnsiConsole.Write(new Rule("[cyan]RSTC-Updater 5.0 · net48 probe[/]").RuleStyle("grey"));

            var loaded = TryLoadCmlLib(out var cmlLibError);
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("项目");
            table.AddColumn("值");
            table.AddRow("CLR", Environment.Version.ToString());
            table.AddRow("框架", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            table.AddRow("系统（注册表）", GetRealOs());
            table.AddRow("系统（CLR 报告值）", Environment.OSVersion.ToString());
            table.AddRow("64 位进程", Environment.Is64BitProcess.ToString());
            table.AddRow("CmlLib.Core", loaded ? "可用" : "加载失败：" + cmlLibError);
            AnsiConsole.Write(table);

            if (!loaded)
            {
                Console.Error.WriteLine("关键依赖 CmlLib.Core 不可用，探针失败。");
                return ExitDependencyBroken;
            }

            string manifest;
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("RSTC-Updater/5.0-probe");
                    manifest = await http.GetStringAsync(options.ManifestUrl).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("拉取版本清单失败");
                Console.Error.WriteLine("  URL: " + options.ManifestUrl);
                Console.Error.WriteLine("  " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine("  内层: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                }
                return ExitProbeFailed;
            }

            using (var doc = ParseOrFail(manifest, "版本清单"))
            {
                JsonElement? target = null;
                foreach (var version in doc.RootElement.GetProperty("versions").EnumerateArray())
                {
                    if (version.GetProperty("id").GetString() == TargetVersion)
                    {
                        target = version;
                        break;
                    }
                }

                if (!target.HasValue)
                {
                    Console.Error.WriteLine("版本清单中找不到 " + TargetVersion);
                    return ExitVersionMissing;
                }

                string versionUrl = target.Value.GetProperty("url").GetString();
                string versionJson;
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("RSTC-Updater/5.0-probe");
                        versionJson = await http.GetStringAsync(versionUrl).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("拉取 " + TargetVersion + " 版本 JSON 失败");
                    Console.Error.WriteLine("  URL: " + versionUrl);
                    Console.Error.WriteLine("  " + ex.GetType().Name + ": " + ex.Message);
                    return ExitProbeFailed;
                }

                using (var versionDoc = ParseOrFail(versionJson, TargetVersion + " 版本 JSON"))
                {
                    var root = versionDoc.RootElement;
                    int java = root.GetProperty("javaVersion").GetProperty("majorVersion").GetInt32();
                    string mainClass = root.GetProperty("mainClass").GetString();
                    string assetIndex = root.GetProperty("assetIndex").GetProperty("id").GetString();

                    bool ok = java == 25 &&
                              assetIndex == "34" &&
                              mainClass == "net.minecraft.client.main.Main";

                    if (!ok)
                    {
                        Console.Error.WriteLine("字段与预期不符：");
                        Console.Error.WriteLine("  javaVersion.majorVersion  期望 25   实际 " + java);
                        Console.Error.WriteLine("  assetIndex.id             期望 34   实际 " + assetIndex);
                        Console.Error.WriteLine("  mainClass                 期望 net.minecraft.client.main.Main  实际 " + mainClass);
                        return ExitFieldMismatch;
                    }

                    AnsiConsole.MarkupLine(
                        "[green]26.3[/] java=" + java + " assetIndex=" + assetIndex + " mainClass=" + mainClass);
                    return ExitOk;
                }
            }
        }

        private static bool TryLoadCmlLib(out string error)
        {
            try
            {
                var probePath = new CmlLib.Core.MinecraftPath(
                    Path.Combine(Path.GetTempPath(), "rstc-probe-" + Guid.NewGuid().ToString("N")));
                if (string.IsNullOrEmpty(probePath.BasePath))
                {
                    error = "MinecraftPath.BasePath 为空";
                    return false;
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ==================== install ====================

        private static async Task<int> InstallAsync(InstallOptions options)
        {
            string gameDir = Path.GetFullPath(options.GameDir);
            try
            {
                EnsureTargetAllowed(gameDir, options.Reuse);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("拒绝安装: " + ex.Message);
                return ExitUsage;
            }

            Directory.CreateDirectory(gameDir);
            return await InstallVanillaAsync(gameDir);
        }

        /// <summary>供 V1 安装流程复用：确保原版 26.3 就绪（也可直接对空目录调用）。</summary>
        internal static async Task<int> InstallVanillaAsync(string gameDir)
        {
            AnsiConsole.Write(new Rule("[cyan]安装 Minecraft " + TargetVersion + "[/]").RuleStyle("grey"));
            AnsiConsole.MarkupLine("目标: [yellow]" + Markup.Escape(gameDir) + "[/]");
            AnsiConsole.MarkupLine("说明: 不下载 Java 运行时（产品语义：只检测），仅安装游戏本体/库/资源。");

            var stopwatch = Stopwatch.StartNew();
            long beforeBytes = GetDirectorySize(gameDir);
            int files = 0;
            int totalFiles = 0;
            long lastBytes = 0;
            long totalBytes = 0;
            var lastReport = Stopwatch.StartNew();
            var sync = new object();

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) })
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                MinecraftLauncher launcher;
                try
                {
                    launcher = CreateLauncher(gameDir, http);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("初始化 CmlLib 失败: " + ex);
                    return ExitDependencyBroken;
                }

                launcher.FileProgressChanged += (s, e) =>
                {
                    lock (sync)
                    {
                        files = e.ProgressedTasks;
                        totalFiles = e.TotalTasks;
                    }
                };

                launcher.ByteProgressChanged += (s, e) =>
                {
                    lock (sync)
                    {
                        lastBytes = e.ProgressedBytes;
                        totalBytes = e.TotalBytes;
                    }

                    if (lastReport.ElapsedMilliseconds < 1000)
                    {
                        return;
                    }

                    lastReport.Restart();
                    lock (sync)
                    {
                        RenderInstallProgress(files, totalFiles, lastBytes, totalBytes, stopwatch.Elapsed);
                    }
                };

                try
                {
                    await launcher.InstallAsync(TargetVersion, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AnsiConsole.MarkupLine("[yellow]安装已取消，保留现场供排查：[/]" + Markup.Escape(gameDir));
                    return ExitCancelled;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("安装失败: " + ex.GetType().Name + ": " + ex.Message);
                    if (ex.InnerException != null)
                    {
                        Console.Error.WriteLine("  内层: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                    }
                    return ExitInstallFailed;
                }
            }

            stopwatch.Stop();
            long afterBytes = GetDirectorySize(gameDir);
            AnsiConsole.MarkupLine("");
            AnsiConsole.MarkupLine("[green]安装完成[/] 耗时 " + stopwatch.Elapsed.ToString("hh\\:mm\\:ss") +
                                   "，本次新增 " + ((afterBytes - beforeBytes) / 1048576.0).ToString("F1") +
                                   " MiB（目录总量 " + (afterBytes / 1048576.0).ToString("F1") + " MiB）");

            return VerifyInstall(gameDir);
        }

        private static long GetDirectorySize(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            long total = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return total;
        }

        private static MinecraftLauncher CreateLauncher(string gameDir, HttpClient http)
        {
            var path = new MinecraftPath(gameDir);
            var parameters = MinecraftLauncherParameters.CreateEmpty();
            parameters.HttpClient = http;
            parameters.MinecraftPath = path;
            parameters.RulesEvaluator = new RulesEvaluator();
            parameters.VersionLoader = new MojangJsonVersionLoaderV2(path, http);
            parameters.JavaPathResolver = new MinecraftJavaPathResolver(path);
            parameters.GameInstaller = ParallelGameInstaller.CreateAsCoreCount(http);
            parameters.NativeLibraryExtractor = new NativeLibraryExtractor(parameters.RulesEvaluator);

            var extractors = DefaultFileExtractors.CreateDefault(
                http, parameters.RulesEvaluator, parameters.JavaPathResolver);
            extractors.Java = null; // 产品语义：不下载 Java 运行时
            parameters.FileExtractors = extractors.ToExtractorCollection();

            return new MinecraftLauncher(parameters);
        }

        private static void RenderInstallProgress(int files, int totalFiles, long bytes, long totalBytes, TimeSpan elapsed)
        {
            double speed = elapsed.TotalSeconds > 0 ? bytes / 1048576.0 / elapsed.TotalSeconds : 0;
            string line = string.Format(
                "\r  [{0:hh\\:mm\\:ss}] 文件 {1}/{2}   {3:F1}/{4:F1} MB   {5:F1} MB/s   ",
                elapsed, files, totalFiles, bytes / 1048576.0, totalBytes / 1048576.0, speed);
            Console.Out.Write(line);
            Console.Out.Flush();
        }

        private static int VerifyInstall(string gameDir)
        {
            var checks = new Table().Border(TableBorder.Rounded);
            checks.AddColumn("检查项");
            checks.AddColumn("结果");
            checks.AddColumn("实际值");

            bool allOk = true;

            string versionJson = Path.Combine(gameDir, "versions", TargetVersion, TargetVersion + ".json");
            bool hasVersionJson = File.Exists(versionJson);
            checks.AddRow("versions/" + TargetVersion + "/" + TargetVersion + ".json", hasVersionJson ? "[green]OK[/]" : "[red]缺失[/]", hasVersionJson ? "存在" : "-");
            allOk &= hasVersionJson;

            string jar = Path.Combine(gameDir, "versions", TargetVersion, TargetVersion + ".jar");
            long jarSize = File.Exists(jar) ? new FileInfo(jar).Length : -1;
            bool jarOk = jarSize == ExpectedClientJarSize;
            checks.AddRow("client jar 大小", jarOk ? "[green]OK[/]" : "[red]不符[/]", jarSize.ToString("N0") + " B");
            allOk &= jarOk;

            string assetIndex = Path.Combine(gameDir, "assets", "indexes", "34.json");
            int assetObjects = -1;
            if (File.Exists(assetIndex))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(assetIndex)))
                    {
                        assetObjects = doc.RootElement.GetProperty("objects").EnumerateObject().Count();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("解析 assets/indexes/34.json 失败: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            bool assetOk = assetObjects == ExpectedAssetObjects;
            checks.AddRow("assets/indexes/34.json", assetOk ? "[green]OK[/]" : "[red]不符[/]", assetObjects + " 个对象");
            allOk &= assetOk;

            string libDir = Path.Combine(gameDir, "libraries");
            int libCount = Directory.Exists(libDir)
                ? Directory.EnumerateFiles(libDir, "*.jar", SearchOption.AllDirectories).Count()
                : 0;
            bool libOk = libCount >= ExpectedClientLibraries;
            checks.AddRow("libraries/*.jar", libOk ? "[green]OK[/]" : "[red]不足[/]", libCount + " 个 jar");
            allOk &= libOk;

            long totalBytes = Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            checks.AddRow("总占用", "信息", (totalBytes / 1048576.0).ToString("F1") + " MB");

            AnsiConsole.Write(checks);

            if (allOk)
            {
                AnsiConsole.MarkupLine("[green]INSTALL_VERIFY=PASS[/]");
                return ExitOk;
            }

            Console.Error.WriteLine("INSTALL_VERIFY=FAIL");
            return ExitInstallVerifyFailed;
        }

        private static async Task<int> ProvisionAsync(Provisioner.ProvisionOptions options)
        {
            try
            {
                EnsureTargetAllowed(Path.GetFullPath(options.GameDir), true);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("拒绝执行: " + ex.Message);
                return ExitUsage;
            }

            return await Provisioner.RunAsync(options).ConfigureAwait(false);
        }

        private static void EnsureTargetAllowed(string fullDir, bool reuse)
        {
            if (!Path.IsPathRooted(fullDir))
            {
                throw new UsageException("安装目录必须是绝对路径");
            }

            var roots = new List<string>(DefaultForbiddenTargets);
            string forbidden = Environment.GetEnvironmentVariable("RSTC_FORBIDDEN_TARGETS");
            if (!string.IsNullOrWhiteSpace(forbidden))
            {
                roots.AddRange(forbidden.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (string raw in roots)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string f = Path.GetFullPath(raw.Trim()).TrimEnd('\\');
                string d = fullDir.TrimEnd('\\');
                if (string.Equals(d, f, StringComparison.OrdinalIgnoreCase) ||
                    d.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UsageException("目标位于受保护目录内: " + fullDir);
                }
            }

            if (!reuse && Directory.Exists(fullDir) && Directory.EnumerateFileSystemEntries(fullDir).Any())
            {
                throw new UsageException("目标目录非空（阶段 0 只允许空目录；复用/续跑请加 --reuse）: " + fullDir);
            }
        }

        // ==================== options ====================

        private static ProbeOptions ParseProbeOptions(string[] args)
        {
            var options = new ProbeOptions();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--url":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--url 缺少参数值（测试用，只接受 https）");
                        }

                        string raw = args[++i];
                        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                        {
                            throw new UsageException("--url 只接受 https 绝对地址");
                        }

                        options.ManifestUrl = uri.ToString();
                        break;
                    default:
                        throw new UsageException("--probe 不认识的参数: " + args[i]);
                }
            }

            return options;
        }

        private static int Rollback(InstanceRef options)
        {
            try
            {
                EnsureTargetAllowed(Path.GetFullPath(options.GameDir), true);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("拒绝执行: " + ex.Message);
                return ExitUsage;
            }

            return PackApplier.Rollback(options.GameDir, options.Instance);
        }

        private static async Task<int> FetchPackAsync(PackFetcher.FetchOptions options)
        {
            try
            {
                EnsureTargetAllowed(Path.GetFullPath(options.GameDir), true);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("拒绝执行: " + ex.Message);
                return ExitUsage;
            }

            return await PackFetcher.RunAsync(options).ConfigureAwait(false);
        }

        private static InstanceRef ParseInstanceOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("需要游戏目录");
            }

            var options = new InstanceRef { GameDir = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--instance" && i + 1 < args.Length)
                {
                    options.Instance = args[++i];
                }
                else
                {
                    throw new UsageException("不认识的参数: " + args[i]);
                }
            }

            if (string.IsNullOrEmpty(options.Instance))
            {
                throw new UsageException("需要 --instance <实例名>");
            }

            return options;
        }

        private static PackFetcher.FetchOptions ParseFetchOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("--fetch-pack 需要游戏目录");
            }

            var options = new PackFetcher.FetchOptions { GameDir = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--release-json":
                        options.ReleaseJson = NextArgument(args, ref i, "--release-json");
                        break;
                    case "--asset-name":
                        options.AssetName = NextArgument(args, ref i, "--asset-name");
                        break;
                    case "--instance":
                        options.Instance = NextArgument(args, ref i, "--instance");
                        break;
                    case "--tag":
                        options.Tag = NextArgument(args, ref i, "--tag");
                        break;
                    case "--update":
                        options.Update = true;
                        break;
                    case "--keep":
                        options.Keep = NextArgument(args, ref i, "--keep")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        break;
                    case "--options":
                        options.OptionsMode = ParseKeepOrPack(NextArgument(args, ref i, "--options"), "--options");
                        break;
                    case "--servers":
                        options.ServersMode = ParseKeepOrPack(NextArgument(args, ref i, "--servers"), "--servers");
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--keep-zip":
                        options.KeepZip = true;
                        break;
                    default:
                        throw new UsageException("--fetch-pack 不认识的参数: " + args[i]);
                }
            }

            if (string.IsNullOrEmpty(options.ReleaseJson))
            {
                throw new UsageException("--fetch-pack 需要 --release-json <文件>");
            }

            if (string.IsNullOrEmpty(options.Instance))
            {
                throw new UsageException("--fetch-pack 需要 --instance <实例名>");
            }

            return options;
        }

        private static string NextArgument(string[] args, ref int i, string option)
        {
            if (i + 1 >= args.Length)
            {
                throw new UsageException(option + " 缺少参数");
            }

            return args[++i];
        }

        private sealed class InstanceRef
        {
            public string GameDir;
            public string Instance;
        }

        private static int MigrateV1(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("--migrate-v1 需要游戏目录");
            }

            string gameDir = args[1];
            string from = null;
            string to = null;
            bool overwrite = false;
            List<string> keys = null;
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--from":
                        from = NextArgument(args, ref i, "--from");
                        break;
                    case "--to":
                        to = NextArgument(args, ref i, "--to");
                        break;
                    case "--categories":
                        keys = NextArgument(args, ref i, "--categories")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        break;
                    case "--overwrite":
                        overwrite = true;
                        break;
                    default:
                        throw new UsageException("--migrate-v1 不认识的参数: " + args[i]);
                }
            }

            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                throw new UsageException("--migrate-v1 需要 --from 与 --to");
            }

            var categories = keys == null || keys.Count == 0
                ? Migration.Categories
                : Migration.Categories.Where(c => keys.Contains(c.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            if (categories.Count == 0)
            {
                throw new UsageException("没有匹配的迁移类别");
            }

            try
            {
                if (!Migration.Run(gameDir, from, to, categories, overwrite, out string summary))
                {
                    Console.Error.WriteLine(summary);
                    return 2;
                }

                Console.WriteLine(summary);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("迁移失败: " + ex.GetType().Name + ": " + ex.Message);
                return 7;
            }

            AnsiConsole.MarkupLine("[green]MIGRATE=PASS[/]");
            return 0;
        }

        private static async Task<int> CheckSelfAsync()
        {
            SelfUpdateCheck check = await SelfUpdater.CheckAsync().ConfigureAwait(false);
            var table = new Table().Border(TableBorder.Rounded).Title("[cyan]更新器自更新检查[/]");
            table.AddColumn("项");
            table.AddColumn("值");
            table.AddRow("当前版本", check.Current.ToString());
            table.AddRow("远端版本", check.LatestTag ?? "未知");
            table.AddRow("更新资产", check.AssetName ?? "无");
            table.AddRow("结论", Markup.Escape(check.Message ?? ""));
            AnsiConsole.Write(table);
            if (check.Message != null && check.Message.Contains("无法获取"))
            {
                return 5;
            }

            return 0;
        }

        private static UpdateOptions ParseUpdateOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("--update-v1 需要游戏目录");
            }

            var options = new UpdateOptions { GameDir = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--instance":
                        options.Instance = NextArgument(args, ref i, "--instance");
                        break;
                    case "--zip":
                        options.LocalZipPath = NextArgument(args, ref i, "--zip");
                        break;
                    case "--keep":
                        options.Keep = NextArgument(args, ref i, "--keep")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        break;
                    case "--options":
                        options.OptionsMode = ParseKeepOrPack(NextArgument(args, ref i, "--options"), "--options");
                        break;
                    case "--servers":
                        options.ServersMode = ParseKeepOrPack(NextArgument(args, ref i, "--servers"), "--servers");
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--force":
                        options.Force = true;
                        break;
                    default:
                        throw new UsageException("--update-v1 不认识的参数: " + args[i]);
                }
            }

            if (string.IsNullOrEmpty(options.Instance))
            {
                throw new UsageException("--update-v1 需要 --instance <实例名>");
            }

            return options;
        }

        private static async Task<int> InstallV1Async(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("--install-v1 需要游戏目录");
            }

            string gameDir = args[1];
            string instance = "红石镇客户端";
            string localZip = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--instance")
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new UsageException("--instance 缺少参数");
                    }

                    instance = args[++i];
                }
                else if (args[i] == "--zip")
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new UsageException("--zip 缺少参数");
                    }

                    localZip = args[++i];
                }
                else
                {
                    throw new UsageException("--install-v1 不认识的参数: " + args[i]);
                }
            }

            try
            {
                return await Installer.InstallFlowAsync(gameDir, instance, localZip).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("安装失败: " + ex.GetType().Name + ": " + ex.Message);
                return 7;
            }
        }

        private static int Pack(PackApplier.PackOptions options)
        {
            try
            {
                EnsureTargetAllowed(Path.GetFullPath(options.GameDir), true);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("拒绝执行: " + ex.Message);
                return ExitUsage;
            }

            return PackApplier.Run(options);
        }

        private static PackApplier.PackOptions ParsePackOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("--pack 需要游戏目录");
            }

            var options = new PackApplier.PackOptions { GameDir = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--zip":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--zip 缺少参数");
                        }

                        options.ZipPath = args[++i];
                        break;
                    case "--instance":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--instance 缺少参数");
                        }

                        options.Instance = args[++i];
                        break;
                    case "--update":
                        options.Update = true;
                        break;
                    case "--keep":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--keep 缺少参数");
                        }

                        options.Keep = args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        break;
                    case "--options":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--options 缺少参数");
                        }

                        options.OptionsMode = ParseKeepOrPack(args[++i], "--options");
                        break;
                    case "--servers":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--servers 缺少参数");
                        }

                        options.ServersMode = ParseKeepOrPack(args[++i], "--servers");
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--tag":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--tag 缺少参数");
                        }

                        options.Tag = args[++i];
                        break;
                    default:
                        throw new UsageException("--pack 不认识的参数: " + args[i]);
                }
            }

            if (string.IsNullOrEmpty(options.ZipPath))
            {
                throw new UsageException("--pack 需要 --zip <整合包>");
            }

            if (string.IsNullOrEmpty(options.Instance))
            {
                throw new UsageException("--pack 需要 --instance <实例名>");
            }

            return options;
        }

        private static string ParseKeepOrPack(string value, string option)
        {
            if (string.Equals(value, "keep", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "pack", StringComparison.OrdinalIgnoreCase))
            {
                return value.ToLowerInvariant();
            }

            throw new UsageException(option + " 只接受 keep 或 pack");
        }

        private static Provisioner.ProvisionOptions ParseProvisionOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("--provision 需要游戏目录");
            }

            var options = new Provisioner.ProvisionOptions { GameDir = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--reuse":
                        options.Reuse = true;
                        break;
                    case "--mod-src":
                        if (i + 1 >= args.Length)
                        {
                            throw new UsageException("--mod-src 缺少目录");
                        }

                        options.ModSource = args[++i];
                        options.SampleMods = true;
                        break;
                    default:
                        throw new UsageException("--provision 不认识的参数: " + args[i]);
                }
            }

            return options;
        }

        private static InstallOptions ParseInstallOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new UsageException("--install 需要目标目录");
            }

            var options = new InstallOptions { GameDir = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--reuse")
                {
                    options.Reuse = true;
                    continue;
                }

                throw new UsageException("--install 不认识的参数: " + args[i]);
            }

            return options;
        }

        private static JsonDocument ParseOrFail(string json, string what)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ProbeFormatException(what + " 解析失败: " + ex.Message);
            }
        }

        private sealed class ProbeFormatException : Exception
        {
            public ProbeFormatException(string message) : base(message)
            {
            }
        }

        private static void PrintUsage(TextWriter writer)
        {
            writer.WriteLine("用法:");
            writer.WriteLine("  RstcUpdater.exe --probe [--url <https 清单地址，测试用>]");
            writer.WriteLine("  RstcUpdater.exe --install <空的绝对目录> [--reuse]   # 阶段 0 验证：安装原版 26.3，不下载 Java；--reuse 允许续跑/复用已有目录");
            writer.WriteLine("  RstcUpdater.exe --provision <游戏目录> [--reuse] [--mod-src <mods 目录>]   # 阶段 0-2：Fabric + A2/A1/B 三份实例 + 启动器配置");
            writer.WriteLine("  RstcUpdater.exe --pack <游戏目录> --zip <整合包> --instance <实例名> [--update] [--keep a,b] [--options keep|pack] [--servers keep|pack] [--tag <Tag>] [--dry-run]");
            writer.WriteLine("  RstcUpdater.exe --fetch-pack <游戏目录> --release-json <文件> --instance <实例名> [--update] [--keep a,b] [--options keep|pack] [--servers keep|pack] [--asset-name <名>] [--keep-zip]");
            writer.WriteLine("  RstcUpdater.exe --rollback <游戏目录> --instance <实例名>");
            writer.WriteLine("  RstcUpdater.exe --install-v1 <游戏目录> [--instance <实例名>] [--zip <本地包>]   # V1 安装流程");
            writer.WriteLine("  RstcUpdater.exe --update-v1 <游戏目录> --instance <实例名> [--zip <本地包>] [--keep a,b] [--options keep|pack] [--servers keep|pack] [--dry-run] [--force]");
            writer.WriteLine("  RstcUpdater.exe --migrate-v1 <游戏目录> --from <源实例> --to <目标实例> [--categories saves,screenshots,maps,dh,settings] [--overwrite]");
            writer.WriteLine("  RstcUpdater.exe --check-self   # 检查更新器自身更新");
            writer.WriteLine("  RstcUpdater.exe --status [游戏目录]   # 非交互输出状态面板 + 关键文件检查");
            writer.WriteLine("  RstcUpdater.exe --menu-script <脚本文件>   # 用脚本输入驱动菜单（测试用）");
            writer.WriteLine("  RstcUpdater.exe （无参数）   # 交互式菜单");
            writer.WriteLine("  RstcUpdater.exe （无参数）   # 交互式菜单");
            writer.WriteLine("  RstcUpdater.exe --help");
            writer.WriteLine("");
            writer.WriteLine("退出码: 0 成功 / 2 参数 / 3 版本缺失 / 4 字段不符 / 5 网络 / 6 依赖 / 7 安装失败 / 8 校验失败 / 130 取消");
        }

        private static string GetRealOs()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null)
                    {
                        return "unknown";
                    }

                    object name = key.GetValue("ProductName");
                    object build = key.GetValue("CurrentBuild");
                    object ubr = key.GetValue("UBR");
                    return (name ?? "unknown") + " (build " + (build ?? "?") + "." + (ubr ?? "?") + ")";
                }
            }
            catch
            {
                return "unknown";
            }
        }

        private sealed class ProbeOptions
        {
            public string ManifestUrl = DefaultManifestUrl;
        }

        private sealed class InstallOptions
        {
            public string GameDir;
            public bool Reuse;
        }

        private sealed class UsageException : Exception
        {
            public UsageException(string message) : base(message)
            {
            }
        }
    }
}
