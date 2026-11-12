using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// v5 终端视觉：固定宽度（60 列）的滚动式布局，纯文本顺序输出。
    /// 不做全屏重绘、不用绝对光标、不依赖窗口宽度，窗口拖动/缩放不会变形。
    /// </summary>
    internal static class Ui
    {
        public const string Accent = "red";
        public const string Dim = "grey";

        /// <summary>内容画布宽度（显示列）。所有输出行都不超过它。</summary>
        public const int W = 76;

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
            for (int row = 0; row < 5; row++)
            {
                rows.Add(string.Join(" ", Letters.Select(l => l[row])));
            }

            return rows.ToArray();
        }

        /// <summary>顶部标志块：只打印一次，之后作为滚动历史存在。</summary>
        public static void Header()
        {
            string version = "v" + SelfUpdater.CurrentVersion;
            string channel = ReleaseClient.BetaChannel ? "BETA" : "RELEASE";
            string barLeft = " RSTC // UPDATER";
            string barRight = version + " · " + channel + " ";

            AnsiConsole.MarkupLine("[white on red]" + Markup.Escape(PadDisplay(barLeft + new string(' ', Math.Max(1, W - DisplayWidth(barLeft) - DisplayWidth(barRight))) + barRight, W)) + "[/]");
            foreach (string line in LogoLines)
            {
                AnsiConsole.MarkupLine("[red]" + Markup.Escape(CenterDisplay(line, W)) + "[/]");
            }

            AnsiConsole.MarkupLine("[bold white]" + Markup.Escape(CenterDisplay("红石镇客户端更新器", W)) + "[/]");
            AnsiConsole.MarkupLine("[grey]" + Markup.Escape(CenterDisplay("Minecraft 26.3 · Fabric 0.20.1", W)) + "[/]");
            AnsiConsole.MarkupLine("[red]" + new string('─', W) + "[/]");
            AnsiConsole.WriteLine();
        }

        /// <summary>状态块：固定两列排版，标签宽 12，值超宽截断。</summary>
        public static void StatusBlock(IEnumerable<(string Label, string ValueMarkup)> rows)
        {
            AnsiConsole.MarkupLine("[red]状态[/]");
            foreach (var row in rows)
            {
                AnsiConsole.MarkupLine("  [" + Dim + "]" + Markup.Escape(PadDisplay(row.Label, 12)) + "[/] " + row.ValueMarkup);
            }

            AnsiConsole.WriteLine();
        }

        /// <summary>编号菜单：动作 + 说明，编号右对齐；不重绘，窗口缩放不影响。</summary>
        public static void Menu(IReadOnlyList<string> items, IReadOnlyList<string> descriptions = null)
        {
            AnsiConsole.MarkupLine("[red]操作[/]");
            for (int i = 0; i < items.Count; i++)
            {
                string desc = descriptions != null && i < descriptions.Count ? descriptions[i] : "";
                string number = (i + 1).ToString();
                string name = PadDisplay(items[i], 18);
                desc = Fit(desc, Math.Max(8, W - 24));
                AnsiConsole.MarkupLine("  [red]" + number + "[/]) " + Markup.Escape(name) + "[" + Dim + "]" + Markup.Escape(desc) + "[/]");
            }

            AnsiConsole.MarkupLine("  [" + Dim + "]输入编号并回车；Ctrl+C 退出[/]");
        }

        public static void Section(string title)
        {
            AnsiConsole.MarkupLine("[red]" + Markup.Escape(title) + "[/]");
        }

        public static void Rows(IEnumerable<(string Label, string ValueMarkup)> rows, int labelWidth = 14)
        {
            foreach (var row in rows)
            {
                string label = PadDisplay(row.Label, labelWidth);
                AnsiConsole.MarkupLine("  [" + Dim + "]" + Markup.Escape(label) + "[/] " + row.ValueMarkup);
            }
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

        /// <summary>按显示宽度截断文本（超出加 ...）。</summary>
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
