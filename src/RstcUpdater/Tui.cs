using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RstcUpdater
{
    /// <summary>
    /// 终端 TUI：致敬 v4.11 的红色边框风格（黑色底、亮红 RST 标志与细边框），方向键菜单。
    /// 颜色用 ConsoleColor（Red 对应 #E74856，和旧版一致的亮红），VT 只负责光标/清屏。
    /// 每帧整屏重绘并监听窗口尺寸变化，缩放/拖动后自动恢复排版。
    /// </summary>
    internal static class Tui
    {
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private static uint _oldMode;

        public static bool Enter()
        {
            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (!GetConsoleMode(handle, out _oldMode))
            {
                return false;
            }

            if (!SetConsoleMode(handle, _oldMode | EnableVirtualTerminalProcessing))
            {
                return false;
            }

            try
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    Console.ResetColor();
                    Console.Write("\x1b[?25h\x1b[0m\x1b[2J\x1b[H");
                };
            }
            catch (Exception)
            {
            }

            Console.Write("\x1b[?25l\x1b[2J\x1b[H");
            return true;
        }

        public static void Leave()
        {
            Console.ResetColor();
            Console.Write("\x1b[?25h\x1b[0m\x1b[2J\x1b[H");
            IntPtr handle = GetStdHandle(StdOutputHandle);
            SetConsoleMode(handle, _oldMode);
        }

        private struct Segment
        {
            public string Text;
            public ConsoleColor Foreground;
            public ConsoleColor Background;
            public bool HasBackground;
            public bool PadToWidth;
        }

        private sealed class Line
        {
            public readonly List<Segment> Segments = new List<Segment>();
        }

        private static Segment Text(string text, ConsoleColor foreground)
        {
            return new Segment { Text = text, Foreground = foreground, HasBackground = false };
        }

        private static Segment Filled(string text, ConsoleColor foreground, ConsoleColor background)
        {
            return new Segment { Text = text, Foreground = foreground, Background = background, HasBackground = true, PadToWidth = true };
        }

        private static Line One(params Segment[] segments)
        {
            var line = new Line();
            line.Segments.AddRange(segments);
            return line;
        }

        public static int Menu(
            IReadOnlyList<string> items,
            IReadOnlyList<string> descriptions,
            IReadOnlyList<(string Label, ConsoleColor Dot, string Value)> status,
            string version,
            string channel)
        {
            int selected = 0;
            int lastWidth = -1;
            int lastHeight = -1;

            Draw(selected, items, descriptions, status, version, channel, ref lastWidth, ref lastHeight, true);

            while (true)
            {
                if (Console.WindowWidth != lastWidth || Console.WindowHeight != lastHeight)
                {
                    Draw(selected, items, descriptions, status, version, channel, ref lastWidth, ref lastHeight, true);
                }

                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        selected = (selected - 1 + items.Count) % items.Count;
                        Draw(selected, items, descriptions, status, version, channel, ref lastWidth, ref lastHeight, false);
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        selected = (selected + 1) % items.Count;
                        Draw(selected, items, descriptions, status, version, channel, ref lastWidth, ref lastHeight, false);
                    }
                    else if (key.Key == ConsoleKey.Enter)
                    {
                        Console.ResetColor();
                        Console.Write("\x1b[?25h\x1b[2J\x1b[H");
                        return selected;
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        Console.ResetColor();
                        Console.Write("\x1b[?25h\x1b[2J\x1b[H");
                        return -1;
                    }
                    else if (key.KeyChar >= '1' && key.KeyChar <= '9' && key.KeyChar - '1' < items.Count)
                    {
                        selected = key.KeyChar - '1';
                        Draw(selected, items, descriptions, status, version, channel, ref lastWidth, ref lastHeight, false);
                    }
                }
                else
                {
                    Thread.Sleep(30);
                }
            }
        }

        private static void Draw(
            int selected,
            IReadOnlyList<string> items,
            IReadOnlyList<string> descriptions,
            IReadOnlyList<(string Label, ConsoleColor Dot, string Value)> status,
            string version,
            string channel,
            ref int lastWidth,
            ref int lastHeight,
            bool clearFirst)
        {
            int windowWidth = SafeWindowWidth();
            int windowHeight = SafeWindowHeight();
            lastWidth = windowWidth;
            lastHeight = windowHeight;
            int width = Math.Max(40, Math.Min(96, windowWidth - 1));
            bool full = windowHeight >= 30;

            var lines = new List<Line>();
            string inner = new string('-', width - 2);

            lines.Add(One(Text("+" + inner + "+", ConsoleColor.Red)));

            string left = " RSTC // UPDATER";
            string right = version + " | " + channel + " ";
            string header = Pad(left + new string(' ', Math.Max(1, width - 2 - DisplayWidth(left) - DisplayWidth(right))) + right, width - 2);
            lines.Add(One(Text("|", ConsoleColor.Red), Text(header, ConsoleColor.White), Text("|", ConsoleColor.Red)));

            if (full)
            {
                lines.Add(One(Text("|" + new string(' ', width - 2) + "|", ConsoleColor.Red)));
                foreach (string logo in Logo)
                {
                    lines.Add(One(Text("|", ConsoleColor.Red), Text(Center(logo, width - 2), ConsoleColor.Red), Text("|", ConsoleColor.Red)));
                }
            }

            lines.Add(One(Text("+" + inner + "+", ConsoleColor.Red)));
            lines.Add(One(Text(Center("红石镇客户端更新器", width), ConsoleColor.White)));
            lines.Add(One(Text(Center("Minecraft 26.3 | Fabric 0.20.1", width), ConsoleColor.DarkGray)));
            lines.Add(One(Text("", ConsoleColor.Gray)));

            foreach (var row in status)
            {
                string label = Pad(row.Label, 10);
                string value = Fit(row.Value, Math.Max(10, width - 16));
                lines.Add(One(
                    Text(" " + label + " ", ConsoleColor.DarkGray),
                    Text("*", row.Dot),
                    Text(" " + value, ConsoleColor.Gray)));
            }

            lines.Add(One(Text("", ConsoleColor.Gray)));
            lines.Add(One(Text("操作", ConsoleColor.Red)));
            for (int i = 0; i < items.Count; i++)
            {
                string desc = descriptions != null && i < descriptions.Count ? descriptions[i] : "";
                if (i == selected)
                {
                    string text = "  > " + Pad(items[i], 16) + " " + desc;
                    lines.Add(One(Filled(text, ConsoleColor.White, ConsoleColor.DarkRed)));
                }
                else
                {
                    lines.Add(One(
                        Text("    " + Pad(items[i], 16), ConsoleColor.Gray),
                        Text("  " + desc, ConsoleColor.DarkGray)));
                }
            }

            lines.Add(One(Text("  ^ v 移动    Enter 确认    Ctrl+C 退出", ConsoleColor.DarkGray)));

            int maxLines = Math.Max(6, windowHeight - 1);
            Console.ResetColor();
            Console.Write(clearFirst ? "\x1b[2J\x1b[H" : "\x1b[H");
            for (int i = 0; i < lines.Count && i < maxLines; i++)
            {
                int used = 0;
                foreach (var segment in lines[i].Segments)
                {
                    string text = segment.Text;
                    if (segment.HasBackground)
                    {
                        int pad = width - used - DisplayWidth(text);
                        if (pad > 0)
                        {
                            text += new string(' ', pad);
                        }
                    }

                    Console.ForegroundColor = segment.Foreground;
                    if (segment.HasBackground)
                    {
                        Console.BackgroundColor = segment.Background;
                    }
                    else
                    {
                        Console.ResetColor();
                        Console.ForegroundColor = segment.Foreground;
                    }

                    Console.Write(text);
                    used += DisplayWidth(text);
                    Console.ResetColor();
                }

                Console.Write("\x1b[K");
                if (i < lines.Count - 1 && i < maxLines - 1)
                {
                    Console.Write("\n");
                }
            }

            Console.Write("\x1b[0J");
            Console.ResetColor();
        }

        private static int SafeWindowWidth()
        {
            try
            {
                return Console.WindowWidth > 20 ? Console.WindowWidth : 80;
            }
            catch (Exception)
            {
                return 80;
            }
        }

        private static int SafeWindowHeight()
        {
            try
            {
                return Console.WindowHeight > 10 ? Console.WindowHeight : 30;
            }
            catch (Exception)
            {
                return 30;
            }
        }

        private static readonly string[][] Letters =
        {
            new[]
            {
                "#####",
                "#   #",
                "#####",
                "#  # ",
                "#   #"
            },
            new[]
            {
                " ####",
                "#    ",
                " ####",
                "    #",
                "#### "
            },
            new[]
            {
                "#######",
                "   #   ",
                "   #   ",
                "   #   ",
                "   #   "
            }
        };

        private static readonly string[] Logo = BuildLogo();

        private static string[] BuildLogo()
        {
            var rows = new List<string>();
            int width = Letters.Max(l => l.Max(r => r.Length));
            for (int row = 0; row < 5; row++)
            {
                var parts = Letters.Select(l => l[row].PadRight(width)).ToArray();
                string combined = string.Join("  ", parts);
                var scaled = new StringBuilder();
                foreach (char c in combined)
                {
                    scaled.Append(c == '#' ? "##" : "  ");
                }

                rows.Add(scaled.ToString());
            }

            return rows.ToArray();
        }

        private static string ScaleLogo(string line)
        {
            var builder = new StringBuilder();
            foreach (char c in line)
            {
                builder.Append(c == '#' ? "##" : "  ");
            }

            return builder.ToString();
        }

        private static string Center(string text, int width)
        {
            int textWidth = DisplayWidth(text);
            int left = Math.Max(0, (width - textWidth) / 2);
            int right = Math.Max(0, width - textWidth - left);
            return new string(' ', left) + text + new string(' ', right);
        }

        private static string Pad(string text, int width)
        {
            int current = DisplayWidth(text);
            return current >= width ? text : text + new string(' ', width - current);
        }

        private static string Fit(string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || DisplayWidth(text) <= maxWidth)
            {
                return text;
            }

            var builder = new StringBuilder();
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

        private static int DisplayWidth(string text)
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
    }
}
