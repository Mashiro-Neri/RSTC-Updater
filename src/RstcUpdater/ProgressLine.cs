using System;

namespace RstcUpdater
{
    /// <summary>单行进度条（\r 原地刷新）：标签 + 彩色进度条 + 百分比 + MB + 速度 + 剩余时间。</summary>
    internal static class ProgressLine
    {
        private static int _lastLength;

        public static bool ParallelRequested;
        private static bool _parallel;
        private static long _vDone, _vTotal, _pDone, _pTotal;
        private static double _vSpeed, _pSpeed;
        private static bool _vFinished, _pFinished;
        private static string _vSummary = "", _pSummary = "";

        public static bool Parallel
        {
            get { return _parallel; }
        }

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

        public static void BeginParallel()
        {
            ParallelRequested = false;
            _parallel = true;
            _vDone = _vTotal = _pDone = _pTotal = 0;
            _vSpeed = _pSpeed = 0;
            _vFinished = _pFinished = false;
            _vSummary = _pSummary = "";
        }

        public static void UpdateParallelVanilla(long done, long total, double speed)
        {
            _vDone = done;
            _vTotal = total;
            _vSpeed = speed;
            DrawParallel();
        }

        public static void UpdateParallelPack(long done, long total, double speed)
        {
            _pDone = done;
            _pTotal = total;
            _pSpeed = speed;
            DrawParallel();
        }

        public static void SetParallelVanillaSummary(string summary)
        {
            _vFinished = true;
            _vSummary = summary;
            DrawParallel();
        }

        public static void SetParallelPackSummary(string summary)
        {
            _pFinished = true;
            _pSummary = summary;
            DrawParallel();
        }

        public static void FinishParallel()
        {
            _parallel = false;
            Clear();
            if (!string.IsNullOrEmpty(_vSummary))
            {
                Console.WriteLine("  " + _vSummary);
            }

            if (!string.IsNullOrEmpty(_pSummary))
            {
                Console.WriteLine("  " + _pSummary);
            }

            _vSummary = _pSummary = "";
            _vFinished = _pFinished = false;
            Console.Out.Flush();
        }

        public static void AbortParallel()
        {
            _parallel = false;
            ParallelRequested = false;
            Clear();
        }

        private static void DrawParallel()
        {
            if (!_parallel)
            {
                return;
            }

            int width;
            try
            {
                width = Console.WindowWidth;
            }
            catch (Exception)
            {
                width = 0;
            }

            if (width < 90)
            {
                string shortLine = string.Format("\r  原版 {0}%  整合包 {1}%  合计 {2:F1} MB/s   ",
                    Percent(_vDone, _vTotal), Percent(_pDone, _pTotal),
                    (_vFinished ? 0 : _vSpeed) + (_pFinished ? 0 : _pSpeed));
                Console.Write(shortLine);
                _lastLength = shortLine.Length;
                Console.Out.Flush();
                return;
            }

            const int barWidth = 14;
            string head = "\r  原版 [";
            string mid = "   整合包 [";
            string vInfo = _vFinished
                ? "完成"
                : _vTotal > 0
                    ? string.Format("{0,3}%  {1:F0}/{2:F0}MB", Percent(_vDone, _vTotal),
                        _vDone / 1048576.0, _vTotal / 1048576.0)
                    : "准备中";
            string pInfo = _pFinished
                ? "完成"
                : _pTotal > 0
                    ? string.Format("{0,3}%  {1:F0}/{2:F0}MB", Percent(_pDone, _pTotal),
                        _pDone / 1048576.0, _pTotal / 1048576.0)
                    : "准备中";
            string tail = string.Format("   合计 {0:F1} MB/s   ",
                (_vFinished ? 0 : _vSpeed) + (_pFinished ? 0 : _pSpeed));

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(head);
            WriteParallelBar(_vDone, _vTotal, _vFinished);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write("] " + vInfo);
            Console.Write(mid);
            WriteParallelBar(_pDone, _pTotal, _pFinished);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write("] " + pInfo);
            Console.Write(tail);
            Console.ResetColor();

            int length = VisibleLength(head) + barWidth + VisibleLength("] " + vInfo) + VisibleLength(mid) +
                         barWidth + VisibleLength("] " + pInfo) + VisibleLength(tail);
            if (_lastLength > length)
            {
                Console.Write(new string(' ', _lastLength - length));
            }

            _lastLength = length;
            Console.Out.Flush();
        }

        private static void WriteParallelBar(long done, long total, bool finished)
        {
            const int barWidth = 14;
            int percent = finished ? 100 : Percent(done, total);
            int filled = finished ? barWidth : Math.Max(0, Math.Min(barWidth, percent * barWidth / 100));
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(new string('█', filled));
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(new string('░', barWidth - filled));
        }

        private static int Percent(long done, long total)
        {
            return total > 0 ? (int)Math.Min(100, done * 100L / total) : 0;
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
