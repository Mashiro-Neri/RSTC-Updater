using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;

namespace RstcUpdater
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var mode = args.Length > 0 ? args[0] : "--probe";
            switch (mode)
            {
                case "--probe":
                    var manifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
                    for (var i = 1; i < args.Length - 1; i++)
                    {
                        if (args[i] == "--url")
                        {
                            manifestUrl = args[i + 1];
                        }
                    }

                    return await ProbeAsync(manifestUrl);
                default:
                    Console.Error.WriteLine("unknown mode: " + mode);
                    return 2;
            }
        }

        private static async Task<int> ProbeAsync(string manifestUrl)
        {
            AnsiConsole.Write(new Rule("[cyan]RSTC-Updater 5.0 · net48 probe[/]").RuleStyle("grey"));

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("项目");
            table.AddColumn("值");
            table.AddRow("运行时", Environment.Version.ToString());
            table.AddRow("系统", Environment.OSVersion.ToString());
            table.AddRow("64 位进程", Environment.Is64BitProcess.ToString());

            try
            {
                var mcPath = new CmlLib.Core.MinecraftPath(Path.Combine(Path.GetTempPath(), "rstc-probe"));
                table.AddRow("CmlLib.Core", "已加载：" + mcPath.BasePath);
            }
            catch (Exception ex)
            {
                table.AddRow("CmlLib.Core", "加载失败：" + ex.GetType().Name + ": " + ex.Message);
            }

            AnsiConsole.Write(table);

            try
            {
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("RSTC-Updater/5.0-probe");

                    var manifest = await http.GetStringAsync(manifestUrl);
                    using (var doc = JsonDocument.Parse(manifest))
                    {
                        JsonElement? target = null;
                        foreach (var version in doc.RootElement.GetProperty("versions").EnumerateArray())
                        {
                            if (version.GetProperty("id").GetString() == "26.3")
                            {
                                target = version;
                                break;
                            }
                        }

                        if (!target.HasValue)
                        {
                            AnsiConsole.MarkupLine("[red]未在版本清单中找到 26.3[/]");
                            return 3;
                        }

                        var versionUrl = target.Value.GetProperty("url").GetString();
                        var versionJson = await http.GetStringAsync(versionUrl);
                        using (var versionDoc = JsonDocument.Parse(versionJson))
                        {
                            var root = versionDoc.RootElement;
                            var java = root.GetProperty("javaVersion").GetProperty("majorVersion").GetInt32();
                            var mainClass = root.GetProperty("mainClass").GetString();
                            var assetIndex = root.GetProperty("assetIndex").GetProperty("id").GetString();

                            AnsiConsole.MarkupLine(
                                $"[green]26.3[/] java={java} assetIndex={assetIndex} mainClass={mainClass}");

                            return java == 25 && assetIndex == "34" &&
                                   mainClass == "net.minecraft.client.main.Main"
                                ? 0
                                : 4;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]探针失败：[/]" + ex.GetType().Name + ": " + ex.Message);
                return 5;
            }
        }
    }
}
