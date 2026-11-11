using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>V1 白名单迁移：只搬玩家数据类别，不搬 mods/config（整合包自带）。</summary>
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

        /// <summary>扫描同目录下可作为迁移源的实例（有 options.txt 或 mods/ 或 RSTC_version.txt）。</summary>
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

        public static string ResolveCategoryPath(string instanceDir, Category category, string path)
        {
            string direct = Path.Combine(instanceDir, path);
            if (category.NeedsWildcard && path == "XaeroWaypoints")
            {
                string parent = Directory.Exists(instanceDir) ? instanceDir : null;
                if (parent != null)
                {
                    string match = Directory.GetDirectories(parent, "XaeroWaypoints*").FirstOrDefault();
                    if (match != null)
                    {
                        return match;
                    }
                }
            }

            return direct;
        }

        public static long EstimateSize(string instanceDir, IEnumerable<Category> categories)
        {
            long total = 0;
            foreach (var category in categories)
            {
                foreach (string path in category.Paths)
                {
                    string full = ResolveCategoryPath(instanceDir, category, path);
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

            return total;
        }

        /// <summary>按类别复制（覆盖同名文件），源不变；返回结果摘要。</summary>
        public static bool Run(string gameDir, string fromInstance, string toInstance, IEnumerable<Category> categories, out string summary)
        {
            string source = Path.Combine(gameDir, "versions", fromInstance);
            string target = Path.Combine(gameDir, "versions", toInstance);
            var lines = new List<string>();
            int files = 0;
            long bytes = 0;

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

            foreach (var category in categories)
            {
                int categoryFiles = 0;
                foreach (string path in category.Paths)
                {
                    string src = ResolveCategoryPath(source, category, path);
                    if (!Directory.Exists(src) && !File.Exists(src))
                    {
                        continue;
                    }

                    string rel = path;
                    string dst = Path.Combine(target, path);
                    if (Directory.Exists(src))
                    {
                        // 目录：递归复制
                        string dstDir = dst;
                        if (category.NeedsWildcard && path == "XaeroWaypoints")
                        {
                            dstDir = Path.Combine(target, Path.GetFileName(src));
                        }

                        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                        {
                            string relative = file.Substring(src.Length).TrimStart('\\');
                            string outFile = Path.Combine(dstDir, relative);
                            Directory.CreateDirectory(Path.GetDirectoryName(outFile) ?? dstDir);
                            File.Copy(file, outFile, true);
                            categoryFiles++;
                            bytes += new FileInfo(file).Length;
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? target);
                        File.Copy(src, dst, true);
                        categoryFiles++;
                        bytes += new FileInfo(src).Length;
                    }
                }

                files += categoryFiles;
                lines.Add(category.Name + ": " + categoryFiles + " 个文件");
            }

            summary = string.Join("\n", lines) + "\n总计: " + files + " 个文件 / " + (bytes / 1048576.0).ToString("F1") + " MiB（源未改动）";
            return true;
        }
    }
}
