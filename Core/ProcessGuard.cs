using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WorkBuddyDataMover.Core
{
    public class ProcInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public override string ToString() =>
            string.IsNullOrEmpty(Path) ? $"{Name} (PID {Id})" : $"{Name} (PID {Id})  {Path}";
    }

    public class DirStat
    {
        public long Bytes { get; set; }
        public int Files { get; set; }
        public int Dirs { get; set; }
        public int SkippedReparse { get; set; }
        public int Errors { get; set; }
    }

    public class SubdirStat
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public RiskTier Tier { get; set; }
        public long Bytes { get; set; }
        public int Files { get; set; }
        public int Dirs { get; set; }

        public string Display =>
            $"{Name}   [{WorkBuddyPaths.TierName(Tier)}]   {Fmt.Bytes(Bytes)}   {Fmt.Count(Files)} 文件";
    }

    public class DirSnapshot
    {
        public string Root { get; set; }
        public bool Exists { get; set; }
        public bool IsJunction { get; set; }
        public string JunctionTarget { get; set; }
        public string ResolvedBy { get; set; }
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public int DirCount { get; set; }
        public long RootLooseBytes { get; set; }
        public int RootLooseFiles { get; set; }
        public List<SubdirStat> Subdirs { get; set; } = new List<SubdirStat>();

        public IEnumerable<SubdirStat> ByTier(RiskTier t) => Subdirs.Where(s => s.Tier == t);

        public long BytesOfTier(RiskTier t) => Subdirs.Where(s => s.Tier == t).Sum(s => s.Bytes);
    }

    public static class ProcessGuard
    {
        /// <summary>检测 WorkBuddy / CodeBuddy 相关进程是否仍在运行。</summary>
        public static List<ProcInfo> FindWorkBuddyProcesses()
        {
            var list = new List<ProcInfo>();
            int selfId;
            try { selfId = Process.GetCurrentProcess().Id; } catch { selfId = -1; }

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return list; }

            foreach (Process p in all)
            {
                try
                {
                    if (p.Id == selfId) continue;

                    string name = "";
                    try { name = p.ProcessName ?? ""; } catch { }

                    string path = null;
                    try { path = p.MainModule != null ? p.MainModule.FileName : null; }
                    catch { }

                    bool hit = name.IndexOf("workbuddy", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("codebuddy", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("work-buddy", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!hit && !string.IsNullOrEmpty(path))
                    {
                        hit = path.IndexOf(@"\WorkBuddy\", StringComparison.OrdinalIgnoreCase) >= 0
                           || path.IndexOf(@"\WorkBuddy", StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    if (hit)
                        list.Add(new ProcInfo { Id = p.Id, Name = name, Path = path });
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            return list.OrderBy(x => x.Id).ToList();
        }

        /// <summary>
        /// 用独占方式试探文件是否被占用。
        /// 只用 Read 权限申请 FileShare.None：既能发现任何已打开该文件的句柄，
        /// 又不会被"文件只读"这类属性误判成没被占用。
        /// </summary>
        public static bool IsFileLocked(string filePath)
        {
            try
            {
                using (new FileStream(LongPath.Fix(filePath), FileMode.Open, FileAccess.Read, FileShare.None))
                    return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return true; }
            catch { return false; }
        }

        /// <summary>探测数据目录里的 SQLite 库是否仍被占用（WAL 并发写风险）。</summary>
        public static List<string> FindLockedDatabaseFiles(string root)
        {
            var locked = new List<string>();
            if (!LongPath.IsDirectory(root)) return locked;

            string dir = LongPath.Fix(root);
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir).ToList(); }
            catch { return locked; }

            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (!name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsFileLocked(f)) locked.Add(LongPath.Unfix(f));
            }

            return locked;
        }

        /// <summary>统计目录体积，跳过重解析点，不跟随联接，避免重复计算与自引用死循环。</summary>
        public static DirStat MeasureDirectory(string path, Func<bool> cancelled = null)
        {
            var stat = new DirStat();
            if (!LongPath.IsDirectory(path)) return stat;

            var stack = new Stack<string>();
            stack.Push(LongPath.Fix(path));

            while (stack.Count > 0)
            {
                if (cancelled != null && cancelled()) break;

                string cur = stack.Pop();
                List<string> items;
                try { items = Directory.EnumerateFileSystemEntries(cur).ToList(); }
                catch { stat.Errors++; continue; }

                foreach (string item in items)
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(item); }
                    catch { stat.Errors++; continue; }

                    if ((attr & FileAttributes.ReparsePoint) != 0) { stat.SkippedReparse++; continue; }

                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        stat.Dirs++;
                        stack.Push(item);
                    }
                    else
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(item);
                            stat.Bytes += fi.Length;
                            stat.Files++;
                        }
                        catch { stat.Errors++; }
                    }
                }
            }

            return stat;
        }

        /// <summary>扫描数据目录：体积、文件数、一级子目录分级。</summary>
        public static DirSnapshot Snapshot(string root, Func<bool> cancelled = null, Action<string> progress = null)
        {
            var snap = new DirSnapshot { Root = LongPath.Unfix(root) };

            if (!LongPath.Exists(root))
            {
                snap.Exists = false;
                return snap;
            }

            snap.Exists = true;
            snap.IsJunction = Junction.IsJunction(root);
            snap.JunctionTarget = snap.IsJunction && Junction.TryGetTarget(root, out string jt) ? jt : null;

            if (!snap.IsJunction)
            {
                // 只有真实目录才做完整统计
                string dir = LongPath.Fix(root);
                List<string> items;
                try { items = Directory.EnumerateFileSystemEntries(dir).ToList(); }
                catch { items = new List<string>(); }

                foreach (string item in items)
                {
                    if (cancelled != null && cancelled()) break;

                    FileAttributes attr;
                    try { attr = File.GetAttributes(item); }
                    catch { continue; }

                    string name = Path.GetFileName(item);

                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        // 已存在的联接：记录体积为 0，不再穿透
                        snap.Subdirs.Add(new SubdirStat
                        {
                            Name = name,
                            FullPath = LongPath.Unfix(item),
                            Tier = WorkBuddyPaths.Classify(name),
                            Bytes = 0,
                            Files = 0,
                            Dirs = 0,
                        });
                        continue;
                    }

                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        if (progress != null) progress(name);
                        DirStat st = MeasureDirectory(item, cancelled);
                        snap.Subdirs.Add(new SubdirStat
                        {
                            Name = name,
                            FullPath = LongPath.Unfix(item),
                            Tier = WorkBuddyPaths.Classify(name),
                            Bytes = st.Bytes,
                            Files = st.Files,
                            Dirs = st.Dirs,
                        });
                        snap.DirCount++;
                        snap.TotalBytes += st.Bytes;
                        snap.FileCount += st.Files;
                    }
                    else
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(item);
                            snap.RootLooseBytes += fi.Length;
                            snap.RootLooseFiles++;
                            snap.TotalBytes += fi.Length;
                            snap.FileCount++;
                        }
                        catch { }
                    }
                }
            }

            snap.Subdirs.Sort((a, b) =>
            {
                int t = a.Tier.CompareTo(b.Tier);
                if (t != 0) return t;
                int s = b.Bytes.CompareTo(a.Bytes);
                if (s != 0) return s;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return snap;
        }
    }
}
