using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// v5 终端视觉（精致版）：红石红/暗色，双线边框标题区、像素块 RST 标志、
    /// 分段状态面板（状态圆点）、全宽反白方向键菜单。
    /// </summary>
    internal static class Ui
    {
        public const string Accent = "red";
        public const string Dim = "grey";

        // RST 像素字：每个字母 8 列 × 5 行，字母间 1 空格
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

        public static void LogoBox()
        {
            int inner = Math.Min(64, Math.Max(50, SafeWidth() - 4));
            string version = "v" + SelfUpdater.CurrentVersion;
            string channel = ReleaseClient.BetaChannel ? "Beta 内测" : "正式版";
            string headerLeft = " RSTC // UPDATER";
            string headerRight = version + "  ·  " + channel + " ";

            AnsiConsole.MarkupLine("[" + Accent + "]╔" + new string('═', inner) + "╗[/]");
            int gap = Math.Max(1, inner - DisplayWidth(headerLeft) - DisplayWidth(headerRight));
            AnsiConsole.MarkupLine("[" + Accent + "]║[/]" + "[bold white]" + Markup.Escape(headerLeft) + "[/]" +
                                   new string(' ', gap) + "[" + Dim + "]" + Markup.Escape(headerRight) + "[/]" +
                                   "[" + Accent + "]║[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]╟" + new string('─', inner) + "╢[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]║[/]" + new string(' ', inner) + "[" + Accent + "]║[/]");

            foreach (string line in LogoLines)
            {
                AnsiConsole.MarkupLine("[" + Accent + "]║[/]" + "[" + Accent + "]" +
                                       CenterDisplay(line, inner) + "[/]" + "[" + Accent + "]║[/]");
            }

            AnsiConsole.MarkupLine("[" + Accent + "]║[/]" + new string(' ', inner) + "[" + Accent + "]║[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]║[/]" + "[bold white]" + CenterDisplay("红石镇客户端更新器", inner) + "[/]" +
                                   "[" + Accent + "]║[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]║[/]" + "[" + Dim + "]" +
                                   CenterDisplay("Minecraft 26.3 · Fabric 0.20.1", inner) + "[/]" +
                                   "[" + Accent + "]║[/]");
            AnsiConsole.MarkupLine("[" + Accent + "]╚" + new string('═', inner) + "╝[/]");
            AnsiConsole.WriteLine();
        }

        public static void Section(string title)
        {
            int width = Math.Max(40, SafeWidth() - 2);
            string head = "› " + title + " ";
            int rest = Math.Max(0, width - DisplayWidth(head) - 2);
            AnsiConsole.MarkupLine("[" + Accent + "]" + Markup.Escape(head) + "[/]" + "[" + Dim + "]" +
                                   new string('─', rest) + "[/]");
        }

        public static void Rows(IEnumerable<(string Label, string ValueMarkup)> rows, int labelWidth = 14)
        {
            foreach (var row in rows)
            {
                string label = PadDisplay(row.Label, labelWidth);
                AnsiConsole.MarkupLine("  [" + Dim + "]" + Markup.Escape(label) + "[/] " + row.ValueMarkup);
            }
        }

        /// <summary>全宽反白方向键菜单（仅交互模式）。返回选中下标。</summary>
        public static int ArrowMenu(IReadOnlyList<string> items)
        {
            int selected = 0;
            int startTop = Math.Max(0, Console.CursorTop);
            int width = Math.Min(SafeWidth() - 2, 66);

            void Render()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    TrySetCursor(0, startTop + i);
                    Console.Write(new string(' ', Math.Max(0, Math.Min(SafeWidth() - 1, 70))));
                    TrySetCursor(0, startTop + i);
                    if (i == selected)
                    {
                        Console.BackgroundColor = ConsoleColor.DarkRed;
                        Console.ForegroundColor = ConsoleColor.White;
                        string line = "  ▶ " + items[i];
                        int pad = Math.Max(0, width - DisplayWidth(line));
                        Console.Write(line + new string(' ', pad));
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("    " + items[i]);
                        Console.ResetColor();
                    }
                }

                TrySetCursor(0, startTop + items.Count);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  ↑ ↓ 移动    Enter 确认");
                Console.ResetColor();
            }

            Render();
            while (true)
            {
                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(true);
                }
                catch (InvalidOperationException)
                {
                    Console.WriteLine();
                    return selected;
                }

                if (key.Key == ConsoleKey.UpArrow)
                {
                    selected = (selected - 1 + items.Count) % items.Count;
                    Render();
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    selected = (selected + 1) % items.Count;
                    Render();
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    TrySetCursor(0, startTop + items.Count + 1);
                    return selected;
                }
            }
        }

        private static void TrySetCursor(int left, int top)
        {
            try
            {
                Console.SetCursorPosition(left, Math.Max(0, top));
            }
            catch (Exception)
            {
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
    }
}
