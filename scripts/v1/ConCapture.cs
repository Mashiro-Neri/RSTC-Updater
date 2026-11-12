using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class ConCapture
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;

        public COORD(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        [FieldOffset(0)] public int bKeyDown;
        [FieldOffset(4)] public ushort wRepeatCount;
        [FieldOffset(6)] public ushort wVirtualKeyCode;
        [FieldOffset(8)] public ushort wVirtualScanCode;
        [FieldOffset(10)] public char UnicodeChar;
        [FieldOffset(12)] public int dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);


    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacter(IntPtr hConsoleOutput, StringBuilder lpCharacter,
        uint nLength, COORD dwReadCoord, out uint lpNumberOfCharsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleWindowInfo(IntPtr hConsoleOutput, bool bAbsolute, ref SMALL_RECT lpConsoleWindow);    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleOutputAttribute(IntPtr hConsoleOutput, ushort[] lpAttribute,
        uint nLength, COORD dwReadCoord, out uint lpNumberOfAttrsRead);    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput,
        out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength,
        out uint lpNumberOfEventsWritten);

    private static IntPtr _out;
    private static IntPtr _in;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ConCapture <exe> <outdir> [keys]");
            return 2;
        }

        string exe = args[0];
        string outdir = args[1];
        Directory.CreateDirectory(outdir);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        };
        if (args.Length > 3)
        {
            psi.Arguments = args[3];
        }
        Process process = Process.Start(psi);
        Thread.Sleep(2500);

        bool attached = false;
        for (int attempt = 0; attempt < 20 && !attached; attempt++)
        {
            FreeConsole();
            attached = AttachConsole((uint)process.Id);
            if (!attached)
            {
                Thread.Sleep(250);
            }
        }

        if (!attached)
        {
            File.WriteAllText(Path.Combine(outdir, "error.txt"), "attach failed");
            return 3;
        }

        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x1;
        const uint FILE_SHARE_WRITE = 0x2;
        const uint OPEN_EXISTING = 3;
        _out = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        _in = CreateFile("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (_out == IntPtr.Zero || _out == new IntPtr(-1))
        {
            File.WriteAllText(Path.Combine(outdir, "error.txt"), "open CONOUT$ failed");
            return 4;
        }
        Capture(Path.Combine(outdir, "frame-0.txt"));

        int frame = 1;
        if (args.Length > 2)
        {
            foreach (string step in args[2].Split(','))
            {
                if (step.StartsWith("DOWN", StringComparison.OrdinalIgnoreCase))
                {
                    int n = 1;
                    if (step.Contains(":"))
                    {
                        int.TryParse(step.Substring(step.IndexOf(':') + 1), out n);
                    }

                    for (int i = 0; i < n; i++)
                    {
                        Key(0x28);
                        Thread.Sleep(120);
                    }
                }
                else if (step.StartsWith("UP", StringComparison.OrdinalIgnoreCase))
                {
                    int n = 1;
                    if (step.Contains(":"))
                    {
                        int.TryParse(step.Substring(step.IndexOf(':') + 1), out n);
                    }

                    for (int i = 0; i < n; i++)
                    {
                        Key(0x26);
                        Thread.Sleep(120);
                    }
                }
                else if (string.Equals(step, "ENTER", StringComparison.OrdinalIgnoreCase))
                {
                    Key(0x0D);
                    Thread.Sleep(900);
                }
                else if (string.Equals(step, "SPACE", StringComparison.OrdinalIgnoreCase))
                {
                    Key(0x20);
                    Thread.Sleep(500);
                }
                else if (step.StartsWith("TYPE:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (char ch in step.Substring(5))
                    {
                        KeyUnicode(ch);
                        Thread.Sleep(40);
                    }
                }
                else if (string.Equals(step, "ESC", StringComparison.OrdinalIgnoreCase))
                {
                    Key(0x1B);
                    Thread.Sleep(300);
                }
                else if (string.Equals(step, "CTRLC", StringComparison.OrdinalIgnoreCase))
                {
                    KeyCtrlC();
                    Thread.Sleep(600);
                }
                else if (string.Equals(step, "WAIT", StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(900);
                }
                else if (string.Equals(step, "RESIZE", StringComparison.OrdinalIgnoreCase))
                {
                    SetConsoleScreenBufferSize(_out, new COORD(120, 900));
                    var rect = new SMALL_RECT { Left = 0, Top = 0, Right = 99, Bottom = 34 };
                    SetConsoleWindowInfo(_out, true, ref rect);
                    Thread.Sleep(700);
                }

                Capture(Path.Combine(outdir, "frame-" + frame + ".txt"));
                frame++;
            }
        }

        FreeConsole();
        try
        {
            var titles = new System.Collections.Generic.List<string>();
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)process.Id && IsWindowVisible(h))
                {
                    var sb = new StringBuilder(512);
                    GetWindowText(h, sb, sb.Capacity);
                    if (sb.Length > 0)
                    {
                        titles.Add(sb.ToString());
                    }
                }

                return true;
            }, IntPtr.Zero);
            File.WriteAllLines(Path.Combine(outdir, "windows.txt"), titles.ToArray(), new UTF8Encoding(false));
        }
        catch (Exception)
        {
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private static void Capture(string path)
    {
        CONSOLE_SCREEN_BUFFER_INFO info;
        if (!GetConsoleScreenBufferInfo(_out, out info))
        {
            File.WriteAllText(path, "(no console info)");
            return;
        }

        int left = info.srWindow.Left;
        int top = info.srWindow.Top;
        int width = info.srWindow.Right - left + 1;
        int height = info.srWindow.Bottom - top + 1;
        var text = new StringBuilder();
        for (int row = 0; row < height; row++)
        {
            var line = new StringBuilder(width);
            uint read;
            ReadConsoleOutputCharacter(_out, line, (uint)width, new COORD((short)left, (short)(top + row)), out read);
            text.AppendLine(line.ToString().TrimEnd());
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));

        var attrs = new ushort[width];
        uint attrsRead;
        ReadConsoleOutputAttribute(_out, attrs, (uint)width, new COORD((short)left, (short)top), out attrsRead);
        var bg = new StringBuilder();
        for (int row = 0; row < height; row++)
        {
            uint n;
            ReadConsoleOutputAttribute(_out, attrs, (uint)width, new COORD((short)left, (short)(top + row)), out n);
            int first = -1;
            int lastBg = -1;
            for (int x = 0; x < width; x++)
            {
                int back = (attrs[x] >> 4) & 0x000F;
                if (back != 0)
                {
                    if (first < 0) first = x;
                    lastBg = x;
                }
            }

            bg.AppendLine(row + " bg=" + (first >= 0 ? first + ".." + lastBg : "-"));
        }

        File.WriteAllText(path + ".bg.txt", bg.ToString(), new UTF8Encoding(false));
    }

    private static void KeyUnicode(char ch)
    {
        var records = new INPUT_RECORD[2];
        records[0].EventType = 1;
        records[0].KeyEvent.bKeyDown = 1;
        records[0].KeyEvent.wRepeatCount = 1;
        records[0].KeyEvent.UnicodeChar = ch;
        records[1] = records[0];
        records[1].KeyEvent.bKeyDown = 0;
        uint written;
        WriteConsoleInput(_in, records, 2, out written);
    }

    private static void KeyCtrlC()
    {
        var records = new INPUT_RECORD[2];
        records[0].EventType = 1;
        records[0].KeyEvent.bKeyDown = 1;
        records[0].KeyEvent.wRepeatCount = 1;
        records[0].KeyEvent.wVirtualKeyCode = 0x43;
        records[0].KeyEvent.UnicodeChar = '\x03';
        records[0].KeyEvent.dwControlKeyState = 0x0008;
        records[1] = records[0];
        records[1].KeyEvent.bKeyDown = 0;
        uint written;
        WriteConsoleInput(_in, records, 2, out written);
    }

    private static void Key(ushort virtualKey)
    {
        var records = new INPUT_RECORD[2];
        records[0].EventType = 1;
        records[0].KeyEvent.bKeyDown = 1;
        records[0].KeyEvent.wRepeatCount = 1;
        records[0].KeyEvent.wVirtualKeyCode = virtualKey;
        if (virtualKey == 0x0D)
        {
            records[0].KeyEvent.UnicodeChar = '\r';
        }
        else if (virtualKey == 0x20)
        {
            records[0].KeyEvent.UnicodeChar = ' ';
        }
        records[1] = records[0];
        records[1].KeyEvent.bKeyDown = 0;
        uint written;
        WriteConsoleInput(_in, records, 2, out written);
    }
}
