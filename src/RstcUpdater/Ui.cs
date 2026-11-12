using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RstcUpdater
{
    /// <summary>
    /// v5 终端视觉（纯 Spectre 渲染）：红石红重型边框标题面板、状态表、方向键菜单。
    /// 不手写光标/背景色，避免控制台渲染错位。
    /// </summary>
    internal static class Ui
    {
        public const string Accent = "red";
        public const string Dim = "grey";

        private const int LogoRows = 5;

        private static readonly string[][] Letters =
        {
            new[]
            {
                "██████  ",
                "██   ██ ",
                "██████  ",
                "██  ██  ",
                "██   ██ "
            },
            new[]
            {
                " ██████ ",
                "██      ",
                " █████  ",
                "      ██",
                "██████  "
            },
            new[]
            {
                "████████",
                "   ██   ",
                "   ██   ",
                "   ██   ",
                "   ██   "
            }
        };

        private static readonly string[] LogoLines = BuildLogo();

        private static string[] BuildLogo()
        {
            var rows = new List<string>();
            for (int row = 0; row < LogoRows; row++)
            {
                rows.Add(string.Join(" ", Letters.Select(l => l[row])));
            }

            return rows.ToArray();
        }

        /// <summary>标题面板：重型红框 + 居中 RST 像素字 + 名称与版本。</summary>
        public static void LogoPanel()
        {
            string version = "v" + SelfUpdater.CurrentVersion;
            string channel = ReleaseClient.BetaChannel ? "Beta 内测" : "正式版";

            var content = new List<IRenderable>();
            foreach (string line in LogoLines)
            {
                content.Add(new Markup("[red]" + Markup.Escape(line) + "[/]"));
            }

            content.Add(new Markup(" "));
            content.Add(new Markup("[bold white]红石镇客户端更新器[/]"));
            content.Add(new Markup("[grey]Minecraft 26.3 · Fabric 0.20.1[/]"));

            var body = new Rows(content.ToArray());
            var panel = new Panel(new Align(body, HorizontalAlignment.Center))
            {
                Border = BoxBorder.Heavy,
                BorderStyle = new Style(foreground: Color.Red),
                Header = new PanelHeader(" RSTC // UPDATER  ·  " + version + "  ·  " + channel + " ",
                    Justify.Left),
                Padding = new Padding(2, 0, 2, 0)
            };
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }

        /// <summary>状态表（圆角红框）；compact 时隐藏表头以压缩高度。</summary>
        public static void StatusTable(IEnumerable<(string Label, string ValueMarkup)> rows, bool compact)
        {
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.BorderStyle = new Style(foreground: Color.Red);
            if (!compact)
            {
                table.Title = new TableTitle("[red]状态[/]");
            }

            table.ShowHeaders = !compact;
            table.AddColumn(new TableColumn("[grey]项目[/]").NoWrap());
            table.AddColumn(new TableColumn("[grey]值[/]"));
            foreach (var row in rows)
            {
                table.AddRow(Markup.Escape(row.Label), row.ValueMarkup);
            }

            AnsiConsole.Write(table);
        }

        public static void Section(string title)
        {
            AnsiConsole.MarkupLine("[red]▌ " + Markup.Escape(title) + "[/]");
        }

        public static void Rows(IEnumerable<(string Label, string ValueMarkup)> rows, int labelWidth = 14)
        {
            foreach (var row in rows)
            {
                string label = PadDisplay(row.Label, labelWidth);
                AnsiConsole.MarkupLine("  [" + Dim + "]" + Markup.Escape(label) + "[/] " + row.ValueMarkup);
            }
        }

        /// <summary>方向键菜单（内部用 Spectre SelectionPrompt，渲染稳定）。返回选中下标。</summary>
        public static int ArrowMenu(IReadOnlyList<string> items, IReadOnlyList<string> descriptions = null)
        {
            var prompt = new SelectionPrompt<string>()
                .Title("[grey]↑ ↓ 移动    Enter 确认[/]")
                .PageSize(9)
                .HighlightStyle(new Style(foreground: Color.White, background: Color.Maroon))
                .AddChoices(items);
            string picked = AnsiConsole.Prompt(prompt);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == picked)
                {
                    return i;
                }
            }

            return 0;
        }

        public static string PadDisplay(string text, int width)
        {
            int current = DisplayWidth(text);
            return current >= width ? text : text + new string(' ', width - current);
        }

        public static string CenterDisplay(string text, int width)
        {
            int textWidth = DisplayWidth(text);
            int left = Math.Max(0, (width - textWidth) / 2);
            int right = Math.Max(0, width - textWidth - left);
            return new string(' ', left) + text + new string(' ', right);
        }

        /// <summary>CJK 字符按 2 列计算（近似，足以对齐中文标签）。</summary>
        public static int DisplayWidth(string text)
        {
            int width = 0;
            foreach (char c in text)
            {
                width += c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF) ||
                                          (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) ||
                                          (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) ||
                                          (c >= 0xFFE0 && c <= 0xFFE6))
                    ? 2
                    : 1;
            }

            return width;
        }

        public static int SafeWidth()
        {
            try
            {
                int consoleWidth = Console.WindowWidth;
                return consoleWidth > 20 ? consoleWidth : 80;
            }
            catch (Exception)
            {
                return 80;
            }
        }

        public static string Fit(string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                return text;
            }

            if (DisplayWidth(text) <= maxWidth)
            {
                return text;
            }

            var builder = new System.Text.StringBuilder();
            int width = 0;
            foreach (char c in text)
            {
                int charWidth = DisplayWidth(c.ToString());
                if (width + charWidth > maxWidth - 3)
                {
                    break;
                }

                builder.Append(c);
                width += charWidth;
            }

            return builder.Append("...").ToString();
        }
    }
}
