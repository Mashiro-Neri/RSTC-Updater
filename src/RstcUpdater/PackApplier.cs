using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace RstcUpdater
{
    /// <summary>
    /// 阶段 0-4/0-5：把 mrpack（modrinth.index.json + overrides/）铺开进实例目录。
    /// 白名单镜像：只处理 overrides 顶层出现过的路径；支持删除清单、保留清单、备份、journal、回滚。
    /// </summary>
    internal static class PackApplier
    {
        private static readonly HashSet<string> NeverDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "saves", "screenshots", "logs", "crash-reports", "xaero", "journeymap",
            "Distant_Horizons_server_data", "_modpack_backup", "PCL", "rstc-backup",
            "hmclversion.cfg", "options.txt", "servers.dat", "modrinth.index.json",
            "RSTC_version.txt"
        };

        private static readonly string[] NeverDeletePrefixes = { "XaeroWaypoints", "update_log_" };

        // ==================== 安装/更新 ====================

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

            if (!IsSafeInstanceName(instanceName))
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

        public static bool IsSafeInstanceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
            {
                return false;
            }

            if (name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
            {
                return false;
            }

            if (name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                return false;
            }

            string stem = name.Split('.')[0].ToUpperInvariant();
            string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            return Array.IndexOf(reserved, stem) < 0;
        }

        /// <summary>把 zip 条目名规范成安全的相对路径；不合法则抛异常（防 zip-slip）。</summary>
        private static string SafeRelativePath(string entryName)
        {
            string name = entryName.Replace('\\', '/');
            if (name.Length == 0)
            {
                throw new InvalidDataException("空条目名");
            }

            if (name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidDataException("绝对路径条目: " + entryName);
            }

            if (name.Contains(":"))
            {
                throw new InvalidDataException("条目名含盘符/流: " + entryName);
            }

            string[] parts = name.Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..")
                {
                    throw new InvalidDataException("条目名含空段/相对段: " + entryName);
                }
            }

            return string.Join("/", parts);
        }

        private static string EnsureInside(string root, string rel)
        {
            string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
            string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("目标越界: " + rel);
            }

            return full;
        }

        private static int Apply(string gameDir, string zipPath, string instanceName, PackOptions options)
        {
            string instanceDir = Path.Combine(gameDir, "versions", instanceName);
            string instanceFull = Path.GetFullPath(instanceDir);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var packFiles = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                ZipArchiveEntry indexEntry = null;
                int pclSkipped = 0;
                int launcherSkipped = 0;

                foreach (var entry in zip.Entries)
                {
                    string raw = entry.FullName.Replace('\\', '/');
                    if (raw.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string name = SafeRelativePath(raw);
                    if (string.Equals(name, "modrinth.index.json", StringComparison.OrdinalIgnoreCase))
                    {
                        indexEntry = entry;
                        continue;
                    }

                    if (!name.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string rel = SafeRelativePath(name.Substring("overrides/".Length));
                    if (rel.IndexOf(':') >= 0)
                    {
                        throw new InvalidDataException("overrides 条目名含盘符: " + raw);
                    }

                    if (rel.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase))
                    {
                        pclSkipped++;
                        continue;
                    }

                    // 启动器配置属于本地状态，绝不由包覆盖
                    if (string.Equals(rel, "hmclversion.cfg", StringComparison.OrdinalIgnoreCase))
                    {
                        launcherSkipped++;
                        continue;
                    }

                    packFiles[rel] = entry;
                }

                if (indexEntry == null)
                {
                    throw new InvalidDataException("包内缺少 modrinth.index.json");
                }

                string indexJson = ReadEntry(indexEntry);
                string versionId = "unknown";
                using (var doc = JsonDocument.Parse(indexJson))
                {
                    if (doc.RootElement.TryGetProperty("versionId", out var v) && v.GetString() != null)
                    {
                        versionId = v.GetString();
                    }
                }

                string tag = options.Tag;
                if (string.IsNullOrEmpty(tag))
                {
                    string existingTag = Path.Combine(instanceDir, "RSTC_version.txt");
                    if (File.Exists(existingTag))
                    {
                        string current = File.ReadAllText(existingTag, Encoding.UTF8).Trim();
                        if (current.Length > 0)
                        {
                            tag = current;
                        }
                    }
                }

                if (string.IsNullOrEmpty(tag))
                {
                    tag = versionId;
                }

                // ---- 计划 ----
                var writes = new List<string>();
                var overwrites = new List<string>();
                var newFiles = new List<string>();
                var backups = new List<string>();
                var deletes = new List<string>();
                var keptByUser = new List<string>();
                var conflicts = new List<string>();
                int reused = 0;

                foreach (string rel in packFiles.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                {
                    string dest = EnsureInside(instanceFull, rel);
                    if (File.Exists(dest))
                    {
                        bool same = SameContent(dest, packFiles[rel]);
                        string top = TopSegment(rel);
                        if (IsOptionsFile(rel))
                        {
                            string mode = string.Equals(top, "options.txt", StringComparison.OrdinalIgnoreCase)
                                ? options.OptionsMode
                                : options.ServersMode;
                            if (same)
                            {
                                reused++;
                                continue;
                            }

                            if (string.Equals(mode, "keep", StringComparison.OrdinalIgnoreCase))
                            {
                                conflicts.Add(rel);
                                continue;
                            }
                        }
                        else if (same)
                        {
                            reused++;
                            continue;
                        }

                        overwrites.Add(rel);
                        backups.Add(rel);
                    }
                    else
                    {
                        newFiles.Add(rel);
                    }

                    writes.Add(rel);
                }

                if (options.Update && Directory.Exists(instanceFull))
                {
                    var packedSet = new HashSet<string>(packFiles.Keys, StringComparer.OrdinalIgnoreCase);
                    var topLevels = new HashSet<string>(packFiles.Keys.Select(TopSegment), StringComparer.OrdinalIgnoreCase);
                    foreach (string top in topLevels)
                    {
                        if (IsOptionsFile(top) || NeverDelete.Contains(top))
                        {
                            continue;
                        }

                        string dir = EnsureInside(instanceFull, top);
                        if (!Directory.Exists(dir))
                        {
                            continue;
                        }

                        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            string rel = file.Substring(instanceFull.Length + 1).Replace('\\', '/');
                            if (packedSet.Contains(rel) || IsNeverDelete(rel))
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
                table.AddRow("启动器配置跳过", launcherSkipped.ToString());
                table.AddRow("写入", writes.Count.ToString() + "（覆盖 " + overwrites.Count + "，新建 " + newFiles.Count + "）");
                table.AddRow("复用（内容一致不重写）", reused.ToString());
                table.AddRow("删除", deletes.Count.ToString() + "（更新模式）");
                table.AddRow("玩家保留", keptByUser.Count.ToString());
                table.AddRow("options/servers 冲突（保留本地）", conflicts.Count.ToString());
                table.AddRow("版本", versionId + "（tag " + tag + "）");
                AnsiConsole.Write(table);

                foreach (string c in conflicts)
                {
                    AnsiConsole.MarkupLine("[yellow]保留本地:[/] " + Markup.Escape(c));
                }

                if (options.DryRun)
                {
                    foreach (string d in deletes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("DELETE " + d);
                    }

                    foreach (string w in writes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("WRITE  " + w);
                    }

                    AnsiConsole.MarkupLine("[yellow]DRY-RUN[/] 未做任何修改");
                    return 0;
                }

                Directory.CreateDirectory(instanceFull);

                string backupRoot = Path.Combine(gameDir, "rstc-backup", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                string backupFiles = Path.Combine(backupRoot, "files");
                string journalPath = Path.Combine(instanceFull, ".rstc-journal.json");

                var journal = new JsonObject
                {
                    ["versionId"] = versionId,
                    ["tag"] = tag,
                    ["zip"] = zipPath,
                    ["instance"] = instanceName,
                    ["backupPath"] = backupRoot,
                    ["status"] = "writing",
                    ["startedAt"] = DateTime.Now.ToString("o"),
                    ["writes"] = ToJsonArray(writes),
                    ["overwrites"] = ToJsonArray(overwrites),
                    ["newFiles"] = ToJsonArray(newFiles),
                    ["deletes"] = ToJsonArray(deletes)
                };
                File.WriteAllText(journalPath, journal.ToJsonString(), new UTF8Encoding(false));

                // 备份（覆盖 + 删除）
                int backedUp = 0;
                foreach (string rel in backups.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string src = EnsureInside(instanceFull, rel);
                    if (!File.Exists(src))
                    {
                        continue;
                    }

                    string dst = Path.Combine(backupFiles, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? backupFiles);
                    File.Copy(src, dst, true);
                    backedUp++;
                }

                AnsiConsole.MarkupLine("[green]备份[/] " + Markup.Escape(backupRoot) + "（" + backedUp + " 个文件）");
                if (backedUp == 0)
                {
                    AnsiConsole.MarkupLine("[grey]本次无需备份（无覆盖/删除）[/]");
                }

                // 写入（测试钩子：环境变量控制写入 N 个后抛中断，用于验证 journal/回滚）
                int failAfter = GetTestFailAfter();
                int written = 0;
                foreach (string rel in writes)
                {
                    if (failAfter > 0 && written >= failAfter)
                    {
                        throw new IOException("测试注入：写入 " + written + " 个后中断");
                    }

                    string dest = EnsureInside(instanceFull, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? instanceFull);
                    using (var input = packFiles[rel].Open())
                    using (var output = File.Create(dest))
                    {
                        input.CopyTo(output);
                    }

                    written++;
                }

                foreach (string rel in deletes)
                {
                    string dest = EnsureInside(instanceFull, rel);
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }
                }

                // 测试钩子：删除阶段完成后中断（用于验证 rollback 对被删除文件的恢复）
                if (GetTestFailAfterDeletes() > 0)
                {
                    throw new IOException("测试注入：删除阶段后中断");
                }

                File.WriteAllText(Path.Combine(instanceFull, "modrinth.index.json"), indexJson, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(instanceFull, "RSTC_version.txt"), tag, new UTF8Encoding(false));

                journal["status"] = "done";
                journal["finishedAt"] = DateTime.Now.ToString("o");
                File.WriteAllText(journalPath, journal.ToJsonString(), new UTF8Encoding(false));

                int missing = writes.Count(rel => !File.Exists(EnsureInside(instanceFull, rel)));
                int notDeleted = deletes.Count(rel => File.Exists(EnsureInside(instanceFull, rel)));
                int mods = Directory.Exists(Path.Combine(instanceFull, "mods"))
                    ? Directory.EnumerateFiles(Path.Combine(instanceFull, "mods"), "*.jar").Count()
                    : 0;

                var result = new Table().Border(TableBorder.Rounded);
                result.AddColumn("检查");
                result.AddColumn("结果");
                result.AddRow("写入缺失", missing.ToString());
                result.AddRow("删除残留", notDeleted.ToString());
                result.AddRow("mods/*.jar", mods.ToString());
                result.AddRow("备份目录", backupRoot);
                AnsiConsole.Write(result);

                if (missing == 0 && notDeleted == 0)
                {
                    TrimBackups(gameDir);
                }

                if (missing != 0 || notDeleted != 0)
                {
                    Console.Error.WriteLine("PACK_VERIFY=FAIL");
                    return 8;
                }

                AnsiConsole.MarkupLine("[green]PACK_APPLY=PASS[/]");
                return 0;
            }
        }

        private static int GetTestFailAfterDeletes()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RSTC_STAGE0_TEST"), "1", StringComparison.Ordinal))
            {
                return 0;
            }

            return string.Equals(Environment.GetEnvironmentVariable("RSTC_STAGE0_FAIL_AFTER_DELETES"), "1", StringComparison.Ordinal) ? 1 : 0;
        }

        private static int GetTestFailAfter()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RSTC_STAGE0_TEST"), "1", StringComparison.Ordinal))
            {
                return 0;
            }

            string raw = Environment.GetEnvironmentVariable("RSTC_STAGE0_FAIL_AFTER");
            return int.TryParse(raw, out int n) ? n : 0;
        }

        private static JsonArray ToJsonArray(IEnumerable<string> values)
        {
            var array = new JsonArray();
            foreach (string v in values)
            {
                array.Add(v);
            }

            return array;
        }

        private static string ReadEntry(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // ==================== 回滚 ====================

        public static int Rollback(string gameDir, string instanceName)
        {
            if (!IsSafeInstanceName(instanceName))
            {
                Console.Error.WriteLine("实例名不合法: " + instanceName);
                return 2;
            }

            string instanceDir = Path.GetFullPath(Path.Combine(gameDir, "versions", instanceName));
            string journalPath = Path.Combine(instanceDir, ".rstc-journal.json");
            if (!File.Exists(journalPath))
            {
                Console.Error.WriteLine("没有 journal，无法回滚: " + journalPath);
                return 2;
            }

            JsonObject journal;
            try
            {
                journal = JsonNode.Parse(File.ReadAllText(journalPath, Encoding.UTF8)) as JsonObject
                          ?? throw new FormatException("journal 不是对象");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("journal 解析失败: " + ex.Message);
                return 7;
            }

            string status = journal["status"]?.GetValue<string>() ?? "";
            if (string.Equals(status, "rolledback", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("已经回滚过");
                return 2;
            }

            string backupRoot = journal["backupPath"]?.GetValue<string>();
            var overwrites = journal["overwrites"]?.AsArray().Select(x => x.GetValue<string>()).ToList() ?? new List<string>();
            var newFiles = journal["newFiles"]?.AsArray().Select(x => x.GetValue<string>()).ToList() ?? new List<string>();
            var deletes = journal["deletes"]?.AsArray().Select(x => x.GetValue<string>()).ToList() ?? new List<string>();

            // fail-closed：先确认所有需要恢复的备份都在，缺一个就拒绝回滚且不改 journal
            var needingRestore = overwrites.Concat(deletes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var missingBackups = new List<string>();
            if (string.IsNullOrEmpty(backupRoot))
            {
                missingBackups.Add("(journal 未记录 backupPath)");
            }
            else
            {
                foreach (string rel in needingRestore)
                {
                    string src = Path.Combine(backupRoot, "files", rel.Replace('/', '\\'));
                    if (!File.Exists(src))
                    {
                        missingBackups.Add(rel + " -> " + src);
                    }
                }
            }

            if (missingBackups.Count > 0)
            {
                Console.Error.WriteLine("备份缺失，拒绝回滚（journal 保持 " + status + "，可人工恢复）:");
                foreach (string m in missingBackups.Take(20))
                {
                    Console.Error.WriteLine("  " + m);
                }

                return 7;
            }

            try
            {
                int restored = 0;
                foreach (string rel in needingRestore)
                {
                    string src = Path.Combine(backupRoot, "files", rel.Replace('/', '\\'));
                    string dest = EnsureInside(instanceDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? instanceDir);
                    File.Copy(src, dest, true);
                    restored++;
                }

                int removed = 0;
                foreach (string rel in newFiles)
                {
                    string dest = EnsureInside(instanceDir, rel);
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                        removed++;
                    }
                }

                journal["status"] = "rolledback";
                journal["rolledBackAt"] = DateTime.Now.ToString("o");
                File.WriteAllText(journalPath, journal.ToJsonString(), new UTF8Encoding(false));

                AnsiConsole.MarkupLine("[green]ROLLBACK=PASS[/] 恢复 " + restored + " 个文件，删除新建 " + removed + " 个");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("回滚失败（journal 未标记 rolledback）: " + ex.GetType().Name + ": " + ex.Message);
                return 7;
            }
        }

        // ==================== 工具函数 ====================

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

            if (string.Equals(Path.GetFileName(rel), "DistantHorizons.toml", StringComparison.OrdinalIgnoreCase))
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

        private static bool SameContent(string path, ZipArchiveEntry entry)
        {
            var local = new FileInfo(path);
            if (local.Length != entry.Length)
            {
                return false;
            }

            using (var stream = entry.Open())
            {
                string remote = ToHex(SHA256.Create().ComputeHash(stream));
                using (var localStream = File.OpenRead(path))
                {
                    string actual = ToHex(SHA256.Create().ComputeHash(localStream));
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

            // 失败/进行中的 journal 指向的备份不能删，否则无法回滚
            var protectedBackups = GetProtectedBackups(gameDir);
            var dirs = Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            int kept = 0;
            foreach (string dir in dirs)
            {
                if (protectedBackups.Contains(Path.GetFullPath(dir)))
                {
                    continue;
                }

                if (kept < 3)
                {
                    kept++;
                    continue;
                }

                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static HashSet<string> GetProtectedBackups(string gameDir)
        {
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string versions = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versions))
            {
                return protectedPaths;
            }

            foreach (string journalPath in Directory.EnumerateFiles(versions, ".rstc-journal.json", SearchOption.AllDirectories))
            {
                try
                {
                    var journal = JsonNode.Parse(File.ReadAllText(journalPath, Encoding.UTF8)) as JsonObject;
                    string status = journal?["status"]?.GetValue<string>() ?? "";
                    if (string.Equals(status, "writing", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        string backupPath = journal?["backupPath"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(backupPath))
                        {
                            protectedPaths.Add(Path.GetFullPath(backupPath));
                        }
                    }
                }
                catch (Exception)
                {
                    // journal 损坏时不冒险删备份
                    string backupPath = null;
                    try
                    {
                        var journal = JsonNode.Parse(File.ReadAllText(journalPath, Encoding.UTF8)) as JsonObject;
                        backupPath = journal?["backupPath"]?.GetValue<string>();
                    }
                    catch (Exception)
                    {
                    }

                    if (!string.IsNullOrEmpty(backupPath))
                    {
                        protectedPaths.Add(Path.GetFullPath(backupPath));
                    }
                }
            }

            return protectedPaths;
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
            public string Tag;
        }
    }
}
