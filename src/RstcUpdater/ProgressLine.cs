using System;

namespace RstcUpdater
{
    /// <summary>单行进度条（\r 原地刷新）：标签 + 彩色进度条 + 百分比 + MB + 速度 + 剩余时间。</summary>
    internal static class ProgressLine
    {
        private static int _lastLength;

        public static void Draw(string label, long done, long total, double mibPerSecond, string extra = null)
        {
            int percent = total > 0 ? (int)Math.Min(100, done * 100L / total) : 0;
            const int barWidth = 28;
            int filled = Math.Max(0, Math.Min(barWidth, percent * barWidth / 100));
            string filledBar = new string('█', filled);
            string emptyBar = new string('░', barWidth - filled);
            string eta = "--:--";
            if (mibPerSecond > 0.05 && total > done)
            {
                int seconds = (int)Math.Min(359999, (total - done) / 1048576.0 / mibPerSecond);
                eta = TimeSpan.FromSeconds(seconds).ToString("mm\\:ss");
            }

            string head = "\r  " + label + " [";
            string tail = string.Format("] {0,3}%  {1:F1}/{2:F1} MB  {3:F1} MB/s  剩余 {4}",
                percent, done / 1048576.0, total / 1048576.0, mibPerSecond, eta);
            string suffix = string.IsNullOrEmpty(extra) ? "" : "  " + extra;

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(head);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(filledBar);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(emptyBar);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(tail);
            if (suffix.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(suffix);
            }

            Console.ResetColor();
            int length = VisibleLength(head) + barWidth + VisibleLength(tail) + VisibleLength(suffix);
            if (_lastLength > length)
            {
                Console.Write(new string(' ', _lastLength - length));
            }

            _lastLength = length;
            Console.Out.Flush();
        }

        public static void Clear()
        {
            if (_lastLength > 0)
            {
                Console.Write("\r" + new string(' ', Math.Min(_lastLength, 200)) + "\r");
                _lastLength = 0;
            }

            Console.Out.Flush();
        }

        private static int VisibleLength(string text)
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
