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
    /// <summary>V1 交互主界面：状态面板 + 菜单。</summary>
    internal static class Menu
    {
        private const int RequiredJava = 25;

        public static async Task<int> RunAsync()
        {
            await Task.Yield();
            Console.OutputEncoding = Encoding.UTF8;
            var config = AppConfig.Load();
            string gameDir = ResolveGameDir(config);

            while (true)
            {
                RenderStatus(gameDir, config);
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey]请选择操作[/]")
                        .PageSize(10)
                        .AddChoices(
                            "安装到新目录",
                            "更新现有实例",
                            "从旧实例迁移",
                            "检查关键文件",
                            "检查更新器更新",
                            "设置",
                            "退出"));

                try
                {
                    switch (choice)
                    {
                        case "安装到新目录":
                        case "更新现有实例":
                        case "从旧实例迁移":
                        case "检查更新器更新":
                            AnsiConsole.MarkupLine("[yellow]该功能正在接入（V1 后续提交实现）[/]");
                            break;
                        case "检查关键文件":
                            CheckKeyFiles(gameDir);
                            break;
                        case "设置":
                            gameDir = Settings(config, gameDir);
                            break;
                        case "退出":
                            config.LastGameDir = gameDir;
                            config.Save();
                            return 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("操作失败: " + ex.GetType().Name + ": " + ex.Message);
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]按任意键返回菜单…[/]");
                try
                {
                    Console.ReadKey(true);
                }
                catch (InvalidOperationException)
                {
                    // 非交互环境（重定向）直接继续
                }
            }
        }

        /// <summary>非交互状态输出（供测试/证据使用）。</summary>
        public static int PrintStatus(string gameDir)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var config = AppConfig.Load();
            string resolved = gameDir;
            if (string.IsNullOrEmpty(resolved))
            {
                if (!string.IsNullOrEmpty(config.LastGameDir) && GameDirDetector.LooksLikeMinecraftDir(config.LastGameDir))
                {
                    resolved = config.LastGameDir;
                }
                else
                {
                    resolved = GameDirDetector.FindCandidates(config.LastGameDir).FirstOrDefault();
                }
            }

            RenderStatus(resolved, config);
            CheckKeyFiles(resolved);
            return 0;
        }

        private static string ResolveGameDir(AppConfig config)
        {
            if (!string.IsNullOrEmpty(config.LastGameDir) && GameDirDetector.LooksLikeMinecraftDir(config.LastGameDir))
            {
                return config.LastGameDir;
            }

            var candidates = GameDirDetector.FindCandidates(config.LastGameDir);
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            if (candidates.Count > 1)
            {
                var picked = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey]检测到多个游戏目录，请选择[/]")
                        .AddChoices(candidates));
                return picked;
            }

            string input = AnsiConsole.Prompt(
                new TextPrompt<string>("请输入 .minecraft 游戏目录路径（可留空跳过）:")
                    .AllowEmpty());
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            string full = Path.GetFullPath(input.Trim().Trim('"'));
            if (!GameDirDetector.LooksLikeMinecraftDir(full))
            {
                AnsiConsole.MarkupLine("[yellow]该目录缺少 versions/ 等特征，仍按手动选择使用。[/]");
            }

            return full;
        }

        private static void RenderStatus(string gameDir, AppConfig config)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[cyan]RSTC-Updater 5.0 · 状态[/]");
            table.AddColumn("项目");
            table.AddColumn("值");

            table.AddRow("游戏目录", string.IsNullOrEmpty(gameDir) ? "[yellow]未定位[/]" : Markup.Escape(gameDir));

            string instance = FindCurrentInstance(gameDir, config);
            table.AddRow("当前实例", string.IsNullOrEmpty(instance) ? "[yellow]未安装[/]" : Markup.Escape(instance));

            string packVersion = "未安装";
            string mcVersion = "未知";
            if (!string.IsNullOrEmpty(gameDir) && !string.IsNullOrEmpty(instance))
            {
                string index = Path.Combine(gameDir, "versions", instance, "modrinth.index.json");
                string tagFile = Path.Combine(gameDir, "versions", instance, "RSTC_version.txt");
                if (File.Exists(index))
                {
                    try
                    {
                        var root = JsonNode.Parse(File.ReadAllText(index, Encoding.UTF8)) as JsonObject;
                        packVersion = root?["versionId"]?.GetValue<string>() ?? packVersion;
                        mcVersion = root?["dependencies"]?["minecraft"]?.GetValue<string>() ?? mcVersion;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (packVersion == "未安装" && File.Exists(tagFile))
                {
                    packVersion = File.ReadAllText(tagFile, Encoding.UTF8).Trim();
                }
            }

            table.AddRow("本地整合包版本", packVersion);
            table.AddRow("本地 MC 版本", mcVersion);

            string latest = "无法检查更新";
            try
            {
                var release = ReleaseClient.GetLatestAsync(ReleaseClient.PackRepo).GetAwaiter().GetResult();
                if (release != null)
                {
                    var asset = ReleaseClient.FindPackAsset(release);
                    string ver = ReleaseClient.VersionFromAssetName(asset?.Name);
                    latest = (ver ?? "未知") + "（Minecraft 26.3）";
                    if (release.FromCache)
                    {
                        latest += "（来自缓存 " + (release.FetchedAt?.ToString("yyyy-MM-dd") ?? "-") + "）";
                    }
                }
            }
            catch (Exception)
            {
            }

            table.AddRow("最新版本", latest);

            var javas = JavaDetector.Detect(gameDir);
            string javaText;
            if (javas.Count == 0)
            {
                javaText = "[yellow]未检测到任何 Java[/]";
            }
            else if (javas.Any(j => j.MajorVersion >= RequiredJava))
            {
                javaText = "[green]Java " + RequiredJava + " 已就绪[/]（" + javas.First(j => j.MajorVersion >= RequiredJava).Path + "）";
            }
            else
            {
                javaText = "[yellow]需要 " + RequiredJava + "，当前最高 " + javas[0].MajorVersion + "[/]";
            }

            table.AddRow("Java", javaText);

            string isolation = "未检测到启动器配置";
            if (!string.IsNullOrEmpty(gameDir) && !string.IsNullOrEmpty(instance))
            {
                string instDir = Path.Combine(gameDir, "versions", instance);
                bool pcl = File.Exists(Path.Combine(instDir, "PCL", "Setup.ini"));
                bool hmcl = File.Exists(Path.Combine(instDir, "hmclversion.cfg"));
                if (pcl && hmcl)
                {
                    isolation = "PCL2 已启用 / HMCL 已启用";
                }
                else if (pcl)
                {
                    isolation = "PCL2 已启用";
                }
                else if (hmcl)
                {
                    isolation = "HMCL 已启用";
                }
            }

            table.AddRow("启动器隔离", isolation);
            AnsiConsole.Write(table);
        }

        private static void CheckKeyFiles(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                AnsiConsole.MarkupLine("[yellow]未定位游戏目录[/]");
                return;
            }

            var config = AppConfig.Load();
            string instance = FindCurrentInstance(gameDir, config);
            if (string.IsNullOrEmpty(instance))
            {
                AnsiConsole.MarkupLine("[yellow]未找到实例（versions/ 下无 modrinth.index.json / RSTC_version.txt）[/]");
                return;
            }

            string instDir = Path.Combine(gameDir, "versions", instance);
            var table = new Table().Border(TableBorder.Rounded).Title("[cyan]关键文件检查: " + Markup.Escape(instance) + "[/]");
            table.AddColumn("项");
            table.AddColumn("状态");

            void Check(string name, bool exists, string detail = null)
            {
                table.AddRow(Markup.Escape(name), exists ? "[green]OK[/]" : "[red]缺失[/]" + (detail == null ? "" : " " + Markup.Escape(detail)));
            }

            Check("实例目录", Directory.Exists(instDir));
            Check("modrinth.index.json", File.Exists(Path.Combine(instDir, "modrinth.index.json")));
            Check("RSTC_version.txt", File.Exists(Path.Combine(instDir, "RSTC_version.txt")));
            Check("mods/", Directory.Exists(Path.Combine(instDir, "mods")));
            Check("config/", Directory.Exists(Path.Combine(instDir, "config")));
            Check("options.txt", File.Exists(Path.Combine(instDir, "options.txt")));
            Check("versions/26.3/26.3.json", File.Exists(Path.Combine(gameDir, "versions", "26.3", "26.3.json")));
            Check("versions/26.3/26.3.jar", File.Exists(Path.Combine(gameDir, "versions", "26.3", "26.3.jar")));
            Check("assets/indexes/34.json", File.Exists(Path.Combine(gameDir, "assets", "indexes", "34.json")));
            Check("libraries/", Directory.Exists(Path.Combine(gameDir, "libraries")));

            int mods = Directory.Exists(Path.Combine(instDir, "mods"))
                ? Directory.EnumerateFiles(Path.Combine(instDir, "mods"), "*.jar").Count()
                : 0;
            table.AddRow("mods/*.jar", mods.ToString());
            AnsiConsole.Write(table);
        }

        private static string Settings(AppConfig config, string currentGameDir)
        {
            string input = AnsiConsole.Prompt(
                new TextPrompt<string>("游戏目录（留空保持不变）:")
                    .DefaultValue(currentGameDir ?? "")
                    .AllowEmpty());
            if (string.IsNullOrWhiteSpace(input))
            {
                return currentGameDir;
            }

            string full = Path.GetFullPath(input.Trim().Trim('"'));
            config.LastGameDir = full;
            config.Save();
            AnsiConsole.MarkupLine("[green]已保存[/] " + Markup.Escape(full));
            return full;
        }

        private static string FindCurrentInstance(string gameDir, AppConfig config)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                return null;
            }

            string versionsDir = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versionsDir))
            {
                return null;
            }

            var instances = new List<string>();
            foreach (string dir in Directory.GetDirectories(versionsDir))
            {
                string name = Path.GetFileName(dir);
                if (File.Exists(Path.Combine(dir, "modrinth.index.json")))
                {
                    instances.Add(name);
                }
            }

            if (instances.Count == 0)
            {
                foreach (string dir in Directory.GetDirectories(versionsDir))
                {
                    string name = Path.GetFileName(dir);
                    if (File.Exists(Path.Combine(dir, "RSTC_version.txt")))
                    {
                        instances.Add(name);
                    }
                }
            }

            if (instances.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(config.LastInstance) && instances.Contains(config.LastInstance))
            {
                return config.LastInstance;
            }

            return instances[0];
        }
    }
}
