using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RstcUpdater
{
    /// <summary>
    /// 终端 TUI：完全复刻 v4.11 的视觉（红色 ╔═╗ 方框 + ANSI Shadow RST 标志 + 红字标题；
    /// 菜单选中行 White on DarkCyan + ▶；底部 ↑↓ 移动 Enter 确认）。
    /// 负责整屏重绘与窗口尺寸监听。
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

            Draw(selected, items, descriptions, status, version, ref lastWidth, ref lastHeight, true);

            while (true)
            {
                if (Console.WindowWidth != lastWidth || Console.WindowHeight != lastHeight)
                {
                    Draw(selected, items, descriptions, status, version, ref lastWidth, ref lastHeight, true);
                }

                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        selected = (selected - 1 + items.Count) % items.Count;
                        Draw(selected, items, descriptions, status, version, ref lastWidth, ref lastHeight, false);
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        selected = (selected + 1) % items.Count;
                        Draw(selected, items, descriptions, status, version, ref lastWidth, ref lastHeight, false);
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
                        Draw(selected, items, descriptions, status, version, ref lastWidth, ref lastHeight, false);
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
            ref int lastWidth,
            ref int lastHeight,
            bool clearFirst)
        {
            int windowWidth = SafeWindowWidth();
            int windowHeight = SafeWindowHeight();
            lastWidth = windowWidth;
            lastHeight = windowHeight;
            bool compact = windowHeight < 24;

            Console.ResetColor();
            Console.Write(clearFirst ? "\x1b[2J\x1b[H" : "\x1b[H");

            int row = 0;
            const int inner = 50;

            WriteLine(ref row, "  ╔" + new string('═', inner) + "╗", ConsoleColor.Red);
            WriteLine(ref row, "  ║" + new string(' ', inner) + "║", ConsoleColor.Red);

            if (!compact)
            {
                foreach (string art in BannerArt)
                {
                    string padRight = new string(' ', Math.Max(0, inner - 4 - art.Length));
                    WriteLine(ref row, "  ║" + new string(' ', 4) + art + padRight + "║", ConsoleColor.Red);
                }

                WriteLine(ref row, "  ║" + new string(' ', inner) + "║", ConsoleColor.Red);
            }

            string title = "红石镇客户端更新器 " + version;
            int titleWidth = DisplayWidth(title);
            int padLeft = Math.Max(0, (inner - titleWidth) / 2);
            int padRightTitle = Math.Max(0, inner - titleWidth - padLeft);
            WriteLine(ref row, "  ║" + new string(' ', padLeft) + title + new string(' ', padRightTitle) + "║", ConsoleColor.Red);
            WriteLine(ref row, "  ║" + new string(' ', inner) + "║", ConsoleColor.Red);
            WriteLine(ref row, "  ╚" + new string('═', inner) + "╝", ConsoleColor.Red);
            WriteLine(ref row, "", ConsoleColor.Gray);

            // 状态：旧版主界面没有，保留一行精简信息，不破坏旧版观感
            if (status != null && status.Count >= 2)
            {
                WriteLine(ref row, "  游戏目录  " + Fit(status[0].Value, Math.Max(20, Math.Min(70, windowWidth - 16))), ConsoleColor.DarkGray);
            }

            WriteLine(ref row, "", ConsoleColor.Gray);
            WriteLine(ref row, "  请选择：", ConsoleColor.DarkGray);

            for (int i = 0; i < items.Count; i++)
            {
                if (i == selected)
                {
                    WriteLine(ref row, "  ▶ " + items[i], ConsoleColor.White, ConsoleColor.DarkCyan);
                }
                else
                {
                    WriteLine(ref row, "    " + items[i], ConsoleColor.Gray);
                }
            }

            WriteLine(ref row, "  ↑↓ 移动  Enter 确认", ConsoleColor.DarkGray);

            SetCursor(0, Math.Min(row, Math.Max(0, windowHeight - 1)));
            Console.Write("\x1b[0J");
            Console.ResetColor();
        }

        private static void WriteLine(ref int row, string text, ConsoleColor foreground, ConsoleColor? background = null)
        {
            SetCursor(0, row);
            Console.ForegroundColor = foreground;
            if (background.HasValue)
            {
                Console.BackgroundColor = background.Value;
            }
            else
            {
                Console.ResetColor();
                Console.ForegroundColor = foreground;
            }

            Console.Write(text);
            Console.ResetColor();
            Console.Write("\x1b[K");
            row++;
        }

        private static void SetCursor(int left, int top)
        {
            try
            {
                Console.SetCursorPosition(Math.Max(0, left), Math.Max(0, top));
            }
            catch (Exception)
            {
            }
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

        private static readonly string[] BannerArt =
        {
            "██████╗ ███████╗████████╗",
            "██╔══██╗██╔════╝╚══██╔══╝",
            "██████╔╝███████╗   ██║",
            "██╔══██╗╚════██║   ██║",
            "██║  ██║███████║   ██║",
            "╚═╝  ╚═╝╚══════╝   ╚═╝"
        };

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
