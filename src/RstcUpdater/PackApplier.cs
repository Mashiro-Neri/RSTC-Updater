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
    internal sealed class PackPlan
    {
        public string VersionId;
        public string Tag;
        public string IndexJson;
        public int PclSkipped;
        public int LauncherSkipped;
        public int Reused;
        public List<string> Writes = new List<string>();
        public List<string> Overwrites = new List<string>();
        public List<string> NewFiles = new List<string>();
        public List<string> Deletes = new List<string>();
        public List<string> KeptByUser = new List<string>();
        public List<string> Conflicts = new List<string>();
        public Dictionary<string, ZipArchiveEntry> PackFiles = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// mrpack 应用/更新：白名单镜像 + 删除清单 + 保留清单 + options 冲突 + 备份 + journal + 回滚。
    /// 计划（PackPlan）可先预览再应用。
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

        // ==================== 元数据 / 计划 ====================

        internal sealed class PackMetadata
        {
            public string VersionId;
            public string Minecraft;
        }

        public static PackMetadata ReadMetadata(string zipPath)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entry = zip.GetEntry("modrinth.index.json");
                    if (entry == null)
                    {
                        return null;
                    }

                    string json;
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        json = reader.ReadToEnd();
                    }

                    using (var doc = JsonDocument.Parse(json))
                    {
                        var meta = new PackMetadata();
                        if (doc.RootElement.TryGetProperty("versionId", out var v))
                        {
                            meta.VersionId = v.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("dependencies", out var deps) &&
                            deps.TryGetProperty("minecraft", out var mc))
                        {
                            meta.Minecraft = mc.GetString();
                        }

                        return meta;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>只计算计划，不做任何写入（zip 由调用方负责 Dispose；抛错时本方法负责释放）。</summary>
        internal static PackPlan BuildPlan(string gameDir, string zipPath, string instanceName, PackOptions options, out ZipArchive zip)
        {
            ZipArchive opened = null;
            try
            {
                PackPlan plan = BuildPlanCore(gameDir, zipPath, instanceName, options, out opened);
                zip = opened;
                return plan;
            }
            catch (Exception)
            {
                if (opened != null)
                {
                    opened.Dispose();
                }

                throw;
            }
        }

        private static PackPlan BuildPlanCore(string gameDir, string zipPath, string instanceName, PackOptions options, out ZipArchive zip)
        {
            string instanceDir = Path.Combine(gameDir, "versions", instanceName);
            string instanceFull = Path.GetFullPath(instanceDir);
            zip = ZipFile.OpenRead(zipPath);
            var plan = new PackPlan();

            ZipArchiveEntry indexEntry = null;
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
                if (rel.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase))
                {
                    plan.PclSkipped++;
                    continue;
                }

                if (string.Equals(rel, "hmclversion.cfg", StringComparison.OrdinalIgnoreCase))
                {
                    plan.LauncherSkipped++;
                    continue;
                }

                plan.PackFiles[rel] = entry;
            }

            if (indexEntry == null)
            {
                throw new InvalidDataException("包内缺少 modrinth.index.json");
            }

            plan.IndexJson = ReadEntry(indexEntry);
            string versionId = "unknown";
            using (var doc = JsonDocument.Parse(plan.IndexJson))
            {
                if (doc.RootElement.TryGetProperty("versionId", out var v) && v.GetString() != null)
                {
                    versionId = v.GetString();
                }
            }

            plan.VersionId = versionId;
            plan.Tag = ResolveTag(options.Tag, instanceDir, versionId);

            foreach (string rel in plan.PackFiles.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                string dest = EnsureInside(instanceFull, rel);
                if (File.Exists(dest))
                {
                    var entry = plan.PackFiles[rel];
                    bool same = SameContent(dest, entry);
                    if (IsOptionsFile(rel))
                    {
                        string top = TopSegment(rel);
                        string mode = string.Equals(top, "options.txt", StringComparison.OrdinalIgnoreCase)
                            ? options.OptionsMode
                            : options.ServersMode;
                        if (same)
                        {
                            plan.Reused++;
                            continue;
                        }

                        if (string.Equals(mode, "keep", StringComparison.OrdinalIgnoreCase))
                        {
                            plan.Conflicts.Add(rel);
                            continue;
                        }
                    }
                    else if (same)
                    {
                        plan.Reused++;
                        continue;
                    }

                    plan.Overwrites.Add(rel);
                }
                else
                {
                    plan.NewFiles.Add(rel);
                }

                plan.Writes.Add(rel);
            }

            if (options.Update && Directory.Exists(instanceFull))
            {
                var packedSet = new HashSet<string>(plan.PackFiles.Keys, StringComparer.OrdinalIgnoreCase);
                var topLevels = new HashSet<string>(plan.PackFiles.Keys.Select(TopSegment), StringComparer.OrdinalIgnoreCase);
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
                            plan.KeptByUser.Add(rel);
                            continue;
                        }

                        plan.Deletes.Add(rel);
                    }
                }
            }

            return plan;
        }

        /// <summary>预览：不写入，返回计划（zip 已释放；异常时 BuildPlan 已释放）。</summary>
        public static PackPlan Preview(string gameDir, string zipPath, string instanceName, PackOptions options)
        {
            ZipArchive zip = null;
            try
            {
                return BuildPlan(gameDir, zipPath, instanceName, options, out zip);
            }
            finally
            {
                if (zip != null)
                {
                    zip.Dispose();
                }
            }
        }

        // ==================== 应用 ====================

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

        private static int Apply(string gameDir, string zipPath, string instanceName, PackOptions options)
        {
            string instanceDir = Path.Combine(gameDir, "versions", instanceName);
            string instanceFull = Path.GetFullPath(instanceDir);

            ZipArchive zip;
            PackPlan plan = BuildPlan(gameDir, zipPath, instanceName, options, out zip);
            using (zip)
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("项");
                table.AddColumn("数量");
                table.AddRow("包内文件（overrides）", plan.PackFiles.Count.ToString());
                table.AddRow("PCL/ 跳过（用户要求）", plan.PclSkipped.ToString());
                table.AddRow("启动器配置跳过", plan.LauncherSkipped.ToString());
                table.AddRow("写入", plan.Writes.Count.ToString() + "（覆盖 " + plan.Overwrites.Count + "，新建 " + plan.NewFiles.Count + "）");
                table.AddRow("复用（内容一致不重写）", plan.Reused.ToString());
                table.AddRow("删除", plan.Deletes.Count.ToString() + "（更新模式）");
                table.AddRow("玩家保留", plan.KeptByUser.Count.ToString());
                table.AddRow("options/servers 冲突（保留本地）", plan.Conflicts.Count.ToString());
                table.AddRow("版本", plan.VersionId + "（tag " + plan.Tag + "）");
                AnsiConsole.Write(table);

                foreach (string c in plan.Conflicts)
                {
                    AnsiConsole.MarkupLine("[yellow]保留本地:[/] " + Markup.Escape(c));
                }

                if (options.DryRun)
                {
                    foreach (string d in plan.Deletes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("DELETE " + d);
                    }

                    foreach (string w in plan.Writes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
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

                var backups = plan.Overwrites.Concat(plan.Deletes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var journal = new JsonObject
                {
                    ["versionId"] = plan.VersionId,
                    ["tag"] = plan.Tag,
                    ["zip"] = zipPath,
                    ["instance"] = instanceName,
                    ["backupPath"] = backupRoot,
                    ["status"] = "writing",
                    ["startedAt"] = DateTime.Now.ToString("o"),
                    ["writes"] = ToJsonArray(plan.Writes),
                    ["overwrites"] = ToJsonArray(plan.Overwrites),
                    ["newFiles"] = ToJsonArray(plan.NewFiles),
                    ["deletes"] = ToJsonArray(plan.Deletes)
                };
                File.WriteAllText(journalPath, journal.ToJsonString(), new UTF8Encoding(false));

                int backedUp = 0;
                foreach (string rel in backups)
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

                if (backedUp > 0)
                {
                    AnsiConsole.MarkupLine("[green]备份[/] " + Markup.Escape(backupRoot) + "（" + backedUp + " 个文件）");
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]本次无需备份（无覆盖/删除）[/]");
                }

                int failAfter = GetTestFailAfter();
                int written = 0;
                foreach (string rel in plan.Writes)
                {
                    if (failAfter > 0 && written >= failAfter)
                    {
                        throw new IOException("测试注入：写入 " + written + " 个后中断");
                    }

                    string dest = EnsureInside(instanceFull, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? instanceFull);
                    using (var input = plan.PackFiles[rel].Open())
                    using (var output = File.Create(dest))
                    {
                        input.CopyTo(output);
                    }

                    written++;
                }

                foreach (string rel in plan.Deletes)
                {
                    string dest = EnsureInside(instanceFull, rel);
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }
                }

                if (GetTestFailAfterDeletes() > 0)
                {
                    throw new IOException("测试注入：删除阶段后中断");
                }

                File.WriteAllText(Path.Combine(instanceFull, "modrinth.index.json"), plan.IndexJson, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(instanceFull, "RSTC_version.txt"), plan.Tag, new UTF8Encoding(false));

                // 修复 PCL2 首次启动写入的大写语言码（如 zh_CN → zh_cn），26.3 只认小写
                NormalizeLanguage(instanceFull);

                journal["status"] = "done";
                journal["finishedAt"] = DateTime.Now.ToString("o");
                File.WriteAllText(journalPath, journal.ToJsonString(), new UTF8Encoding(false));

                int missing = plan.Writes.Count(rel => !File.Exists(EnsureInside(instanceFull, rel)));
                int notDeleted = plan.Deletes.Count(rel => File.Exists(EnsureInside(instanceFull, rel)));
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

            var needingRestore = overwrites.Concat(deletes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

        /// <summary>把 options.txt 里的语言码规范为小写（Minecraft 语言码约定为小写）。</summary>
        private static void NormalizeLanguage(string instanceDir)
        {
            string optionsPath = Path.Combine(instanceDir, "options.txt");
            if (!File.Exists(optionsPath))
            {
                return;
            }

            try
            {
                var lines = File.ReadAllLines(optionsPath, Encoding.UTF8).ToList();
                bool changed = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!lines[i].StartsWith("lang:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string code = lines[i].Substring("lang:".Length).Trim();
                    string lower = code.ToLowerInvariant();
                    if (lower != code && System.Text.RegularExpressions.Regex.IsMatch(lower, "^[a-z]{2,3}_[a-z]{2}$"))
                    {
                        lines[i] = "lang:" + lower;
                        changed = true;
                    }
                }

                if (changed)
                {
                    File.WriteAllLines(optionsPath, lines, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
            }
        }

        // ==================== 工具 ====================

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
                if (part.Length == 0 || part == "." || part == ".." || part != part.TrimEnd(' ', '.'))
                {
                    throw new InvalidDataException("条目名含空段/相对段/尾随空格或点: " + entryName);
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

        private static string ResolveTag(string requested, string instanceDir, string versionId)
        {
            if (!string.IsNullOrEmpty(requested))
            {
                return requested;
            }

            string existing = Path.Combine(instanceDir, "RSTC_version.txt");
            if (File.Exists(existing))
            {
                string current = File.ReadAllText(existing, Encoding.UTF8).Trim();
                if (current.Length > 0)
                {
                    return current;
                }
            }

            return versionId;
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

        private static int GetTestFailAfter()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RSTC_STAGE0_TEST"), "1", StringComparison.Ordinal))
            {
                return 0;
            }

            string raw = Environment.GetEnvironmentVariable("RSTC_STAGE0_FAIL_AFTER");
            return int.TryParse(raw, out int n) ? n : 0;
        }

        private static int GetTestFailAfterDeletes()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RSTC_STAGE0_TEST"), "1", StringComparison.Ordinal))
            {
                return 0;
            }

            return string.Equals(Environment.GetEnvironmentVariable("RSTC_STAGE0_FAIL_AFTER_DELETES"), "1", StringComparison.Ordinal) ? 1 : 0;
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
