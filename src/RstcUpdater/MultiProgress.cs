using System;
using System.Text;

namespace RstcUpdater
{
    /// <summary>
    /// 并行下载进度：一行内同时显示原版与整合包两条 mini 进度条，原地刷新。
    /// 全部完成后清空该行并输出两条完成摘要。非交互/重定向时自动退化为单条顺序进度。
    /// </summary>
    internal static class MultiProgress
    {
        private static readonly object Sync = new object();
        private static bool _active;
        private static int _row = -1;
        private static long _vDone;
        private static long _vTotal;
        private static double _vSpeed;
        private static long _pDone;
        private static long _pTotal;
        private static double _pSpeed;
        private static int _vWaitingSeconds;
        private static bool _vFinished;
        private static bool _pFinished;
        private static string _vSummary = "";
        private static string _pSummary = "";
        private static int _lastWidth;

        /// <summary>安装流程是否请求了并行进度（Program 打印完头部后调用 Begin）。</summary>
        public static bool Requested;

        public static bool Active
        {
            get
            {
                lock (Sync)
                {
                    return _active;
                }
            }
        }

        public static void Begin()
        {
            lock (Sync)
            {
                if (_active)
                {
                    return;
                }

                try
                {
                    Console.Out.Write(Environment.NewLine);
                    _row = Math.Max(0, Console.CursorTop - 1);
                    _active = true;
                    _vWaitingSeconds = 0;
                    _vFinished = false;
                    _pFinished = false;
                    _vSummary = "";
                    _pSummary = "";
                    DrawLocked();
                    RestoreCursor(0);
                }
                catch (Exception)
                {
                    _active = false;
                }
            }
        }

        public static void SetVanillaWaiting(int seconds)
        {
            lock (Sync)
            {
                _vWaitingSeconds = seconds;
                DrawLocked();
            }
        }

        public static void UpdateVanilla(long done, long total, double speed)
        {
            lock (Sync)
            {
                _vDone = done;
                _vTotal = total;
                _vSpeed = speed;
                _vWaitingSeconds = 0;
                DrawLocked();
            }
        }

        public static void UpdatePack(long done, long total, double speed)
        {
            lock (Sync)
            {
                _pDone = done;
                _pTotal = total;
                _pSpeed = speed;
                DrawLocked();
            }
        }

        public static void SetVanillaFinished(string summary)
        {
            lock (Sync)
            {
                _vFinished = true;
                _vSummary = summary;
                DrawLocked();
            }
        }

        public static void SetPackFinished(string summary)
        {
            lock (Sync)
            {
                _pFinished = true;
                _pSummary = summary;
                DrawLocked();
            }
        }

        /// <summary>结束并行进度：清空进度行，输出两条完成摘要（若存在）。</summary>
        public static void End()
        {
            lock (Sync)
            {
                if (!_active)
                {
                    return;
                }

                _active = false;
                try
                {
                    if (_row >= 0)
                    {
                        Console.SetCursorPosition(0, _row);
                        Console.Write("\x1b[K");
                        if (!string.IsNullOrEmpty(_vSummary))
                        {
                            Console.WriteLine(_vSummary);
                            _row++;
                        }

                        if (!string.IsNullOrEmpty(_pSummary))
                        {
                            Console.SetCursorPosition(0, _row);
                            Console.Write(_pSummary);
                            Console.Write("\x1b[K");
                            Console.WriteLine();
                            _row++;
                        }

                        Console.SetCursorPosition(0, _row);
                    }
                }
                catch (Exception)
                {
                }

                _row = -1;
                _lastWidth = 0;
                Console.Out.Flush();
            }
        }

        private static void RestoreCursor(int fallbackRow)
        {
            try
            {
                Console.SetCursorPosition(0, Math.Max(0, _row + 1));
            }
            catch (Exception)
            {
            }
        }

        public static void Abort()
        {
            lock (Sync)
            {
                if (!_active)
                {
                    return;
                }

                _active = false;
                try
                {
                    if (_row >= 0)
                    {
                        Console.SetCursorPosition(0, _row);
                        Console.Write("\x1b[K");
                    }
                }
                catch (Exception)
                {
                }

                _row = -1;
                _lastWidth = 0;
            }
        }

        private static void DrawLocked()
        {
            if (!_active || _row < 0)
            {
                return;
            }

            try
            {
                int width = Console.WindowWidth;
                if (width <= 20)
                {
                    return;
                }

                int cursorTop = Console.CursorTop;
                int cursorLeft = Console.CursorLeft;
                string line = BuildLine();
                if (line.Length > width - 2)
                {
                    line = line.Substring(0, width - 2);
                }

                Console.SetCursorPosition(0, _row);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(line);
                if (_lastWidth > line.Length)
                {
                    Console.Write(new string(' ', _lastWidth - line.Length));
                }

                Console.ResetColor();
                Console.Out.Flush();
                _lastWidth = line.Length;
                Console.SetCursorPosition(cursorLeft, cursorTop);
            }
            catch (Exception)
            {
            }
        }

        private static string BuildLine()
        {
            var builder = new StringBuilder("  ");
            builder.Append("原版 ");
            if (_vFinished)
            {
                builder.Append("完成    ");
            }
            else if (_vTotal <= 0)
            {
                builder.Append("准备中 " + _vWaitingSeconds + "s ");
            }
            else
            {
                AppendBar(builder, _vDone, _vTotal, 10);
                builder.Append(' ');
            }

            builder.Append("  整合包 ");
            if (_pFinished)
            {
                builder.Append("完成    ");
            }
            else
            {
                AppendBar(builder, _pDone, _pTotal, 10);
            }

            double totalSpeed = (_vFinished ? 0 : _vSpeed) + (_pFinished ? 0 : _pSpeed);
            if (totalSpeed > 0.05)
            {
                builder.Append("  " + totalSpeed.ToString("F1") + " MB/s");
            }

            return builder.ToString();
        }

        private static void AppendBar(StringBuilder builder, long done, long total, int width)
        {
            int percent = total > 0 ? (int)Math.Min(100, done * 100L / total) : 0;
            int filled = Math.Max(0, Math.Min(width, percent * width / 100));
            builder.Append('[');
            builder.Append(new string('#', filled));
            builder.Append(new string('-', width - filled));
            builder.Append("] ");
            builder.Append(percent.ToString().PadLeft(3));
            builder.Append('%');
        }
    }
}
