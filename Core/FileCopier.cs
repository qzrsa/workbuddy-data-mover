using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WorkBuddyDataMover.Core
{
    public class FileEntry
    {
        public string RelPath { get; set; }
        public bool IsDir { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }

    public class ScanResult
    {
        public List<FileEntry> Entries { get; set; } = new List<FileEntry>();
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public int DirCount { get; set; }
        public int SkippedReparse { get; set; }

        /// <summary>被跳过的重解析点（联接 / 符号链接）路径，用于在日志里明确告知。</summary>
        public List<string> ReparsePaths { get; set; } = new List<string>();

        public List<string> ErrorMessages { get; set; } = new List<string>();
    }

    public class CopyStats
    {
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int DirsCreated { get; set; }
        public long BytesCopied { get; set; }
        public int ScanPending { get; set; }
        public List<string> ErrorMessages { get; set; } = new List<string>();
    }

    /// <summary>
    /// 递归扫描 / 复制：栈式遍历避免爆栈，跳过重解析点，支持"只补不覆盖"合并模式。
    /// </summary>
    public static class FileCopier
    {
        /// <summary>递归扫描目录，返回相对路径清单与统计（跳过重解析点）。</summary>
        public static ScanResult Scan(string root, Func<bool> cancelled = null)
        {
            var result = new ScanResult();
            if (!LongPath.IsDirectory(root))
            {
                result.ErrorMessages.Add("源目录不存在或不是目录：" + LongPath.Unfix(root));
                return result;
            }

            var stack = new Stack<KeyValuePair<string, string>>(); // full, rel
            stack.Push(new KeyValuePair<string, string>(LongPath.Fix(root), ""));

            while (stack.Count > 0)
            {
                if (cancelled != null && cancelled())
                    throw new OperationCanceledException("操作已取消。");

                KeyValuePair<string, string> cur = stack.Pop();
                List<string> items;
                try { items = Directory.EnumerateFileSystemEntries(cur.Key).ToList(); }
                catch (Exception ex)
                {
                    result.ErrorMessages.Add(LongPath.Unfix(cur.Key) + "：" + ex.Message);
                    continue;
                }

                foreach (string item in items)
                {
                    string name = Path.GetFileName(item);
                    string childRel = cur.Value.Length == 0 ? name : cur.Value + "\\" + name;

                    FileAttributes attr;
                    try { attr = File.GetAttributes(item); }
                    catch (Exception ex)
                    {
                        result.ErrorMessages.Add(LongPath.Unfix(item) + "：" + ex.Message);
                        continue;
                    }

                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        result.SkippedReparse++;
                        result.ReparsePaths.Add(childRel);
                        continue;
                    }

                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        result.DirCount++;
                        result.Entries.Add(new FileEntry { RelPath = childRel, IsDir = true });
                        stack.Push(new KeyValuePair<string, string>(item, childRel));
                    }
                    else
                    {
                        long len = 0;
                        DateTime lw = DateTime.MinValue;
                        try
                        {
                            FileInfo fi = new FileInfo(item);
                            len = fi.Length;
                            lw = fi.LastWriteTimeUtc;
                        }
                        catch (Exception ex)
                        {
                            result.ErrorMessages.Add(LongPath.Unfix(item) + "：" + ex.Message);
                        }

                        result.FileCount++;
                        result.TotalBytes += len;
                        result.Entries.Add(new FileEntry
                        {
                            RelPath = childRel,
                            IsDir = false,
                            Length = len,
                            LastWriteUtc = lw,
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 复制。merge=false 时为严格覆盖复制；merge=true 时为"只补不覆盖"。
        /// </summary>
        public static CopyStats Copy(
            string sourceRoot,
            string targetRoot,
            ScanResult scan,
            bool merge,
            Action<CopyStats, string> progress = null,
            Func<bool> cancelled = null)
        {
            var stats = new CopyStats { ScanPending = scan.Entries.Count };

            string srcRoot = LongPath.Fix(sourceRoot);
            string dstRoot = LongPath.Fix(targetRoot);

            Directory.CreateDirectory(dstRoot);

            foreach (FileEntry e in scan.Entries)
            {
                if (cancelled != null && cancelled())
                    throw new OperationCanceledException("操作已取消。");

                string src = LongPath.Fix(Path.Combine(LongPath.Unfix(srcRoot), e.RelPath));
                string dst = LongPath.Fix(Path.Combine(LongPath.Unfix(dstRoot), e.RelPath));

                try
                {
                    if (e.IsDir)
                    {
                        if (!LongPath.IsDirectory(dst))
                        {
                            Directory.CreateDirectory(dst);
                            stats.DirsCreated++;
                        }
                    }
                    else
                    {
                        if (merge && LongPath.Exists(dst))
                        {
                            DateTime targetTime = File.GetLastWriteTimeUtc(dst);
                            if (targetTime >= e.LastWriteUtc)
                            {
                                // 目标更新或同版本：保留目标，绝不回退
                                // 注意：ScanPending 统一在 finally 里递减，此处不要再减（否则会重复扣减）
                                stats.FilesSkipped++;
                                continue;
                            }
                        }

                        File.Copy(src, dst, true);
                        if (e.LastWriteUtc > DateTime.MinValue && e.LastWriteUtc.Year > 1601)
                        {
                            try { File.SetLastWriteTimeUtc(dst, e.LastWriteUtc); } catch { }
                        }

                        stats.FilesCopied++;
                        stats.BytesCopied += e.Length;
                    }
                }
                catch (Exception ex)
                {
                    string msg = $"复制失败：{e.RelPath} —— {ex.Message}";
                    stats.ErrorMessages.Add(msg);
                    throw new IOException(msg, ex);
                }
                finally
                {
                    stats.ScanPending--;
                    if (progress != null) progress(stats, e.RelPath);
                }
            }

            // 不做下限钳制：ScanPending 一旦为负，说明计数逻辑有缺陷，必须暴露出来而不是掩盖
            return stats;
        }

        /// <summary>
        /// 扫描只要有一条读取错误就拒绝继续。
        /// 读不到的目录里的文件既不会进清单也不会被复制，目标端同样看不到，
        /// 只比"总数"会得到一次假通过——那等于静默漏拷，是迁移工具最不能出现的后果。
        /// </summary>
        public static void ThrowIfScanFailed(ScanResult scan, string what)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));
            if (scan.ErrorMessages.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine($"{what}有 {scan.ErrorMessages.Count} 处无法读取，为避免漏拷已中止（源数据未改动）。");
            foreach (string m in scan.ErrorMessages.Take(8)) sb.AppendLine("  · " + m);
            if (scan.ErrorMessages.Count > 8)
                sb.AppendLine("  · …… 其余 " + (scan.ErrorMessages.Count - 8) + " 处省略");
            sb.Append("请关闭占用这些位置的程序后重试；若确认这些位置无需迁移，请改用「部分迁移」避开对应子目录。");
            throw new IOException(sb.ToString());
        }

        /// <summary>
        /// 严格校验：文件数 + 总字节 必须完全相等，且逐个文件比对存在性与长度。
        /// 只比总量是不够的：两个文件一长一短可以互相抵消，总量照样相等。
        /// </summary>
        public static void VerifyStrict(string targetRoot, ScanResult sourceScan)
        {
            var targetScan = Scan(targetRoot);

            if (targetScan.ErrorMessages.Count > 0)
                throw new IOException(
                    $"校验失败：目标目录有 {targetScan.ErrorMessages.Count} 处无法读取，无法确认复制完整。已中止，源数据未改动。");

            if (targetScan.FileCount != sourceScan.FileCount)
                throw new IOException(
                    $"校验失败：文件数不一致（源 {sourceScan.FileCount}，目标 {targetScan.FileCount}）。已中止，源数据未改动。");

            if (targetScan.TotalBytes != sourceScan.TotalBytes)
                throw new IOException(
                    $"校验失败：总字节不一致（源 {sourceScan.TotalBytes}，目标 {targetScan.TotalBytes}）。已中止，源数据未改动。");

            // 逐文件比对：目标里每个源文件都必须存在，且长度一致
            var targetFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var targetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileEntry e in targetScan.Entries)
            {
                if (e.IsDir) targetDirs.Add(e.RelPath);
                else targetFiles[e.RelPath] = e.Length;
            }

            var failures = new List<string>();
            foreach (FileEntry e in sourceScan.Entries)
            {
                if (e.IsDir)
                {
                    if (!targetDirs.Contains(e.RelPath)) failures.Add("目标缺少目录：" + e.RelPath);
                    continue;
                }

                if (!targetFiles.TryGetValue(e.RelPath, out long len))
                {
                    failures.Add("目标缺少文件：" + e.RelPath);
                    continue;
                }

                if (len != e.Length)
                    failures.Add($"长度不一致：{e.RelPath}（源 {e.Length} 字节，目标 {len} 字节）");
            }

            if (failures.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"校验失败：{failures.Count} 个条目与源不一致。已中止，源数据未改动。");
                foreach (string f in failures.Take(10)) sb.AppendLine("  · " + f);
                if (failures.Count > 10) sb.AppendLine("  · …… 其余 " + (failures.Count - 10) + " 项省略");
                throw new IOException(sb.ToString());
            }
        }

        /// <summary>
        /// 合并模式校验：源里每个文件都必须在目标中就位且不旧于源。
        /// 目标独有的文件只作提示，不判错。
        /// </summary>
        public static List<string> VerifyMerge(string targetRoot, ScanResult sourceScan, Action<string> log = null)
        {
            var notes = new List<string>();
            var failures = new List<string>();
            string dstRoot = LongPath.Fix(targetRoot);

            var sourceRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FileEntry e in sourceScan.Entries)
            {
                if (e.IsDir) continue;
                sourceRels.Add(e.RelPath);

                string dst = LongPath.Fix(Path.Combine(LongPath.Unfix(dstRoot), e.RelPath));

                if (!LongPath.Exists(dst))
                {
                    failures.Add("目标缺失：" + e.RelPath);
                    continue;
                }

                try
                {
                    DateTime tw = File.GetLastWriteTimeUtc(dst);
                    if (tw < e.LastWriteUtc)
                        failures.Add("目标更旧于源：" + e.RelPath);
                }
                catch (Exception ex)
                {
                    failures.Add("无法读取目标：" + e.RelPath + " " + ex.Message);
                }
            }

            if (failures.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"校验失败：{failures.Count} 个文件未在目标就位。已中止，源数据未改动。");
                foreach (string f in failures.Take(10)) sb.AppendLine("  · " + f);
                if (failures.Count > 10) sb.AppendLine("  · …… 其余 " + (failures.Count - 10) + " 项省略");
                throw new IOException(sb.ToString());
            }

            // 目标独有的文件（合并模式合法）
            ScanResult targetScan = Scan(targetRoot);
            if (targetScan.ErrorMessages.Count > 0)
                throw new IOException(
                    $"校验失败：目标目录有 {targetScan.ErrorMessages.Count} 处无法读取，无法确认复制完整。已中止，源数据未改动。");

            int targetOnly = 0;
            foreach (FileEntry e in targetScan.Entries)
            {
                if (e.IsDir) continue;
                if (!sourceRels.Contains(e.RelPath)) targetOnly++;
            }

            if (targetOnly > 0)
            {
                string note = $"提示：目标目录含 {targetOnly} 个源里没有的文件，合并模式下合法，已保留。";
                notes.Add(note);
                if (log != null) log(note);
            }

            return notes;
        }
    }
}
