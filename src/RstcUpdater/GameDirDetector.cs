using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RstcUpdater
{
    /// <summary>V1：候选游戏目录探测（不扫全盘，只查常见位置 + 配置记忆 + 当前目录）。</summary>
    internal static class GameDirDetector
    {
        public static List<string> FindCandidates(string lastGameDir)
        {
            var candidates = new List<string>();
            void Add(string dir, bool requireMarker = true)
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return;
                }

                string full;
                try
                {
                    full = Path.GetFullPath(dir).TrimEnd('\\');
                }
                catch (Exception)
                {
                    return;
                }

                if (candidates.Any(c => string.Equals(c, full, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                if (!Directory.Exists(full))
                {
                    return;
                }

                if (requireMarker && !LooksLikeMinecraftDir(full))
                {
                    return;
                }

                candidates.Add(full);
            }

            Add(lastGameDir, false);

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add(Path.Combine(appData, ".minecraft"));
            Add(Path.Combine(userProfile, "Desktop", ".minecraft"));
            Add(Path.Combine(userProfile, "Downloads", ".minecraft"));
            Add(Path.Combine(Environment.CurrentDirectory, ".minecraft"));
            Add(Environment.CurrentDirectory);

            return candidates;
        }

        public static bool LooksLikeMinecraftDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return false;
            }

            return Directory.Exists(Path.Combine(dir, "versions"))
                   || File.Exists(Path.Combine(dir, "launcher_profiles.json"))
                   || File.Exists(Path.Combine(dir, "options.txt"));
        }
    }
}
