using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RstcUpdater
{
    /// <summary>
    /// 真正的终端 TUI：ANSI/VT 全屏布局 + 方向键菜单。
    /// 关键点：每帧整屏重绘、监听窗口尺寸变化并重绘，缩放/拖动后自动恢复正确排版。
    /// </summary>
    internal static class Tui
    {
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        public const string Red = "\x1b[31m";
        private const string BrightRed = "\x1b[91m";
        public const string Grey = "\x1b[90m";
        public const string White = "\x1b[97m";
        public const string Green = "\x1b[32m";
        public const string Yellow = "\x1b[33m";
        private const string Reset = "\x1b[0m";
        private const string Bar = "\x1b[41m\x1b[97m";
        private const string Sel = "\x1b[41m\x1b[97m";

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
            Console.Write("\x1b[?25h\x1b[0m\x1b[2J\x1b[H");
            IntPtr handle = GetStdHandle(StdOutputHandle);
            SetConsoleMode(handle, _oldMode);
        }

        private sealed class Snapshot
        {
            public int Width;
            public int Height;
        }

        /// <summary>方向键菜单主循环。返回选中下标；Ctrl+C/Esc 返回 -1。</summary>
        public static int Menu(
            IReadOnlyList<string> items,
            IReadOnlyList<string> descriptions,
            IReadOnlyList<(string Label, string DotColor, string Value)> status,
            string version,
            string channel)
        {
            int selected = 0;
            var snapshot = new Snapshot { Width = -1, Height = -1 };
            Render(snapshot, selected, items, descriptions, status, version, channel, true);

            while (true)
            {
                int currentWidth = Console.WindowWidth;
                int currentHeight = Console.WindowHeight;
                if (snapshot.Width != currentWidth || snapshot.Height != currentHeight)
                {
                    Render(snapshot, selected, items, descriptions, status, version, channel, true);
                }

                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        selected = (selected - 1 + items.Count) % items.Count;
                        Render(snapshot, selected, items, descriptions, status, version, channel, false);
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        selected = (selected + 1) % items.Count;
                        Render(snapshot, selected, items, descriptions, status, version, channel, false);
                    }
                    else if (key.Key == ConsoleKey.Enter)
                    {
                        Console.Write("\x1b[?25h\x1b[2J\x1b[H");
                        return selected;
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        Console.Write("\x1b[?25h\x1b[2J\x1b[H");
                        return -1;
                    }
                    else if (key.KeyChar >= '1' && key.KeyChar <= '9' && key.KeyChar - '1' < items.Count)
                    {
                        selected = key.KeyChar - '1';
                        Render(snapshot, selected, items, descriptions, status, version, channel, false);
                    }
                }
                else
                {
                    Thread.Sleep(30);
                }
            }
        }

        private static void Render(
            Snapshot snapshot,
            int selected,
            IReadOnlyList<string> items,
            IReadOnlyList<string> descriptions,
            IReadOnlyList<(string Label, string DotColor, string Value)> status,
            string version,
            string channel,
            bool clearFirst)
        {
            int width = Math.Max(40, Math.Min(96, SafeWindowWidth() - 1));
            int height = Math.Max(12, SafeWindowHeight());
            snapshot.Width = SafeWindowWidth();
            snapshot.Height = SafeWindowHeight();

            var lines = new List<string>();
            bool full = height >= 27;

            string left = " RSTC // UPDATER";
            string right = version + " · " + channel + " ";
            lines.Add(Bar + Pad(left + new string(' ', Math.Max(1, width - DisplayWidth(left) - DisplayWidth(right))) + right, width) + Reset);

            if (full)
            {
                foreach (string logo in Logo)
                {
                    lines.Add(Red + Center(logo, width) + Reset);
                }

                lines.Add(White + Center("红石镇客户端更新器", width) + Reset);
                lines.Add(Grey + Center("Minecraft 26.3 · Fabric 0.20.1", width) + Reset);
            }
            else
            {
                lines.Add(White + Center("红石镇客户端更新器  ·  Minecraft 26.3", width) + Reset);
            }

            lines.Add(Red + new string('─', Math.Min(width, 96)) + Reset);
            lines.Add("");

            foreach (var row in status)
            {
                string label = Pad(row.Label, 10);
                string value = Fit(row.Value, Math.Max(10, width - 16));
                lines.Add(" " + Grey + label + Reset + " " + row.DotColor + "●" + Reset + " " + White + value + Reset);
            }

            lines.Add("");
            lines.Add(Red + "操作" + Reset);
            for (int i = 0; i < items.Count; i++)
            {
                string desc = descriptions != null && i < descriptions.Count ? descriptions[i] : "";
                if (i == selected)
                {
                    string text = "  ▶ " + Pad(items[i], 16) + " " + desc;
                    lines.Add(Sel + Pad(text, width) + Reset);
                }
                else
                {
                    lines.Add("    " + White + Pad(items[i], 16) + Reset + "  " + Grey + desc + Reset);
                }
            }

            lines.Add(Grey + "  ↑ ↓ 移动    Enter 确认    Ctrl+C 退出" + Reset);

            int maxLines = Math.Max(8, height - 1);
            var builder = new StringBuilder();
            builder.Append(clearFirst ? "\x1b[2J\x1b[H" : "\x1b[H");
            for (int i = 0; i < lines.Count && i < maxLines; i++)
            {
                builder.Append(lines[i]);
                if (i < lines.Count - 1 && i < maxLines - 1)
                {
                    builder.Append('\n');
                }
            }

            builder.Append("\x1b[0J");
            Console.Write(builder.ToString());
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
