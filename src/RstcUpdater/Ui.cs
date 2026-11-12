using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>终端视觉：ASCII 横幅、分区线、状态行、页脚（青色 CRT 终端风格）。</summary>
    internal static class Ui
    {
        private const string Accent = "aqua";
        private const string Dim = "grey";

        private static readonly string[] BannerWide =
        {
            @" ____  ____  _____ ____     _   _ ____  ____    _    _____ _____ ____ ",
            @"|  _ \/ ___||_   _/ ___|   | | | |  _ \|  _ \  / \  |_   _| ____|  _ \",
            @"| |_) \___ \  | || |       | | | | |_) | | | |/ _ \   | | |  _| | |_) |",
            @"|  _ < ___) | | || |___    | |_| |  __/| |_| / ___ \  | | | |___|  _ <",
            @"|_| \_\____/  |_| \____|    \___/|_|   |____/_/   \_\ |_| |_____|_| \_\"
        };

        private static readonly string[] BannerCompact =
        {
            @"  RSTC-UPDATER  ::  Minecraft 26.3  ::  client updater"
        };

        public static void Banner()
        {
            int width = SafeWidth();
            if (width >= 80)
            {
                foreach (string line in BannerWide)
                {
                    AnsiConsole.MarkupLine("[" + Accent + "]" + Markup.Escape(line) + "[/]");
                }
            }
            else
            {
                foreach (string line in BannerCompact)
                {
                    AnsiConsole.MarkupLine("[" + Accent + "]" + Markup.Escape(line) + "[/]");
                }
            }

            AnsiConsole.MarkupLine("[grey]  " + Markup.Escape("PCL2 / HMCL 双启动器 · 发布包不含 PCL/ · 支持 Beta 内测通道") + "[/]");
            AnsiConsole.WriteLine();
        }

        public static void Section(string title)
        {
            int width = Math.Max(40, SafeWidth() - 2);
            string head = "-- " + title + " ";
            int rest = Math.Max(0, width - DisplayWidth(head));
            AnsiConsole.MarkupLine("[" + Accent + "]" + Markup.Escape(head) + new string('-', rest) + "[/]");
        }

        public static void Rows(IEnumerable<(string Label, string ValueMarkup)> rows, int labelWidth = 14)
        {
            foreach (var row in rows)
            {
                string label = PadDisplay(row.Label, labelWidth);
                AnsiConsole.MarkupLine("  [" + Dim + "]" + Markup.Escape(label) + "[/] " + row.ValueMarkup);
            }
        }

        public static void Footer()
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [" + Dim + "]>" + Markup.Escape(" 版本 " + SelfUpdater.CurrentVersion + " · 通道 " + (ReleaseClient.BetaChannel ? "beta" : "stable") + " · 发布包不含 PCL/") + "[/]");
        }

        /// <summary>按显示宽度截断文本（超出加 ...），用于游戏目录等长路径。</summary>
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
        public static string PadDisplay(string text, int width)
        {
            int current = DisplayWidth(text);
            if (current >= width)
            {
                return text;
            }

            return text + new string(' ', width - current);
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

        /// <summary>编号菜单（交互式）；返回选中序号（0 基）。</summary>
        public static int NumberMenu(IReadOnlyList<string> actions, IReadOnlyList<string> descriptions)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                string tag = "[" + (i + 1) + "]";
                string desc = descriptions != null && i < descriptions.Count ? descriptions[i] : "";
                AnsiConsole.MarkupLine("  [" + Accent + "]" + Markup.Escape(tag.PadRight(4)) + "[/]" +
                                       Markup.Escape(actions[i].PadRight(12)) +
                                       "[" + Dim + "]" + Markup.Escape(desc) + "[/]");
            }

            AnsiConsole.WriteLine();
            return AnsiConsole.Prompt(
                new TextPrompt<int>("  输入编号")
                    .PromptStyle(Accent)
                    .ValidationErrorMessage("[red]请输入有效编号[/]")
                    .Validate(n => n >= 1 && n <= actions.Count
                        ? ValidationResult.Success()
                        : ValidationResult.Error("范围 1-" + actions.Count))) - 1;
        }
    }
}
