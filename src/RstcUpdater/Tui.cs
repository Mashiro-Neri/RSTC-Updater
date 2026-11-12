using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RstcUpdater
{
    /// <summary>
    /// 终端 TUI（v5 HUD 风格）：满宽红条标题、像素 RST、红字标题行、状态 HUD、方向键菜单。
    /// 颜色全部走 ConsoleColor（Red = #E74856），VT 只做光标/清屏；每帧整屏重绘并监听 resize。
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
            int canvas = Math.Max(40, Math.Min(78, windowWidth - 1));
            bool compact = windowHeight < 27;

            Console.ResetColor();
            Console.Write(clearFirst ? "\x1b[2J\x1b[H" : "\x1b[H");

            int row = 0;

            // 1) 满宽红条（白字）
            string barLeft = " RSTC // UPDATER";
            string barRight = version + " · " + channel + " ";
            int gap = Math.Max(1, canvas - DisplayWidth(barLeft) - DisplayWidth(barRight));
            SetCursor(0, row);
            Console.BackgroundColor = ConsoleColor.Red;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(barLeft + new string(' ', gap) + barRight);
            Console.ResetColor();
            Console.Write("\x1b[K");
            row++;

            // 2) 红条与标志之间留空，避免“黏住”
            BlankRow(row++);

            if (!compact)
            {
                foreach (string logo in Logo)
                {
                    WriteLine(row++, Center(logo, canvas), ConsoleColor.Red);
                }
            }

            // 3) 标题行：中文白 + » 红 + 副标题灰
            string title = "红石镇客户端更新器";
            string subtitle = "Minecraft 26.3 · Fabric 0.20.1";
            string plain = title + "   »   " + subtitle;
            int titlePad = Math.Max(0, (canvas - DisplayWidth(plain)) / 2);
            SetCursor(0, row);
            Console.Write(new string(' ', titlePad));
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(title);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("   »   ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(subtitle);
            Console.ResetColor();
            Console.Write("\x1b[K");
            row++;

            // 4) 红色双线
            WriteLine(row++, new string('═', canvas), ConsoleColor.Red);
            BlankRow(row++);

            // 5) 状态
            WriteSection(row++, canvas, "状态");
            if (status != null && status.Count >= 6)
            {
                WriteStatus(row++, canvas, "PATH", status[0].Dot, status[0].Value);
                WriteStatus(row++, canvas, "ENV", status[1].Dot,
                    status[1].Value + "  │  整合包 " + status[2].Value);
                WriteStatus(row++, canvas, "NET", status[3].Dot,
                    status[3].Value + "  │  " + status[4].Value + "  │  隔离 " + ShortIsolation(status[5].Value));
                BlankRow(row++);
            }

            // 6) 操作区
            WriteSection(row++, canvas, "操作");
            for (int i = 0; i < items.Count; i++)
            {
                string desc = descriptions != null && i < descriptions.Count ? descriptions[i] : "";
                string text = (i == selected ? "  ▶ " : "    ") + Pad(items[i], 16) + " " + desc;
                SetCursor(0, row);
                if (i == selected)
                {
                    Console.BackgroundColor = ConsoleColor.Red;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write(Pad(text, canvas));
                    Console.ResetColor();
                    Console.Write("\x1b[K");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    string name = "    " + Pad(items[i], 16);
                    Console.Write(name);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(" " + desc);
                    Console.ResetColor();
                    PadTo(canvas, DisplayWidth(name) + 1 + DisplayWidth(desc));
                    Console.Write("\x1b[K");
                }

                row++;
            }

            SetCursor(0, row);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  ↑ ↓ 移动    Enter 确认    Ctrl+C 退出");
            Console.ResetColor();
            Console.Write("\x1b[K");
            row++;

            SetCursor(0, Math.Min(row, Math.Max(0, windowHeight - 1)));
            Console.Write("\x1b[0J");
            Console.ResetColor();
        }

        private static void WriteLine(int row, string text, ConsoleColor foreground)
        {
            SetCursor(0, row);
            Console.ForegroundColor = foreground;
            Console.Write(text);
            Console.ResetColor();
            Console.Write("\x1b[K");
        }

        private static void BlankRow(int row)
        {
            SetCursor(0, row);
            Console.ResetColor();
            Console.Write("\x1b[K");
        }

        private static void WriteSection(int row, int canvas, string title)
        {
            SetCursor(0, row);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("▌ " + title + " ");
            int used = DisplayWidth("▌ " + title + " ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(new string('─', Math.Max(0, canvas - used)));
            Console.ResetColor();
            Console.Write("\x1b[K");
        }

        private static void WriteStatus(int row, int canvas, string label, ConsoleColor dotColor, string value)
        {
            SetCursor(0, row);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            string prefix = "  " + Pad(label, 6) + " ";
            Console.Write(prefix);
            Console.ForegroundColor = dotColor;
            Console.Write("●");
            Console.ForegroundColor = ConsoleColor.Gray;
            int room = Math.Max(6, canvas - DisplayWidth(prefix) - 2);
            string shown = Fit(value, room);
            Console.Write(" " + shown);
            Console.ResetColor();
            PadTo(canvas, DisplayWidth(prefix) + 1 + 1 + DisplayWidth(shown));
            Console.Write("\x1b[K");
        }

        private static string ShortIsolation(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "未检测到";
            }

            if (value.Contains("PCL2") && value.Contains("HMCL"))
            {
                return "PCL2 + HMCL";
            }

            if (value.Contains("PCL2"))
            {
                return "PCL2";
            }

            if (value.Contains("HMCL"))
            {
                return "HMCL";
            }

            return value.Contains("未检测") ? "未检测到" : Fit(value, 20);
        }

        private static void PadTo(int canvas, int used)
        {
            int pad = canvas - used;
            if (pad > 0)
            {
                Console.Write(new string(' ', pad));
            }
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

        private static readonly string[] Logo =
        {
            "██████   ██████  ████████",
            "██   ██ ██          ██   ",
            "██████  ██████      ██   ",
            "██  ██        ██    ██   ",
            "██   ██ ██████      ██   "
        };

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
