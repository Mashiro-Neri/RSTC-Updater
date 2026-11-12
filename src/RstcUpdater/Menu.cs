using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>V1 交互主界面：状态面板 + 菜单。</summary>
    internal static class Menu
    {
        private const int RequiredJava = 25;

        private static ReleaseInfo _releaseCache;
        private static DateTime _releaseCacheAt = DateTime.MinValue;

        private static ReleaseInfo GetLatestCached(bool spinner)
        {
            if (_releaseCache != null && (DateTime.Now - _releaseCacheAt).TotalSeconds < 60)
            {
                return _releaseCache;
            }

            ReleaseInfo result = null;
            if (spinner)
            {
                AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("连接 GitHub …", ctx =>
                {
                    result = ReleaseClient.GetLatestAsync(ReleaseClient.PackRepo).GetAwaiter().GetResult();
                });
            }
            else
            {
                result = ReleaseClient.GetLatestAsync(ReleaseClient.PackRepo).GetAwaiter().GetResult();
            }

            _releaseCache = result;
            _releaseCacheAt = DateTime.Now;
            return result;
        }

        private static Queue<string> _script;

        private static bool ScriptMode
        {
            get { return _script != null; }
        }

        public static async Task<int> RunAsync(string scriptPath = null)
        {
            await Task.Yield();
            if (!string.IsNullOrEmpty(scriptPath))
            {
                // 仅 --menu-script 显式入口启用脚本模式（不影响正常双击）
                LoadScript(scriptPath);
            }

            Console.OutputEncoding = Encoding.UTF8;
            var config = AppConfig.Load();
            string gameDir = ResolveGameDir(config);
            HandleUnfinishedJournals(gameDir);

            bool interactive = !ScriptMode && !Console.IsOutputRedirected;
            bool firstRender = true;
            if (!interactive)
            {
                Ui.LogoBox();
            }

            while (true)
            {
                if (interactive)
                {
                    AnsiConsole.Clear();
                    Ui.LogoBox(firstRender);
                    firstRender = false;
                }

                RenderStatus(gameDir, config, interactive);
                AnsiConsole.WriteLine();
                Ui.Section("操作");
                var menuChoices = new[]
                {
                    "安装到新目录",
                    "更新现有实例",
                    "从旧实例迁移",
                    "检查关键文件",
                    "回滚未完成更新",
                    "检查更新器更新",
                    "设置",
                    "退出"
                };
                string choice;
                if (ScriptMode)
                {
                    choice = SelectPrompt("[grey]请选择操作[/]", menuChoices);
                }
                else
                {
                    var descriptions = new[]
                    {
                        "新装 26.3 + 整合包",
                        "下载最新包并应用",
                        "复制旧实例玩家数据",
                        "检查实例完整性",
                        "回滚中断的更新",
                        "检查更新器自身更新",
                        "修改游戏目录 / 实例",
                        "退出程序"
                    };
                    choice = menuChoices[Ui.ArrowMenu(menuChoices, descriptions)];
                }
                try
                {
                    switch (choice)
                    {
                        case "安装到新目录":
                            gameDir = await InstallActionAsync(gameDir);
                            break;
                        case "更新现有实例":
                            await UpdateActionAsync(gameDir);
                            break;
                        case "从旧实例迁移":
                            MigrateAction(gameDir);
                            break;
                        case "检查更新器更新":
                            await SelfUpdateActionAsync();
                            break;
                        case "检查关键文件":
                            CheckKeyFiles(gameDir);
                            break;
                        case "回滚未完成更新":
                            RollbackAction(gameDir);
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
                catch (FlowFailureException)
                {
                    // 脚本输入错误/耗尽：中止菜单并让调用方以对应退出码结束
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("操作失败: " + ex.GetType().Name + ": " + ex.Message);
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]按任意键返回菜单…[/]");
                if (!ScriptMode)
                {
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

            Ui.LogoBox();
            RenderStatus(resolved, config);
            CheckKeyFiles(resolved);
            return 0;        }

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
                return SelectPrompt("[grey]检测到多个游戏目录，请选择[/]", candidates);
            }

            string input = TextPromptInput("请输入 .minecraft 游戏目录路径（可留空跳过）:", "", true);
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

        private static void RenderStatus(string gameDir, AppConfig config, bool compact = false)
        {
            Ui.Section("状态");

            string instance = FindCurrentInstance(gameDir, config);
            var allInstances = FindInstances(gameDir);
            string instanceText = string.IsNullOrEmpty(instance) ? "[yellow]未安装[/]" : Markup.Escape(instance);
            if (allInstances.Count > 1)
            {
                instanceText += "[grey]（共 " + allInstances.Count + " 个实例）[/]";
            }

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

            string latest = "无法检查更新";
            try
            {
                var release = GetLatestCached(compact && !ScriptMode && !Console.IsOutputRedirected);
                if (release != null)
                {
                    var asset = ReleaseClient.FindPackAsset(release);
                    string ver = ReleaseClient.VersionFromAssetName(asset?.Name);
                    latest = (ver ?? "未知") + (release.IsPrerelease ? "[yellow]（Pre-release）[/]" : "");
                    if (release.FromCache)
                    {
                        latest += "[grey]（缓存 " + Markup.Escape(release.FetchedAt?.ToString("yyyy-MM-dd") ?? "-") + "）[/]";
                    }
                }
            }
            catch (Exception)
            {
            }

            var javas = JavaDetector.Detect(gameDir);
            string javaText;
            if (javas.Count == 0)
            {
                javaText = "[yellow]未检测到任何 Java[/]";
            }
            else if (javas.Any(j => j.MajorVersion >= RequiredJava))
            {
                javaText = "[green]Java " + RequiredJava + " 就绪[/]";
            }
            else
            {
                javaText = "[yellow]需要 " + RequiredJava + "，当前最高 " + javas[0].MajorVersion + "[/]";
            }

            string isolation = "未检测到启动器配置";
            if (!string.IsNullOrEmpty(gameDir) && !string.IsNullOrEmpty(instance))
            {
                string instDir = Path.Combine(gameDir, "versions", instance);
                string pclIni = Path.Combine(instDir, "PCL", "Setup.ini");
                string hmclCfg = Path.Combine(instDir, "hmclversion.cfg");
                bool pclIsolated = false;
                if (File.Exists(pclIni))
                {
                    try
                    {
                        string iniText = File.ReadAllText(pclIni, Encoding.UTF8);
                        pclIsolated = Regex.IsMatch(iniText, @"(?im)^\s*VersionArgumentIndieV2\s*:\s*True\s*$") ||
                                      (!Regex.IsMatch(iniText, @"(?im)^\s*VersionArgumentIndieV2\s*:") &&
                                       Regex.IsMatch(iniText, @"(?im)^\s*VersionArgumentIndie\s*:\s*1\s*$"));
                    }
                    catch (Exception)
                    {
                    }
                }

                bool hmclIsolated = false;
                if (File.Exists(hmclCfg))
                {
                    try
                    {
                        var cfg = JsonNode.Parse(File.ReadAllText(hmclCfg, Encoding.UTF8)) as JsonObject;
                        hmclIsolated = cfg?["gameDirType"]?.GetValue<int>() == 1;
                    }
                    catch (Exception)
                    {
                    }
                }

                bool pclInstalled = File.Exists(pclIni);
                bool hmclInstalled = File.Exists(hmclCfg);
                if (pclInstalled || hmclInstalled)
                {
                    isolation = (pclInstalled ? (pclIsolated ? "[green]PCL2 已启用隔离[/]" : "[yellow]PCL2 配置存在但未启用隔离[/]") : "") +
                                (pclInstalled && hmclInstalled ? " / " : "") +
                                (hmclInstalled ? (hmclIsolated ? "[green]HMCL 已启用隔离[/]" : "[yellow]HMCL 配置存在但未启用隔离[/]") : "");
                }
            }

            string instanceChip = string.IsNullOrEmpty(instance) ? "[yellow]●[/] 未安装" : "[green]●[/] " + instanceText;
            string packChip = packVersion == "未安装"
                ? "[yellow]未安装[/]"
                : "[white]" + Markup.Escape(packVersion) + "[/][grey] / MC " + Markup.Escape(mcVersion) + "[/]";
            string remoteChip = (latest.StartsWith("无法", StringComparison.Ordinal) || latest.Contains("Pre-release") ? "[yellow]●[/]" : "[green]●[/]") +
                                " " + (ReleaseClient.BetaChannel ? "beta" : "stable") + " " + latest;
            string javaChip = (javaText.Contains("就绪") ? "[green]●[/]" : "[yellow]●[/]") + " " + javaText;
            string isoChip = (isolation.Contains("已启用隔离") && !isolation.Contains("未启用隔离") ? "[green]●[/]" : "[yellow]●[/]") + " " + isolation;

            string isoShort = isolation.Contains("已启用隔离")
                ? (isolation.Contains("PCL2") && isolation.Contains("HMCL") ? "PCL2 + HMCL" : (isolation.Contains("PCL2") ? "PCL2" : "HMCL"))
                : "未检测到";
            string javaShort = javaText.Contains("就绪") ? ("Java " + RequiredJava + " ✓") : ("Java 需要 " + RequiredJava);

            if (compact)
            {
                string pathText = string.IsNullOrEmpty(gameDir)
                    ? "[yellow]未定位[/]"
                    : Markup.Escape(Ui.Fit(gameDir, Math.Max(18, Ui.ContentWidth() - 10)));
                Ui.StatusLine("PATH", pathText);
                string sep = "  [" + Ui.Dim + "]│[/]  ";
                Ui.StatusLine("ENV", instanceChip + sep + "[" + Ui.Dim + "]整合包[/] " + packChip);
                Ui.StatusLine("NET", remoteChip + sep + "[" + Ui.Dim + "]Java[/] " + javaShort + sep + "[" + Ui.Dim + "]隔离[/] " + isoShort);
                return;
            }

            Ui.Rows(new[]
            {
                ("游戏目录", string.IsNullOrEmpty(gameDir) ? "[yellow]未定位[/]" : Markup.Escape(Ui.Fit(gameDir, Math.Max(30, Ui.SafeWidth() - 20)))),
                ("当前实例", instanceChip),
                ("整合包", packChip),
                ("远端", remoteChip),
                ("Java", javaChip),
                ("启动器隔离", isoChip)
            });
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
            var all = FindInstances(gameDir);
            string title = "关键文件检查: " + instance;
            if (all.Count > 1)
            {
                title += "（共 " + all.Count + " 个实例）";
            }

            AnsiConsole.WriteLine();
            Ui.Section(title);
            var rows = new List<(string Label, string ValueMarkup)>();
            void Check(string name, bool exists)
            {
                rows.Add((name, exists ? "[green]OK[/]" : "[red]缺失[/]"));
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
            rows.Add(("mods/*.jar", mods.ToString()));
            Ui.Rows(rows, 24);
        }

        // ==================== 可脚本化输入（测试钩子） ====================

        private static void LoadScript(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FlowFailureException(2, "菜单脚本不存在: " + path);
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                .Select(l => l.Trim())
                .ToList();
            if (lines.Count == 0)
            {
                throw new FlowFailureException(2, "菜单脚本为空: " + path);
            }

            _script = new Queue<string>(lines);
        }

        private static string NextScript(string what)
        {
            if (_script.Count == 0)
            {
                throw new FlowFailureException(2, "菜单脚本输入用尽，仍需要: " + what);
            }

            return _script.Dequeue();
        }

        private static string SelectPrompt(string title, IReadOnlyList<string> choices)
        {
            if (!ScriptMode)
            {
                return AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title(title)
                    .PageSize(9)
                    .HighlightStyle(new Style(foreground: Color.White, background: Color.Maroon))
                    .AddChoices(choices));
            }
            string answer = NextScript(title);
            string match = choices.FirstOrDefault(c => string.Equals(c, answer, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                var contains = choices.Where(c => c.IndexOf(answer, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (contains.Count == 1)
                {
                    match = contains[0];
                }
            }

            if (match == null)
            {
                throw new FlowFailureException(2, "菜单脚本选择不在选项内: " + answer + "（" + title + "）");
            }

            return match;
        }

        private static List<string> MultiPrompt(string title, IReadOnlyList<string> choices)
        {
            if (!ScriptMode)
            {
                return AnsiConsole.Prompt(new MultiSelectionPrompt<string>().Title(title).AddChoices(choices));
            }

            string answer = NextScript(title);
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(answer) || answer == "无")
            {
                return result;
            }

            foreach (string part in answer.Split(','))
            {
                string token = part.Trim();
                string match = choices.FirstOrDefault(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var contains = choices.Where(c => c.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    match = contains.Count == 1 ? contains[0] : null;
                }

                if (match != null)
                {
                    result.Add(match);
                }
            }

            return result;
        }

        private static bool ConfirmPrompt(string message, bool defaultValue)
        {
            if (!ScriptMode)
            {
                return AnsiConsole.Confirm(message, defaultValue);
            }

            string answer = NextScript(message).ToLowerInvariant();
            return answer.StartsWith("y") || answer == "是" || answer == "1";
        }

        private static string TextPromptInput(string message, string defaultValue, bool allowEmpty)
        {
            if (!ScriptMode)
            {
                var prompt = new TextPrompt<string>(message).DefaultValue(defaultValue ?? "");
                if (allowEmpty)
                {
                    prompt = prompt.AllowEmpty();
                }

                return AnsiConsole.Prompt(prompt);
            }

            string answer = NextScript(message);
            return answer == "空" ? string.Empty : answer;
        }

        private static async Task<string> InstallActionAsync(string gameDir)
        {
            var config = AppConfig.Load();
            string dir = gameDir;
            if (string.IsNullOrEmpty(dir))
            {
                string input = TextPromptInput("游戏目录（.minecraft，留空取消）:", "", true);
                if (string.IsNullOrWhiteSpace(input))
                {
                    return gameDir;
                }

                dir = Path.GetFullPath(input.Trim().Trim('"'));
            }

            string name = TextPromptInput("实例名:", "红石镇客户端", false);
            name = name.Trim();
            if (!PackApplier.IsSafeInstanceName(name))
            {
                AnsiConsole.MarkupLine("[red]实例名不合法（不能含路径分隔符、盘符或保留名）[/]");
                return gameDir;
            }

            if (Directory.Exists(Path.Combine(dir, "versions", name)))
            {
                AnsiConsole.MarkupLine("[red]同名实例已存在，请换一个名字，或使用「更新现有实例」[/]");
                return gameDir;
            }

            var javas = JavaDetector.Detect(dir);
            bool javaOk = javas.Any(j => j.MajorVersion >= RequiredJava);
            var info = new Table().Border(TableBorder.Rounded).Title("[cyan]安装确认[/]");
            info.AddColumn("项");
            info.AddColumn("值");
            info.AddRow("游戏目录", Markup.Escape(dir));
            info.AddRow("实例名", Markup.Escape(name));
            info.AddRow("预计下载", "原版约 590 MiB + 整合包约 580 MiB（已有文件会复用）");
            info.AddRow("Java", javaOk
                ? "[green]Java " + RequiredJava + " 已就绪[/]"
                : "[yellow]未检测到 Java " + RequiredJava + "（安装可继续，启动前需自行安装）[/]");
            AnsiConsole.Write(info);

            string localZipInput = TextPromptInput("本地整合包 zip（留空=在线下载最新）:", "", true);
            string localZip = string.IsNullOrWhiteSpace(localZipInput) ? null : localZipInput.Trim().Trim('"');

            if (!ConfirmPrompt("开始安装？", true))
            {
                return gameDir;
            }

            int code = await Installer.InstallFlowAsync(dir, name, localZip).ConfigureAwait(false);
            if (code == 0)
            {
                config.LastGameDir = dir;
                config.LastInstance = name;
                config.Save();
            }

            return dir;
        }

        private static void HandleUnfinishedJournals(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                return;
            }

            string versions = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versions))
            {
                return;
            }

            foreach (string journalPath in Directory.EnumerateFiles(versions, ".rstc-journal.json", SearchOption.AllDirectories))
            {
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(journalPath, Encoding.UTF8)) as JsonObject;
                    string status = root?["status"]?.GetValue<string>() ?? "";
                    if (!string.Equals(status, "writing", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string instance = Path.GetFileName(Path.GetDirectoryName(journalPath));
                    AnsiConsole.MarkupLine("[yellow]检测到实例 " + Markup.Escape(instance) +
                                           " 有未完成更新（journal=" + Markup.Escape(status) + "）[/]");
                    string pick = SelectPrompt("是否回滚到更新前状态？", new[] { "回滚", "暂时忽略" });
                    if (pick == "回滚")
                    {
                        PackApplier.Rollback(gameDir, instance);
                    }
                }
                catch (FlowFailureException)
                {
                    // 脚本输入错误/耗尽必须上抛，不能被吞掉导致后续输入错位
                    throw;
                }
                catch (Exception)
                {
                }
            }
        }

        private static async Task UpdateActionAsync(string gameDir)
        {
            var config = AppConfig.Load();
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                AnsiConsole.MarkupLine("[yellow]未定位游戏目录[/]");
                return;
            }

            string instance = ResolveInstance(gameDir, config, "选择要更新的实例:");
            if (string.IsNullOrEmpty(instance))
            {
                AnsiConsole.MarkupLine("[yellow]未找到已安装实例，请先用「安装到新目录」[/]");
                return;
            }

            config.LastInstance = instance;
            config.Save();

            UpdateCheck check = await UpdaterFlow.CheckAsync(gameDir, instance).ConfigureAwait(false);
            var table = new Table().Border(TableBorder.Rounded).Title("[cyan]更新检查[/]");
            table.AddColumn("项");
            table.AddColumn("值");
            table.AddRow("实例", Markup.Escape(instance));
            table.AddRow("本地版本", Markup.Escape(check.LocalVersion ?? "未知"));
            table.AddRow("本地 MC", Markup.Escape(check.LocalMinecraft ?? "未知"));
            table.AddRow("远端版本", Markup.Escape(check.RemoteVersion ?? "未知") +
                                (check.RemoteTag == null ? "" : "（" + Markup.Escape(check.RemoteTag) + "）"));
            table.AddRow("结论", Markup.Escape(check.Message ?? (check.UpdateAvailable ? "有新版本" : "已是最新")));
            AnsiConsole.Write(table);

            if (check.LocalInstanceMissing || !check.UpdateAvailable)
            {
                return;
            }

            if (!ConfirmPrompt("下载并更新到 " + (check.RemoteVersion ?? "新版本") + "？", true))
            {
                return;
            }

            PreparedUpdate prepared;
            try
            {
                prepared = await UpdaterFlow.PrepareAsync(new UpdateOptions
                {
                    GameDir = gameDir,
                    Instance = instance
                }).ConfigureAwait(false);
            }
            catch (FlowFailureException ex)
            {
                AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                return;
            }

            if (prepared == null || prepared.UpToDate)
            {
                return;
            }

            var keep = new List<string>();
            if (prepared.Plan.Deletes.Count > 0)
            {
                var chosen = MultiPrompt("[yellow]勾选要「保留」的文件（不选则删除）[/]", prepared.Plan.Deletes);
                keep.AddRange(chosen);
            }

            string optionsMode = "keep";
            string serversMode = "keep";
            if (prepared.Plan.Conflicts.Any(c => string.Equals(c, "options.txt", StringComparison.OrdinalIgnoreCase)))
            {
                string pick = SelectPrompt("options.txt 与包内不同，采用哪个？", new[] { "保留我的", "用包里的" });
                optionsMode = pick == "用包里的" ? "pack" : "keep";
            }

            if (prepared.Plan.Conflicts.Any(c => string.Equals(c, "servers.dat", StringComparison.OrdinalIgnoreCase)))
            {
                string pick = SelectPrompt("servers.dat 与包内不同，采用哪个？", new[] { "保留我的", "用包里的" });
                serversMode = pick == "用包里的" ? "pack" : "keep";
            }

            if (!ConfirmPrompt("确认执行更新？", true))
            {
                UpdaterFlow.CleanupZip(prepared);
                return;
            }

            int code = UpdaterFlow.ApplyPrepared(prepared, keep, optionsMode, serversMode);
            if (code != 0)
            {
                AnsiConsole.MarkupLine("[red]更新失败（exit " + code + "）[/]");
            }
        }

        private static void MigrateAction(string gameDir)
        {
            var config = AppConfig.Load();
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                AnsiConsole.MarkupLine("[yellow]未定位游戏目录[/]");
                return;
            }

            string target = ResolveInstance(gameDir, config, "选择迁移目标实例:");
            if (string.IsNullOrEmpty(target))
            {
                AnsiConsole.MarkupLine("[yellow]未找到目标实例，请先安装[/]");
                return;
            }

            var sources = Migration.FindSources(gameDir, target);
            if (sources.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]同一游戏目录下没有可迁移的旧实例[/]");
                AnsiConsole.MarkupLine("[grey]提示：跨 .minecraft 目录的迁移暂未实现（V1 后续）。[/]");
                return;
            }

            string from = SelectPrompt("选择要迁移的旧实例:", sources);
            var categoryNames = Migration.Categories.Select(c => c.Name).ToList();
            var chosenNames = MultiPrompt("选择要迁移的类别（类别内全选）:", categoryNames);
            var chosen = Migration.Categories.Where(c => chosenNames.Contains(c.Name)).ToList();
            if (chosen.Count == 0)
            {
                return;
            }

            string sourceDir = Path.Combine(gameDir, "versions", from);
            long estimate = Migration.EstimateSize(sourceDir, chosen);
            int conflicts = Migration.CountConflicts(gameDir, from, target, chosen);
            if (conflicts > 0)
            {
                AnsiConsole.MarkupLine("[yellow]目标实例已有 " + conflicts +
                                       " 个同名文件；确认后会先备份这些文件到 rstc-backup/migrate_* 再覆盖。[/]");
            }

            if (!ConfirmPrompt("将从 " + from + " 复制约 " + (estimate / 1048576.0).ToString("F1") +
                               " MiB 到 " + target + "（源不动），继续？", true))
            {
                return;
            }

            try
            {
                if (Migration.Run(gameDir, from, target, chosen, conflicts > 0, out string summary))
                {
                    AnsiConsole.MarkupLine("[green]迁移完成[/]");
                    AnsiConsole.WriteLine(summary);
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(summary) + "[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]迁移失败: " + Markup.Escape(ex.GetType().Name + ": " + ex.Message) + "[/]");
            }
        }

        private static async Task SelfUpdateActionAsync()
        {
            SelfUpdateCheck check = await SelfUpdater.CheckAsync().ConfigureAwait(false);
            var table = new Table().Border(TableBorder.Rounded).Title("[cyan]更新器自更新[/]");
            table.AddColumn("项");
            table.AddColumn("值");
            table.AddRow("当前版本", check.Current.ToString());
            table.AddRow("远端版本", Markup.Escape(check.LatestTag ?? "未知"));
            table.AddRow("更新资产", Markup.Escape(check.AssetName ?? "无"));
            table.AddRow("结论", Markup.Escape(check.Message ?? ""));
            AnsiConsole.Write(table);

            if (!check.UpdateAvailable)
            {
                return;
            }

            if (!ConfirmPrompt("下载新版本并在退出后自动替换？", true))
            {
                return;
            }

            int code = await SelfUpdater.ApplyAsync(check).ConfigureAwait(false);
            if (code == 0)
            {
                AnsiConsole.MarkupLine("[green]程序即将退出以完成替换…[/]");
                Environment.Exit(0);
            }
        }

        private static void RollbackAction(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                AnsiConsole.MarkupLine("[yellow]未定位游戏目录[/]");
                return;
            }

            string versions = Path.Combine(gameDir, "versions");
            var candidates = new List<string>();
            if (Directory.Exists(versions))
            {
                foreach (string journalPath in Directory.EnumerateFiles(versions, ".rstc-journal.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var root = JsonNode.Parse(File.ReadAllText(journalPath, Encoding.UTF8)) as JsonObject;
                        string status = root?["status"]?.GetValue<string>() ?? "";
                        if (string.Equals(status, "writing", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(Path.GetFileName(Path.GetDirectoryName(journalPath)));
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            if (candidates.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]没有未完成的更新[/]");
                return;
            }

            candidates.Sort(StringComparer.OrdinalIgnoreCase);
            string instance = SelectPrompt("选择要回滚的实例:", candidates);
            PackApplier.Rollback(gameDir, instance);
        }

        private static string Settings(AppConfig config, string currentGameDir)
        {
            string input = TextPromptInput("游戏目录（留空保持不变）:", currentGameDir ?? "", true);
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

        private static List<string> FindInstances(string gameDir)
        {
            var instances = new List<string>();
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                return instances;
            }

            string versionsDir = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versionsDir))
            {
                return instances;
            }

            foreach (string dir in Directory.GetDirectories(versionsDir))
            {
                if (File.Exists(Path.Combine(dir, "modrinth.index.json")))
                {
                    instances.Add(Path.GetFileName(dir));
                }
            }

            if (instances.Count == 0)
            {
                foreach (string dir in Directory.GetDirectories(versionsDir))
                {
                    if (File.Exists(Path.Combine(dir, "RSTC_version.txt")))
                    {
                        instances.Add(Path.GetFileName(dir));
                    }
                }
            }

            return instances;
        }

        private static string FindCurrentInstance(string gameDir, AppConfig config)
        {
            var instances = FindInstances(gameDir);
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

        /// <summary>多实例时让用户选择，避免状态/更新对准错误实例（M8）。</summary>
        private static string ResolveInstance(string gameDir, AppConfig config, string title)
        {
            var instances = FindInstances(gameDir);
            if (instances.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(config.LastInstance) && instances.Contains(config.LastInstance))
            {
                return config.LastInstance;
            }

            if (instances.Count == 1)
            {
                return instances[0];
            }

            return SelectPrompt(title, instances);
        }
    }
}
