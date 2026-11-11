using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RstcUpdater
{
    internal sealed class JavaInfo
    {
        public string Path;
        public int MajorVersion;
        public bool Is64Bit;
    }

    /// <summary>V1：Java 检测（只检测不下载，符合文档 §6.3）。</summary>
    internal static class JavaDetector
    {
        public static List<JavaInfo> Detect(string gameDir)
        {
            var results = new List<JavaInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Probe(string javaExe)
            {
                if (string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe))
                {
                    return;
                }

                string full;
                try
                {
                    full = Path.GetFullPath(javaExe);
                }
                catch (Exception)
                {
                    return;
                }

                if (!seen.Add(full))
                {
                    return;
                }

                var info = ReadVersion(full);
                if (info != null)
                {
                    results.Add(info);
                }
            }

            if (!string.IsNullOrEmpty(gameDir))
            {
                string runtime = Path.Combine(gameDir, "runtime");
                if (Directory.Exists(runtime))
                {
                    foreach (string exe in Directory.EnumerateFiles(runtime, "java.exe", SearchOption.AllDirectories))
                    {
                        Probe(exe);
                    }
                }
            }

            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                Probe(Path.Combine(javaHome, "bin", "java.exe"));
            }

            foreach (string root in new[]
                     {
                         @"C:\Program Files\Java",
                         @"C:\Program Files\Eclipse Adoptium",
                         @"C:\Program Files\Microsoft",
                         @"C:\Program Files\Zulu",
                         @"C:\Program Files\BellSoft",
                         @"C:\Program Files (x86)\Java"
                     })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (string exe in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories).Take(20))
                    {
                        Probe(exe);
                    }
                }
                catch (Exception)
                {
                }
            }

            // PATH 上的 java
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string part in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                Probe(Path.Combine(part.Trim(), "java.exe"));
            }

            return results.OrderByDescending(j => j.MajorVersion).ToList();
        }

        public static JavaInfo Best(List<JavaInfo> javas, int required)
        {
            return javas.FirstOrDefault(j => j.MajorVersion >= required);
        }

        private static JavaInfo ReadVersion(string javaExe)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = javaExe,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    var match = Regex.Match(output, "version \"(\\d+)(?:\\.(\\d+))?");
                    if (!match.Success)
                    {
                        return null;
                    }

                    int major = int.Parse(match.Groups[1].Value);
                    if (major == 1 && match.Groups[2].Success)
                    {
                        major = int.Parse(match.Groups[2].Value);
                    }

                    bool is64 = output.IndexOf("64-Bit", StringComparison.OrdinalIgnoreCase) >= 0;
                    return new JavaInfo { Path = javaExe, MajorVersion = major, Is64Bit = is64 };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
