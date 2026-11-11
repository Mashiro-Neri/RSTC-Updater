using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// V1 白名单迁移：只搬玩家数据类别，不搬 mods/config（整合包自带）。
    /// 覆盖目标已有文件时：默认拒绝；显式覆盖前先把目标文件备份到 rstc-backup/migrate_*。
    /// </summary>
    internal static class Migration
    {
        internal sealed class Category
        {
            public string Key;
            public string Name;
            public string[] Paths;
            public bool NeedsWildcard;

            public override string ToString()
            {
                return Name;
            }
        }

        public static readonly List<Category> Categories = new List<Category>
        {
            new Category { Key = "saves", Name = "存档", Paths = new[] { "saves" } },
            new Category { Key = "screenshots", Name = "截图", Paths = new[] { "screenshots" } },
            new Category { Key = "maps", Name = "地图/小地图", Paths = new[] { "xaero", "journeymap", "XaeroWaypoints" }, NeedsWildcard = true },
            new Category { Key = "dh", Name = "Distant Horizons 缓存", Paths = new[] { "Distant_Horizons_server_data", @"config\DistantHorizons.toml" } },
            new Category { Key = "settings", Name = "游戏设置与键位", Paths = new[] { "options.txt", "servers.dat" } }
        };

        public static List<string> FindSources(string gameDir, string excludeInstance)
        {
            var result = new List<string>();
            string versions = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versions))
            {
                return result;
            }

            foreach (string dir in Directory.GetDirectories(versions))
            {
                string name = Path.GetFileName(dir);
                if (string.Equals(name, excludeInstance, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(name, Provisioner.TargetVersion, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, Provisioner.FabricVersionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(dir, "options.txt")) ||
                    Directory.Exists(Path.Combine(dir, "mods")) ||
                    File.Exists(Path.Combine(dir, "RSTC_version.txt")))
                {
                    result.Add(name);
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>把类别解析成实际源路径（支持 XaeroWaypoints* 多目录）。</summary>
        private static List<string> ResolvePaths(string instanceDir, Category category, string path)
        {
            var result = new List<string>();
            if (category.NeedsWildcard && path == "XaeroWaypoints")
            {
                if (Directory.Exists(instanceDir))
                {
                    result.AddRange(Directory.GetDirectories(instanceDir, "XaeroWaypoints*"));
                }

                return result;
            }

            string full = Path.Combine(instanceDir, path);
            if (Directory.Exists(full) || File.Exists(full))
            {
                result.Add(full);
            }

            return result;
        }

        /// <summary>列出「源→目标」文件对。</summary>
        private static List<(string Src, string Dst)> BuildOperations(string gameDir, string fromInstance, string toInstance, IEnumerable<Category> categories)
        {
            string source = Path.Combine(gameDir, "versions", fromInstance);
            string target = Path.Combine(gameDir, "versions", toInstance);
            var operations = new List<(string, string)>();

            foreach (var category in categories)
            {
                foreach (string path in category.Paths)
                {
                    foreach (string src in ResolvePaths(source, category, path))
                    {
                        if (Directory.Exists(src))
                        {
                            string dstRoot = Path.Combine(target, Path.GetFileName(src));
                            foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                            {
                                string rel = file.Substring(src.Length).TrimStart('\\');
                                operations.Add((file, Path.Combine(dstRoot, rel)));
                            }
                        }
                        else
                        {
                            operations.Add((src, Path.Combine(target, path)));
                        }
                    }
                }
            }

            return operations;
        }

        public static int CountConflicts(string gameDir, string fromInstance, string toInstance, IEnumerable<Category> categories)
        {
            var operations = BuildOperations(gameDir, fromInstance, toInstance, categories);
            return operations.Count(op => File.Exists(op.Dst));
        }

        public static long EstimateSize(string instanceDir, IEnumerable<Category> categories)
        {
            long total = 0;
            foreach (var category in categories)
            {
                foreach (string path in category.Paths)
                {
                    foreach (string full in ResolvePaths(instanceDir, category, path))
                    {
                        if (Directory.Exists(full))
                        {
                            try
                            {
                                total += Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                                    .Sum(f => new FileInfo(f).Length);
                            }
                            catch (Exception)
                            {
                            }
                        }
                        else if (File.Exists(full))
                        {
                            total += new FileInfo(full).Length;
                        }
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// 按类别复制。overwrite=false 且目标存在同名文件时拒绝（返回 false）。
        /// overwrite=true 时先备份目标文件到 rstc-backup/migrate_*，再覆盖。
        /// </summary>
        public static bool Run(string gameDir, string fromInstance, string toInstance, IEnumerable<Category> categories, bool overwrite, out string summary)
        {
            string source = Path.Combine(gameDir, "versions", fromInstance);
            string target = Path.Combine(gameDir, "versions", toInstance);
            var lines = new List<string>();

            if (!PackApplier.IsSafeInstanceName(fromInstance) || !PackApplier.IsSafeInstanceName(toInstance))
            {
                summary = "实例名不合法（不能含路径分隔符/盘符/保留名）: " + fromInstance + " -> " + toInstance;
                return false;
            }

            if (!Directory.Exists(source))
            {
                summary = "源实例不存在: " + source;
                return false;
            }

            if (!Directory.Exists(target))
            {
                summary = "目标实例不存在: " + target;
                return false;
            }

            List<(string Src, string Dst)> operations = BuildOperations(gameDir, fromInstance, toInstance, categories);
            var conflicts = operations.Where(op => File.Exists(op.Dst)).ToList();
            if (conflicts.Count > 0 && !overwrite)
            {
                summary = "目标已存在 " + conflicts.Count + " 个同名文件（例如 " +
                          string.Join(", ", conflicts.Take(3).Select(c => Path.GetFileName(c.Dst))) +
                          "）。为避免覆盖玩家数据已停止；确认覆盖请显式允许（菜单确认 / --overwrite）。";
                return false;
            }

            string backupRoot = null;
            if (conflicts.Count > 0)
            {
                backupRoot = Path.Combine(gameDir, "rstc-backup", "migrate_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                foreach (var (src, dst) in conflicts)
                {
                    string rel = dst.Substring(target.Length).TrimStart('\\');
                    string backup = Path.Combine(backupRoot, "files", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup) ?? Path.Combine(backupRoot, "files"));
                    File.Copy(dst, backup, true);
                }
            }

            int files = 0;
            long bytes = 0;
            var createdFiles = new List<string>();
            var createdDirs = new List<string>();
            try
            {
                foreach (var category in categories)
                {
                    int categoryFiles = 0;
                    foreach (string path in category.Paths)
                    {
                        foreach (string src in ResolvePaths(source, category, path))
                        {
                            if (Directory.Exists(src))
                            {
                                string dstRoot = Path.Combine(target, Path.GetFileName(src));
                                foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                                {
                                    string rel = file.Substring(src.Length).TrimStart('\\');
                                    string dst = Path.Combine(dstRoot, rel);
                                    string parent = Path.GetDirectoryName(dst) ?? dstRoot;
                                    if (!Directory.Exists(parent))
                                    {
                                        Directory.CreateDirectory(parent);
                                        createdDirs.Add(parent);
                                    }

                                    bool existed = File.Exists(dst);
                                    File.Copy(file, dst, true);
                                    if (!existed)
                                    {
                                        createdFiles.Add(dst);
                                    }

                                    categoryFiles++;
                                    bytes += new FileInfo(file).Length;
                                }
                            }
                            else if (File.Exists(src))
                            {
                                string dst = Path.Combine(target, path);
                                string parent = Path.GetDirectoryName(dst) ?? target;
                                if (!Directory.Exists(parent))
                                {
                                    Directory.CreateDirectory(parent);
                                    createdDirs.Add(parent);
                                }

                                bool existed = File.Exists(dst);
                                File.Copy(src, dst, true);
                                if (!existed)
                                {
                                    createdFiles.Add(dst);
                                }

                                categoryFiles++;
                                bytes += new FileInfo(src).Length;
                            }
                        }
                    }

                    files += categoryFiles;
                    lines.Add(category.Name + ": " + categoryFiles + " 个文件");
                }
            }
            catch (Exception ex)
            {
                int restored = RestoreFromBackup(backupRoot, target);
                int removed = RemoveCreated(createdFiles);
                RemoveEmptyDirs(createdDirs, target);
                summary = "迁移失败: " + ex.GetType().Name + ": " + ex.Message +
                          "；已回滚（恢复 " + restored + " 个被覆盖文件，删除 " + removed + " 个新建文件）";
                if (!string.IsNullOrEmpty(backupRoot) && Directory.Exists(backupRoot))
                {
                    summary += "；备份保留在 " + backupRoot;
                }

                return false;
            }

            summary = string.Join("\n", lines) + "\n总计: " + files + " 个文件 / " +
                      (bytes / 1048576.0).ToString("F1") + " MiB（源未改动";
            if (conflicts.Count > 0)
            {
                summary += "；覆盖 " + conflicts.Count + " 个目标文件，已备份到 " + backupRoot;
            }

            summary += "）";
            if (conflicts.Count > 0)
            {
                AnsiConsole.MarkupLine("[green]迁移备份[/] " + Markup.Escape(backupRoot));
            }

            return true;
        }

        /// <summary>迁移失败时从备份恢复被覆盖文件；返回恢复数量。</summary>
        private static int RestoreFromBackup(string backupRoot, string target)
        {
            if (string.IsNullOrEmpty(backupRoot) || !Directory.Exists(Path.Combine(backupRoot, "files")))
            {
                return 0;
            }

            int restored = 0;
            string filesRoot = Path.Combine(backupRoot, "files");
            foreach (string backupFile in Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories))
            {
                string rel = backupFile.Substring(filesRoot.Length).TrimStart('\\');
                string dst = Path.Combine(target, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? target);
                    if (File.Exists(dst))
                    {
                        File.SetAttributes(dst, FileAttributes.Normal);
                    }

                    File.Copy(backupFile, dst, true);
                    restored++;
                }
                catch (Exception)
                {
                }
            }

            return restored;
        }

        private static int RemoveCreated(List<string> createdFiles)
        {
            int removed = 0;
            foreach (string file in createdFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception)
                {
                }
            }

            return removed;
        }

        private static void RemoveEmptyDirs(List<string> createdDirs, string target)
        {
            string targetFull = Path.GetFullPath(target).TrimEnd('\\');
            foreach (string dir in createdDirs.OrderByDescending(d => d.Length))
            {
                string current = dir;
                while (current.Length > targetFull.Length &&
                       Directory.Exists(current) &&
                       !Directory.EnumerateFileSystemEntries(current).Any())
                {
                    try
                    {
                        Directory.Delete(current);
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
        }
    }
}
