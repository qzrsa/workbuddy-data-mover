using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace WorkBuddyDataMover.Core
{
    public class ArchiveResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string ArchivePath { get; set; }
        public long ArchiveBytes { get; set; }
        public long SourceBytes { get; set; }
        public int SourceFiles { get; set; }
        public TimeSpan Elapsed { get; set; }

        /// <summary>实际使用的压缩后端：7z 或 zip。</summary>
        public string Backend { get; set; }

        public double Ratio => SourceBytes > 0 ? (double)ArchiveBytes / SourceBytes : 0;
    }

    /// <summary>
    /// 备份目录归档：把 .moved-* 压成单个压缩包，供释放源盘空间时保留"时间点快照"。
    /// 优先调用 7-Zip（LZMA2，对文本类数据压缩率明显更好），未安装时回退到内置 ZipArchive。
    /// </summary>
    public static class ArchiveEngine
    {
        /// <summary>在常见安装位置寻找 7z.exe；找不到返回 null。</summary>
        public static string FindSevenZip()
        {
            var candidates = new List<string>
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
                @"C:\7-Zip\7z.exe",
            };

            string env = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(env))
                candidates.Add(Path.Combine(env, "7-Zip", "7z.exe"));

            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }

            // 退而求其次：PATH 里的 7z
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim(), "7z.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }

            return null;
        }

        /// <summary>该压缩包是否由 7z 生成（用于选择校验方式）。</summary>
        public static bool LooksLike7z(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath)) return false;
            return archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 创建压缩包。sourceDir 会作为根目录打进包里，解压到其上级目录即可原样还原。
        /// level：1=最快，3=较快（默认），5=标准，7=最大，9=极限。
        /// 十几万个小文件时 LZMA2 的开销主要在文件切换上，级别越高越慢，压缩率提升却有限。
        /// </summary>
        public static ArchiveResult Create(
            string sourceDir,
            string archivePath,
            Action<int, string> progress = null,
            Func<bool> cancelled = null,
            int level = 3)
        {
            var sw = Stopwatch.StartNew();
            var result = new ArchiveResult { ArchivePath = archivePath };

            if (!LongPath.IsDirectory(sourceDir))
            {
                result.Error = "源目录不存在：" + sourceDir;
                return result;
            }
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                result.Error = "未指定压缩包保存路径。";
                return result;
            }

            string src = LongPath.Unfix(LongPath.Normalize(sourceDir));
            string dst = LongPath.Unfix(Path.GetFullPath(archivePath));

            string dstDir = Path.GetDirectoryName(dst);
            if (string.IsNullOrEmpty(dstDir))
            {
                result.Error = "压缩包保存路径无效：" + dst;
                return result;
            }

            // 压缩包不能放进正在被压缩的目录里，否则会自己压自己
            if (LongPath.IsSameOrUnder(src, dst))
            {
                result.Error = "压缩包不能放在被压缩的目录内部：" + dst;
                return result;
            }

            try { Directory.CreateDirectory(LongPath.Fix(dstDir)); }
            catch (Exception ex)
            {
                result.Error = "无法创建保存目录：" + ex.Message;
                return result;
            }

            if (LongPath.Exists(dst))
            {
                result.Error = "目标压缩包已存在，请换个文件名：" + dst;
                return result;
            }

            // 统计源体积（用于算压缩率与展示）
            try
            {
                ScanResult scan = FileCopier.Scan(src, cancelled);
                FileCopier.ThrowIfScanFailed(scan, "备份目录");
                result.SourceBytes = scan.TotalBytes;
                result.SourceFiles = scan.FileCount;
            }
            catch (OperationCanceledException) { result.Error = "操作已取消。"; return result; }
            catch (Exception ex) { result.Error = "统计备份目录失败：" + ex.Message; return result; }

            if (progress != null) progress(0, "准备压缩…");

            string sevenZip = FindSevenZip();
            if (sevenZip != null)
            {
                result.Backend = "7z";
                Run7z(sevenZip, src, dst, result, progress, cancelled, level);
            }
            else
            {
                result.Backend = "zip";
                RunZip(src, dst, result, progress, cancelled);
            }

            result.Elapsed = sw.Elapsed;

            if (result.Success)
            {
                try { result.ArchiveBytes = new FileInfo(LongPath.Fix(dst)).Length; } catch { }
                if (progress != null) progress(100, "压缩完成");
            }
            else
            {
                // 失败时不留半截压缩包，避免以后误以为它是完整备份
                try { if (File.Exists(LongPath.Fix(dst))) File.Delete(LongPath.Fix(dst)); } catch { }
            }

            return result;
        }

        private static void Run7z(string sevenZip, string srcDir, string archivePath,
            ArchiveResult result, Action<int, string> progress, Func<bool> cancelled, int level)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // a=添加  -t7z=格式  -mx=压缩级别  -mmt=多线程  -bsp1=进度输出  -bso0=不输出文件清单
            int lv = Math.Max(0, Math.Min(9, level));
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            foreach (string a in new[]
            {
                "a", "-t7z", "-mx=" + lv, "-mmt=" + threads, "-bsp1", "-bso0", "-y", archivePath, srcDir,
            })
                psi.ArgumentList.Add(a);

            using (var p = new Process { StartInfo = psi })
            {
                var stderr = new StringBuilder();

                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    int pct = ParsePercent(e.Data);
                    if (pct >= 0 && progress != null) progress(pct, "正在压缩… " + pct + "%");
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) { lock (stderr) stderr.AppendLine(e.Data); }
                };

                try
                {
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    while (!p.WaitForExit(250))
                    {
                        if (cancelled != null && cancelled())
                        {
                            try { p.Kill(true); } catch { }
                            result.Error = "操作已取消。";
                            return;
                        }
                    }

                    p.WaitForExit();   // 确保异步输出排空

                    if (p.ExitCode == 0)
                    {
                        result.Success = true;
                    }
                    else
                    {
                        string err = "";
                        lock (stderr) err = stderr.ToString().Trim();
                        result.Error = "7-Zip 返回错误码 " + p.ExitCode +
                                       (string.IsNullOrEmpty(err) ? "" : "：" + err);
                    }
                }
                catch (Exception ex)
                {
                    result.Error = "调用 7-Zip 失败：" + ex.Message;
                }
            }
        }

        private static void RunZip(string srcDir, string archivePath,
            ArchiveResult result, Action<int, string> progress, Func<bool> cancelled)
        {
            // 兜底方案：.NET 内置 ZipArchive。无进度回调，长任务体验差，但在没装 7-Zip 的机器上能用。
            try
            {
                if (progress != null) progress(0, "正在压缩（内置 Zip，无法显示进度）…");

                using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create, Encoding.UTF8))
                {
                    string rootName = Path.GetFileName(srcDir);
                    var stack = new Stack<KeyValuePair<string, string>>();
                    stack.Push(new KeyValuePair<string, string>(LongPath.Fix(srcDir), rootName));

                    while (stack.Count > 0)
                    {
                        if (cancelled != null && cancelled())
                        {
                            result.Error = "操作已取消。";
                            return;
                        }

                        KeyValuePair<string, string> cur = stack.Pop();
                        List<string> items;
                        try { items = Directory.EnumerateFileSystemEntries(cur.Key).ToList(); }
                        catch (Exception ex) { throw new IOException("读取失败：" + LongPath.Unfix(cur.Key) + " —— " + ex.Message, ex); }

                        foreach (string item in items)
                        {
                            FileAttributes attr = File.GetAttributes(item);
                            string name = Path.GetFileName(item);
                            string rel = cur.Value + "/" + name;

                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;   // 与复制策略一致：跳过联接

                            if ((attr & FileAttributes.Directory) != 0)
                            {
                                zip.CreateEntry(rel + "/");
                                stack.Push(new KeyValuePair<string, string>(item, rel));
                            }
                            else
                            {
                                ZipArchiveEntry e = zip.CreateEntry(rel, CompressionLevel.Optimal);
                                using (Stream os = e.Open())
                                using (var fs = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    fs.CopyTo(os);
                            }
                        }
                    }
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = "压缩失败：" + ex.Message;
            }
        }

        private static int ParsePercent(string line)
        {
            // 7z -bsp1 输出形如：" 12%"、" 12% 5 - file"
            if (string.IsNullOrEmpty(line)) return -1;
            int i = line.IndexOf('%');
            if (i <= 0) return -1;

            int start = i - 1;
            while (start >= 0 && char.IsDigit(line[start])) start--;
            string num = line.Substring(start + 1, i - start - 1);
            return int.TryParse(num, out int v) && v >= 0 && v <= 100 ? v : -1;
        }

        /// <summary>
        /// 校验压缩包可完整解出：7z 用 `7z t`，zip 遍历条目。
        /// 只有校验通过，才允许删除原备份目录。
        /// </summary>
        public static bool Verify(string archivePath, out string message)
        {
            message = null;

            if (!File.Exists(LongPath.Fix(archivePath)))
            {
                message = "压缩包不存在：" + archivePath;
                return false;
            }

            if (LooksLike7z(archivePath))
            {
                string sevenZip = FindSevenZip();
                if (sevenZip == null)
                {
                    message = "未找到 7-Zip，无法校验；请手动用 7-Zip 打开确认。";
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = sevenZip,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("t");
                psi.ArgumentList.Add("-bso0");
                psi.ArgumentList.Add(archivePath);

                try
                {
                    using (var p = Process.Start(psi))
                    {
                        var err = new StringBuilder();
                        p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(30 * 60 * 1000))
                        {
                            try { p.Kill(true); } catch { }
                            message = "校验超时（超过 30 分钟）。";
                            return false;
                        }

                        if (p.ExitCode == 0) return true;

                        string detail = "";
                        lock (err) detail = err.ToString().Trim();
                        message = "7-Zip 校验失败（错误码 " + p.ExitCode + "）" +
                                  (string.IsNullOrEmpty(detail) ? "" : "：" + detail);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    message = "校验失败：" + ex.Message;
                    return false;
                }
            }

            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(LongPath.Fix(archivePath)))
                {
                    int entries = 0;
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                        entries++;
                    }

                    if (entries == 0)
                    {
                        message = "压缩包里没有任何文件。";
                        return false;
                    }

                    message = "共 " + entries + " 个文件条目。";
                    return true;
                }
            }
            catch (Exception ex)
            {
                message = "校验失败：" + ex.Message;
                return false;
            }
        }
    }
}
