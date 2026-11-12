using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// v5 终端视觉（HUD/复古终端风）：红石红满宽标题条、3D 像素 RST、双线规则线、
    /// 反白方向键菜单、分段状态栏。
    /// </summary>
    internal static class Ui
    {
        public const string Accent = "red";
        public const string dim = "grey";
        public const string Dim = dim;

        private const int LogoRows = 5;

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
            for (int row = 0; row < LogoRows; row++)
            {
                rows.Add(string.Join(" ", Letters.Select(l => l[row])));
            }

            return rows.ToArray();
        }

        public static int ContentWidth()
        {
            return Math.Min(70, Math.Max(30, SafeWidth() - 2));
        }

        public static int SafeHeight()
        {
            try
            {
                int consoleHeight = Console.WindowHeight;
                return consoleHeight > 10 ? consoleHeight : 30;
            }
            catch (Exception)
            {
                return 30;
            }
        }

        /// <summary>HUD 标题区：满宽红条 + 3D 像素 RST + 标题行 + 双线规则线。</summary>
        public static void LogoBox(bool animate = false)
        {
            int width = ContentWidth();
            if (SafeHeight() < 24)
            {
                string v = "v" + SelfUpdater.CurrentVersion;
                string ch = ReleaseClient.BetaChannel ? "Beta 内测" : "正式版";
                string l = " RSTC // UPDATER";
                string r = v + "  ·  " + ch + " ";
                int g = Math.Max(1, width - DisplayWidth(l) - DisplayWidth(r));
                AnsiConsole.MarkupLine("[white on red]" + Markup.Escape(l) + new string(' ', g) + Markup.Escape(r) + "[/]");
                AnsiConsole.MarkupLine("[" + Accent + "]" + new string('═', width) + "[/]");
                return;
            }

            string version = "v" + SelfUpdater.CurrentVersion;
            string channel = ReleaseClient.BetaChannel ? "Beta 内测" : "正式版";
            string left = " RSTC // UPDATER";
            string right = version + "  ·  " + channel + " ";

            int gap = Math.Max(1, width - DisplayWidth(left) - DisplayWidth(right));
            AnsiConsole.MarkupLine("[white on red]" + Markup.Escape(left) + new string(' ', gap) + Markup.Escape(right) + "[/]");

            int logoWidth = LogoLines.Max(l => l.Length);
            int pad = Math.Max(0, (width - logoWidth) / 2);
            for (int row = 0; row < LogoLines.Length; row++)
            {
                string color = row < 3 ? Accent : "maroon";
                AnsiConsole.MarkupLine(new string(' ', pad) + "[" + color + "]" + Markup.Escape(LogoLines[row]) + "[/]");
                if (animate)
                {
                    Thread.Sleep(16);
                }
            }

            string title = "红石镇客户端更新器";
            string subtitle = "Minecraft 26.3 · Fabric 0.20.1";
            string plainTitleLine = title + "   »   " + subtitle;
            if (DisplayWidth(plainTitleLine) <= width)
            {
                int titlePad = Math.Max(0, (width - DisplayWidth(plainTitleLine)) / 2);
                AnsiConsole.MarkupLine(new string(' ', titlePad) + "[bold white]" + Markup.Escape(title) + "[/]" +
                                       "   [" + Accent + "]»[/]   " + "[" + dim + "]" + Markup.Escape(subtitle) + "[/]");
            }
            else
            {
                int titlePad = Math.Max(0, (width - DisplayWidth(title)) / 2);
                AnsiConsole.MarkupLine(new string(' ', titlePad) + "[bold white]" + Markup.Escape(title) + "[/]");
                int subPad = Math.Max(0, (width - DisplayWidth(subtitle)) / 2);
                AnsiConsole.MarkupLine(new string(' ', subPad) + "[" + dim + "]" + Markup.Escape(subtitle) + "[/]");
            }
            if (animate)
            {
                SweepRule(width);
            }
            else
            {
                AnsiConsole.MarkupLine("[" + Accent + "]" + new string('═', width) + "[/]");
            }

            AnsiConsole.WriteLine();
        }

        /// <summary>3D 像素字：前层亮红，右/下加暗红斜面。</summary>
        private static string[] BuildBevelLogo()
        {
            int width = LogoLines.Max(l => l.Length) + 1;
            var result = new List<string>();
            for (int row = 0; row <= LogoRows; row++)
            {
                string current = row < LogoRows ? LogoLines[row] : new string(' ', width);
                string above = row > 0 ? LogoLines[row - 1] : new string(' ', width);
                var markup = new System.Text.StringBuilder();
                string color = null;
                for (int col = 0; col < width; col++)
                {
                    bool front = col < current.Length && current[col] == '█';
                    bool rightEdge = !front && col > 0 && col - 1 < current.Length && current[col - 1] == '█';
                    bool bottomEdge = !front && !rightEdge && row > 0 && col < above.Length && above[col] == '█';
                    string want = front ? "red" : (rightEdge || bottomEdge ? "maroon" : null);
                    if (want != color)
                    {
                        if (color != null)
                        {
                            markup.Append("[/]");
                        }

                        if (want != null)
                        {
                            markup.Append("[" + want + "]");
                        }

                        color = want;
                    }

                    markup.Append(want == null ? ' ' : (front ? '█' : '▓'));
                }

                if (color != null)
                {
                    markup.Append("[/]");
                }

                result.Add(markup.ToString());
            }

            return result.ToArray();
        }

        private static int PlainWidth(string markup)
        {
            string plain = System.Text.RegularExpressions.Regex.Replace(markup, @"\[[^\]]*\]", "");
            return DisplayWidth(plain);
        }

        public static void Section(string title)
        {
            int width = Math.Max(40, SafeWidth() - 2);
            string head = "▌ ▌ " + title + " ";
            int rest = Math.Max(0, width - DisplayWidth(head) - 1);
            AnsiConsole.MarkupLine("[" + Accent + "]▌[/] [white on red] " + Markup.Escape(title) + " [/]" +
                                   "[" + dim + "]" + new string('─', rest) + "[/]");
        }

        /// <summary>扫描线动画（boot 时使用，约 250ms）。</summary>
        public static void SweepRule(int width)
        {
            for (int step = 4; step < width; step += 5)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write(new string('═', step) + new string(' ', width - step));
                    Console.SetCursorPosition(0, Console.CursorTop);
                }
                catch (Exception)
                {
                    break;
                }

                Thread.Sleep(8);
            }

            AnsiConsole.MarkupLine("[" + Accent + "]" + new string('═', width) + "[/]");
        }

        /// <summary>HUD 状态行：给定标签与值，渲染一整行。</summary>
        public static void StatusLine(string label, string valueMarkup)
        {
            AnsiConsole.MarkupLine("  [white on red] " + Markup.Escape(PadDisplay(label, 5)) + " [/] " + valueMarkup);
        }

        public static void Rows(IEnumerable<(string Label, string ValueMarkup)> rows, int labelWidth = 14)
        {
            foreach (var row in rows)
            {
                string label = PadDisplay(row.Label, labelWidth);
                AnsiConsole.MarkupLine("  [white on red] " + Markup.Escape(label) + " [/] " + row.ValueMarkup);
            }
        }

        /// <summary>全宽反白方向键菜单：选中行带说明。返回选中下标。</summary>
        public static int ArrowMenu(IReadOnlyList<string> items, IReadOnlyList<string> descriptions = null)
        {
            int selected = 0;
            int startTop = Math.Max(0, Console.CursorTop);
            int width = ContentWidth();

            void Render()
            {
                for (int i = 0; i < items.Count; i++)
                {
                    TrySetCursor(0, startTop + i);
                    Console.Write(new string(' ', Math.Min(SafeWidth() - 1, 72)));
                    TrySetCursor(0, startTop + i);
                    if (i == selected)
                    {
                        Console.BackgroundColor = ConsoleColor.DarkRed;
                        Console.ForegroundColor = ConsoleColor.White;
                        string desc = descriptions != null && i < descriptions.Count ? descriptions[i] : "";
                        string line = "  ▶ " + PadDisplay(items[i], 14) + "  " + desc;
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
