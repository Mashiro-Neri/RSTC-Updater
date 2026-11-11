using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// 阶段 0-4/0-5：把 mrpack（modrinth.index.json + overrides/）铺开进实例目录。
    /// 只做白名单镜像：只处理 overrides 顶层出现过的路径；支持删除清单、保留清单、备份、journal 与回滚。
    /// </summary>
    internal static class PackApplier
    {
        private static readonly HashSet<string> NeverDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "saves", "screenshots", "logs", "crash-reports", "xaero", "journeymap",
            "Distant_Horizons_server_data", "_modpack_backup", "PCL", "rstc-backup",
            "hmclversion.cfg", "options.txt", "servers.dat", "modrinth.index.json",
            "RSTC_version.txt", "hmclversion.cfg"
        };

        private static readonly string[] NeverDeletePrefixes = { "XaeroWaypoints", "update_log_" };

        public static int Run(PackOptions options)
        {
            string gameDir = Path.GetFullPath(options.GameDir);
            string zipPath = Path.GetFullPath(options.ZipPath);
            string instanceName = options.Instance;

            if (!File.Exists(zipPath))
            {
                Console.Error.WriteLine("整合包不存在: " + zipPath);
                return 2;
            }

            if (string.IsNullOrWhiteSpace(instanceName) || instanceName.IndexOfAny(new[] { '\\', '/', ':', '*' }) >= 0)
            {
                Console.Error.WriteLine("实例名不合法: " + instanceName);
                return 2;
            }

            if (!Directory.Exists(gameDir))
            {
                Console.Error.WriteLine("游戏目录不存在: " + gameDir);
                return 2;
            }

            try
            {
                return Apply(gameDir, zipPath, instanceName, options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("应用失败: " + ex.GetType().Name + ": " + ex.Message);
                return 7;
            }
        }

        private static int Apply(string gameDir, string zipPath, string instanceName, PackOptions options)
        {
            string instanceDir = Path.Combine(gameDir, "versions", instanceName);

            using (var zip = ZipFile.OpenRead(zipPath))
            using (var indexStream = new MemoryStream())
            {
                var packFiles = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                ZipArchiveEntry indexEntry = null;
                int pclSkipped = 0;

                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains("../") || name.Contains("/.."))
                    {
                        throw new InvalidDataException("ZIP 条目越界: " + name);
                    }

                    if (string.Equals(name, "modrinth.index.json", StringComparison.OrdinalIgnoreCase))
                    {
                        indexEntry = entry;
                        continue;
                    }

                    if (!name.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string rel = name.Substring("overrides/".Length);
                    if (rel.Length == 0)
                    {
                        continue;
                    }

                    // 用户要求：发布包不携带 PCL/；即使出现也一律跳过，不写入、不删除玩家本地 PCL/
                    if (rel.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase))
                    {
                        pclSkipped++;
                        continue;
                    }

                    packFiles[rel] = entry;
                }

                if (indexEntry == null)
                {
                    throw new InvalidDataException("包内缺少 modrinth.index.json");
                }

                indexEntry.Open().CopyTo(indexStream);
                indexStream.Position = 0;
                string indexJson;
                using (var reader = new StreamReader(indexStream, Encoding.UTF8))
                {
                    indexJson = reader.ReadToEnd();
                }

                string versionId;
                using (var doc = System.Text.Json.JsonDocument.Parse(indexJson))
                {
                    versionId = doc.RootElement.TryGetProperty("versionId", out var v) ? v.GetString() : null;
                    if (string.IsNullOrEmpty(versionId))
                    {
                        versionId = "unknown";
                    }
                }

                // ---- 计划 ----
                var writes = new List<string>();
                var overwrites = new List<string>();
                var backups = new List<string>(); // 相对路径，需要备份（覆盖 + 删除）
                var deletes = new List<string>();
                var keptByUser = new List<string>();
                var conflicts = new List<string>();

                foreach (var rel in packFiles.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                {
                    string dest = Path.Combine(instanceDir, rel.Replace('/', '\\'));
                    if (File.Exists(dest))
                    {
                        string top = TopSegment(rel);
                        if (IsOptionsFile(rel))
                        {
                            string mode = string.Equals(top, "options.txt", StringComparison.OrdinalIgnoreCase)
                                ? options.OptionsMode
                                : options.ServersMode;
                            if (string.Equals(mode, "keep", StringComparison.OrdinalIgnoreCase) &&
                                !SameFile(dest, packFiles[rel]))
                            {
                                conflicts.Add(rel);
                                continue;
                            }
                        }

                        overwrites.Add(rel);
                        backups.Add(rel);
                    }

                    writes.Add(rel);
                }

                if (options.Update && Directory.Exists(instanceDir))
                {
                    var packedSet = new HashSet<string>(writes, StringComparer.OrdinalIgnoreCase);
                    var topLevels = new HashSet<string>(writes.Select(TopSegment), StringComparer.OrdinalIgnoreCase);
                    foreach (string top in topLevels)
                    {
                        if (IsOptionsFile(top) || NeverDelete.Contains(top))
                        {
                            continue;
                        }

                        string dir = Path.Combine(instanceDir, top);
                        if (!Directory.Exists(dir))
                        {
                            continue;
                        }

                        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            string rel = file.Substring(instanceDir.Length + 1).Replace('\\', '/');
                            if (packedSet.Contains(rel))
                            {
                                continue;
                            }

                            if (IsNeverDelete(rel))
                            {
                                continue;
                            }

                            if (IsKept(options.Keep, rel))
                            {
                                keptByUser.Add(rel);
                                continue;
                            }

                            deletes.Add(rel);
                            backups.Add(rel);
                        }
                    }
                }

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("项");
                table.AddColumn("数量");
                table.AddRow("包内文件（overrides）", packFiles.Count.ToString());
                table.AddRow("PCL/ 跳过（用户要求）", pclSkipped.ToString());
                table.AddRow("写入", writes.Count.ToString() + "（其中覆盖 " + overwrites.Count + "）");
                table.AddRow("删除", deletes.Count.ToString() + "（更新模式）");
                table.AddRow("玩家保留", keptByUser.Count.ToString());
                table.AddRow("options/servers 冲突（保留本地）", conflicts.Count.ToString());
                table.AddRow("版本", versionId);
                AnsiConsole.Write(table);

                foreach (string c in conflicts)
                {
                    AnsiConsole.MarkupLine("[yellow]保留本地:[/] " + Markup.Escape(c));
                }

                if (options.DryRun)
                {
                    foreach (string d in deletes.OrderBy(s => s))
                    {
                        Console.WriteLine("DELETE " + d);
                    }

                    foreach (string w in writes.OrderBy(s => s))
                    {
                        Console.WriteLine("WRITE  " + w);
                    }

                    AnsiConsole.MarkupLine("[yellow]DRY-RUN[/] 未做任何修改");
                    return 0;
                }

                Directory.CreateDirectory(instanceDir);

                // ---- 备份 + journal ----
                string backupRoot = Path.Combine(gameDir, "rstc-backup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                string backupFiles = Path.Combine(backupRoot, "files");
                var journalPath = Path.Combine(instanceDir, ".rstc-journal.json");
                var journal = new JsonObjectBuilder();
                journal.Add("versionId", versionId);
                journal.Add("zip", zipPath);
                journal.Add("startedAt", DateTime.Now.ToString("o"));
                journal.Add("writes", writes);
                journal.Add("deletes", deletes);
                File.WriteAllText(journalPath, journal.Build(), new UTF8Encoding(false));

                foreach (string rel in backups.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string src = Path.Combine(instanceDir, rel.Replace('/', '\\'));
                    if (!File.Exists(src))
                    {
                        continue;
                    }

                    string dst = Path.Combine(backupFiles, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? backupFiles);
                    File.Copy(src, dst, true);
                }

                AnsiConsole.MarkupLine("[green]备份[/] " + Markup.Escape(backupRoot));

                // ---- 写入 ----
                foreach (string rel in writes)
                {
                    string dest = Path.Combine(instanceDir, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? instanceDir);
                    using (var input = packFiles[rel].Open())
                    using (var output = File.Create(dest))
                    {
                        input.CopyTo(output);
                    }
                }

                // ---- 删除 ----
                foreach (string rel in deletes)
                {
                    string dest = Path.Combine(instanceDir, rel.Replace('/', '\\'));
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }
                }

                // ---- 本地状态 ----
                File.WriteAllText(Path.Combine(instanceDir, "modrinth.index.json"), indexJson, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(instanceDir, "RSTC_version.txt"), versionId, new UTF8Encoding(false));

                journal.Add("finishedAt", DateTime.Now.ToString("o"));
                journal.Add("status", "done");
                File.WriteAllText(journalPath, journal.Build(), new UTF8Encoding(false));

                // ---- 校验 ----
                int missing = writes.Count(rel => !File.Exists(Path.Combine(instanceDir, rel.Replace('/', '\\'))));
                int notDeleted = deletes.Count(rel => File.Exists(Path.Combine(instanceDir, rel.Replace('/', '\\'))));
                int mods = Directory.Exists(Path.Combine(instanceDir, "mods"))
                    ? Directory.EnumerateFiles(Path.Combine(instanceDir, "mods"), "*.jar").Count()
                    : 0;

                var result = new Table().Border(TableBorder.Rounded);
                result.AddColumn("检查");
                result.AddColumn("结果");
                result.AddRow("写入缺失", missing.ToString());
                result.AddRow("删除残留", notDeleted.ToString());
                result.AddRow("mods/*.jar", mods.ToString());
                result.AddRow("备份目录", backupRoot);
                AnsiConsole.Write(result);

                TrimBackups(gameDir);

                if (missing != 0 || notDeleted != 0)
                {
                    Console.Error.WriteLine("PACK_VERIFY=FAIL");
                    return 8;
                }

                AnsiConsole.MarkupLine("[green]PACK_APPLY=PASS[/]");
                return 0;
            }
        }

        private static bool IsOptionsFile(string rel)
        {
            string top = TopSegment(rel);
            return string.Equals(top, "options.txt", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(top, "servers.dat", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNeverDelete(string rel)
        {
            if (NeverDelete.Contains(TopSegment(rel)))
            {
                return true;
            }

            return NeverDeletePrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsKept(List<string> keep, string rel)
        {
            if (keep == null || keep.Count == 0)
            {
                return false;
            }

            return keep.Any(k =>
                string.Equals(rel, k, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith(k.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static string TopSegment(string rel)
        {
            int idx = rel.IndexOf('/');
            return idx < 0 ? rel : rel.Substring(0, idx);
        }

        private static bool SameFile(string path, ZipArchiveEntry entry)
        {
            var local = new FileInfo(path);
            if (local.Length != entry.Length)
            {
                return false;
            }

            using (var stream = entry.Open())
            {
                string remote = ToHex(SHA1.Create().ComputeHash(stream));
                using (var localStream = File.OpenRead(path))
                {
                    string actual = ToHex(SHA1.Create().ComputeHash(localStream));
                    return string.Equals(remote, actual, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static void TrimBackups(string gameDir)
        {
            string root = Path.Combine(gameDir, "rstc-backup");
            if (!Directory.Exists(root))
            {
                return;
            }

            var dirs = Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 3; i < dirs.Count; i++)
            {
                try
                {
                    Directory.Delete(dirs[i], true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private sealed class JsonObjectBuilder
        {
            private readonly List<string> _pairs = new List<string>();

            public void Add(string key, string value)
            {
                _pairs.Add(JsonString(key) + ":" + JsonString(value));
            }

            public void Add(string key, IEnumerable<string> values)
            {
                _pairs.Add(JsonString(key) + ":[" + string.Join(",", values.Select(JsonString)) + "]");
            }

            public string Build()
            {
                return "{" + string.Join(",", _pairs) + "}";
            }

            private static string JsonString(string s)
            {
                var sb = new StringBuilder("\"");
                foreach (char c in s ?? "")
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default: sb.Append(c); break;
                    }
                }

                return sb.Append('"').ToString();
            }
        }

        internal sealed class PackOptions
        {
            public string GameDir;
            public string ZipPath;
            public string Instance;
            public bool Update;
            public List<string> Keep = new List<string>();
            public string OptionsMode = "keep";
            public string ServersMode = "keep";
            public bool DryRun;
        }
    }
}
