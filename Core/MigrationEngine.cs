using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WorkBuddyDataMover.Core
{
    public enum MigrateMode
    {
        Junction = 0,
        EnvVar = 1,
        Partial = 2
    }

    public enum LogLevel
    {
        Info,
        Step,
        Warn,
        Error,
        Ok
    }

    public class MigrationOptions
    {
        public string SourcePath { get; set; }
        public string TargetRoot { get; set; }
        public MigrateMode Mode { get; set; } = MigrateMode.Junction;
        public bool MergeMode { get; set; }
        public List<string> PartialNames { get; set; } = new List<string>();
        public bool SkipProcessCheck { get; set; }
        public string RecordPath { get; set; }
        public long RequiredBytes { get; set; }

        /// <summary>环境变量方式使用的变量名，默认 WORKBUDDY_CONFIG_DIR（自检时用临时变量名避免污染真实配置）。</summary>
        public string EnvVariableName { get; set; }

        /// <summary>是否跳过 WM_SETTINGCHANGE 广播（自检时跳过，避免反复打扰其它程序）。</summary>
        public bool SkipSettingsBroadcast { get; set; }
    }

    public class CheckItem
    {
        public string Name { get; set; }
        public bool Ok { get; set; }
        public string Detail { get; set; }
        public bool Fatal { get; set; } = true;

        public override string ToString() => $"{(Ok ? "[通过]" : "[阻止]")} {Name}：{Detail}";
    }

    public class PreflightResult
    {
        public List<CheckItem> Items { get; set; } = new List<CheckItem>();
        public bool Passed => Items.All(i => i.Ok || !i.Fatal);
        public string TargetDir { get; set; }
        public long RequiredBytes { get; set; }

        public IEnumerable<CheckItem> Failures => Items.Where(i => !i.Ok && i.Fatal);
    }

    public class MigrationResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public MigrationRecord Record { get; set; }
        public List<string> BackupPaths { get; set; } = new List<string>();
        public long Bytes { get; set; }
        public int Files { get; set; }
        public TimeSpan Elapsed { get; set; }

        /// <summary>回滚记录是否成功落盘。写入失败不代表迁移失败，但用户必须自己记下备份路径。</summary>
        public bool RecordSaved { get; set; } = true;
        public string RecordSaveError { get; set; }
    }

    public static class MigrationEngine
    {
        private const long SpaceHeadroomBytes = 64L * 1024 * 1024; // 64MB 余量

        // ---------------- 路径推导 ----------------

        public static string ResolveTargetDir(string sourcePath, string targetRoot)
        {
            string leaf = Path.GetFileName(LongPath.Normalize(sourcePath));
            if (string.IsNullOrEmpty(leaf)) leaf = WorkBuddyPaths.DefaultFolderName;

            string root = LongPath.Unfix(targetRoot ?? "").TrimEnd('\\', '/');

            // "D:" 是"盘符相对路径"，直接 Path.Combine("D:", "x") 会得到 "D:x"，
            // 再 GetFullPath 就跑到 D 盘的当前目录里去了。补回根分隔符。
            if (root.Length == 2 && root[1] == ':') root += "\\";
            if (root.Length == 0) throw new ArgumentException("目标位置为空。");

            return Path.Combine(root, leaf);
        }

        // ---------------- 预检 ----------------

        public static PreflightResult Preflight(MigrationOptions opt, Action<LogLevel, string> log = null)
        {
            var result = new PreflightResult();
            string source = LongPath.Unfix(opt.SourcePath ?? "");
            string targetRoot = LongPath.Unfix(opt.TargetRoot ?? "");

            void Add(string name, bool ok, string detail, bool fatal = true)
                => result.Items.Add(new CheckItem { Name = name, Ok = ok, Detail = detail, Fatal = fatal });

            // 1) 源目录
            if (!LongPath.Exists(source))
            {
                Add("数据目录", false, "目录不存在：" + source);
                return result;
            }

            if (Junction.IsJunction(source))
            {
                Junction.TryGetTarget(source, out string from);
                Add("数据目录", false, $"已经是目录联接（→ {from}），无需再次迁移。如需改指向，请先一键回滚。");
                return result;
            }

            if (!LongPath.IsDirectory(source))
            {
                Add("数据目录", false, "路径存在但不是目录：" + source);
                return result;
            }

            Add("数据目录", true, source);

            // 2) 目标路径
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                Add("目标位置", false, "未指定目标位置。");
                return result;
            }

            string targetDir = ResolveTargetDir(source, targetRoot);
            result.TargetDir = targetDir;

            if (LongPath.PathEquals(source, targetDir))
            {
                Add("目标位置", false, "目标与源目录相同，无法迁移到自己身上。");
                return result;
            }

            if (LongPath.IsSameOrUnder(source, targetDir))
            {
                Add("目标位置", false, "目标位于源目录内部，会造成递归复制。");
                return result;
            }

            if (LongPath.IsSameOrUnder(targetDir, source))
            {
                Add("目标位置", false, "目标位于源目录的上级，会导致原目录被一起搬走。");
                return result;
            }

            if (Junction.IsJunction(targetDir))
            {
                Add("目标位置", false, "目标位置已是一个目录联接，请选择其它位置。");
                return result;
            }

            // 3) 目标盘
            var drive = Disks.GetDriveOf(targetRoot);
            if (drive == null)
            {
                Add("目标盘", false, "目标盘未就绪或不存在：" + targetRoot);
                return result;
            }

            if (drive.IsNetwork)
            {
                Add("目标盘", false, "网络驱动器不支持目录联接，请选择本地磁盘。");
                return result;
            }

            if (!drive.SupportsJunction)
            {
                Add("目标盘文件系统", false, $"目标盘为 {drive.Format}，目录联接需要 NTFS / ReFS。");
                return result;
            }

            Add("目标盘", true, $"{drive.Root.TrimEnd('\\')}  {drive.Format}，可用 {Fmt.Bytes(drive.FreeBytes)}");

            // 4) 空间
            long required = opt.RequiredBytes;
            if (required <= 0)
            {
                if (opt.Mode == MigrateMode.Partial)
                {
                    foreach (string name in opt.PartialNames)
                        required += ProcessGuard.MeasureDirectory(Path.Combine(source, name)).Bytes;
                }
                else
                {
                    required = ProcessGuard.MeasureDirectory(source).Bytes;
                }
            }

            result.RequiredBytes = required;
            long need = required + SpaceHeadroomBytes;

            if (drive.FreeBytes < need)
            {
                Add("目标空间", false,
                    $"空间不足：需要 {Fmt.Bytes(need)}（数据 {Fmt.Bytes(required)} + 64MB 余量），可用 {Fmt.Bytes(drive.FreeBytes)}。");
            }
            else
            {
                Add("目标空间", true,
                    $"需要 {Fmt.Bytes(need)}（数据 {Fmt.Bytes(required)} + 64MB 余量），可用 {Fmt.Bytes(drive.FreeBytes)}。");
            }

            // 5) 目标非空
            bool targetExists = LongPath.IsDirectory(targetDir);
            bool targetNonEmpty = targetExists && HasAnyEntry(targetDir);

            if (LongPath.Exists(targetDir) && !targetExists)
            {
                Add("目标目录状态", false, "目标位置已存在一个同名文件，无法作为目录使用：" + targetDir + "。请换一个位置。");
                return result;
            }

            if (string.Equals(Path.GetDirectoryName(LongPath.Normalize(targetDir)),
                    LongPath.Normalize(Path.GetPathRoot(targetDir) ?? ""),
                    StringComparison.OrdinalIgnoreCase))
            {
                Add("目标目录位置", true,
                    "目标目录将直接落在盘根：" + targetDir + "。可以继续，但建议放到专用子目录里，便于以后整体搬走。", false);
            }

            if (opt.Mode == MigrateMode.Partial)
            {
                // 部分迁移的目标盘根目录允许非空，逐个检查子目录目标即可
                Add("目标目录状态", true,
                    targetExists ? "目标目录已存在（部分迁移逐个检查子目录目标）" : "目标目录不存在，将自动创建。", false);
            }
            else if (targetNonEmpty && !opt.MergeMode)
            {
                Add("目标目录状态", false,
                    "目标目录已存在且非空。为避免与既有数据混在一起，请改选空目录，或勾选「允许合并到非空目标」。");
            }
            else if (targetNonEmpty)
            {
                Add("目标目录状态", true, "目标目录非空，已允许合并（只补不覆盖）。", false);
            }
            else
            {
                Add("目标目录状态", true, targetExists ? "目标目录已存在且为空。" : "目标目录不存在，将自动创建。");
            }

            // 6) 环境变量冲突（只看用户级持久设置，进程继承值不算）
            string envNameForCheck = string.IsNullOrWhiteSpace(opt.EnvVariableName)
                ? WorkBuddyPaths.EnvPrimary
                : opt.EnvVariableName.Trim();
            string envVal = WorkBuddyPaths.GetUserEnv(envNameForCheck);

            if (!string.IsNullOrWhiteSpace(envVal) && opt.Mode != MigrateMode.EnvVar)
            {
                if (!LongPath.PathEquals(envVal, source))
                {
                    Add("环境变量冲突", false,
                        $"用户级环境变量 {envNameForCheck} 已指向 {envVal}，产品会优先读它，目录联接不会生效。" +
                        "请先清空该环境变量，或改用「环境变量」方式。");
                }
                else
                {
                    Add("环境变量", true, $"{envNameForCheck} = {envVal}（与数据目录一致，联接仍然生效）", false);
                }
            }
            else if (opt.Mode == MigrateMode.EnvVar)
            {
                Add("环境变量", true,
                    string.IsNullOrWhiteSpace(envVal)
                        ? $"{envNameForCheck} 当前未设置，迁移后写入。"
                        : $"{envNameForCheck} 当前为 {envVal}，迁移后覆盖（原值会记入回滚记录）。",
                    false);
            }

            // 7) 进程占用
            if (!opt.SkipProcessCheck)
            {
                List<ProcInfo> procs = ProcessGuard.FindWorkBuddyProcesses();
                if (procs.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append("检测到 WorkBuddy 相关进程仍在运行：");
                    foreach (ProcInfo p in procs.Take(6)) sb.Append(" | ").Append(p.Name).Append(" (PID ").Append(p.Id).Append(")");
                    sb.Append("。运行中会有 SQLite WAL 并发写风险，请从托盘完全退出后重试。");
                    Add("进程占用", false, sb.ToString());
                }
                else
                {
                    Add("进程占用", true, "未检测到 WorkBuddy 进程。");
                }

                List<string> locked = ProcessGuard.FindLockedDatabaseFiles(source);
                if (locked.Count > 0)
                {
                    Add("数据库占用", false,
                        "以下数据库文件仍被占用：" + string.Join("；", locked.Take(5)) + "。请完全退出 WorkBuddy。");
                }
                else
                {
                    Add("数据库占用", true, "未检测到数据库被占用。");
                }
            }
            else
            {
                Add("进程占用", true, "已跳过（自检模式）。", false);
            }

            // 8) 部分迁移清单
            if (opt.Mode == MigrateMode.Partial)
            {
                if (opt.PartialNames == null || opt.PartialNames.Count == 0)
                {
                    Add("迁移清单", false, "部分迁移至少要勾选一个子目录。");
                }
                else
                {
                    var missing = new List<string>();
                    var coreSelected = new List<string>();
                    foreach (string name in opt.PartialNames)
                    {
                        string full = Path.Combine(source, name);
                        if (!LongPath.IsDirectory(full)) missing.Add(name);
                        if (WorkBuddyPaths.Classify(name) == RiskTier.Core) coreSelected.Add(name);
                    }

                    if (coreSelected.Count > 0)
                        Add("迁移清单", false, "核心数据禁止迁移：" + string.Join("、", coreSelected));
                    else if (missing.Count > 0)
                        Add("迁移清单", false, "以下子目录不存在或不是目录：" + string.Join("、", missing));
                    else
                        Add("迁移清单", true, $"已勾选 {opt.PartialNames.Count} 个目录，合计 {Fmt.Bytes(required)}。");
                }

                if (!opt.MergeMode && opt.PartialNames != null && opt.PartialNames.Count > 0)
                {
                    var occupied = new List<string>();
                    foreach (string name in opt.PartialNames)
                    {
                        string subTarget = Path.Combine(targetDir, name);
                        if (LongPath.IsDirectory(subTarget) && HasAnyEntry(subTarget)) occupied.Add(name);
                    }

                    if (occupied.Count > 0)
                        Add("子目录目标状态", false,
                            "以下子目录在目标位置已存在且非空：" + string.Join("、", occupied) +
                            "。请勾选「允许合并到非空目标」，或换一个空的目标文件夹。");
                    else
                        Add("子目录目标状态", true, "各子目录在目标位置均为空或不存在。", false);
                }
            }

            // 9) 目标可写
            try
            {
                Directory.CreateDirectory(LongPath.Fix(targetRoot));
                string probe = Path.Combine(targetRoot, ".wbdm-write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(LongPath.Fix(probe), "ok");
                File.Delete(LongPath.Fix(probe));
                Add("目标可写", true, "已成功写入测试文件。", false);
            }
            catch (Exception ex)
            {
                Add("目标可写", false, "无法写入目标位置：" + ex.Message);
            }

            if (log != null)
            {
                foreach (CheckItem it in result.Items)
                    log(it.Ok ? LogLevel.Ok : (it.Fatal ? LogLevel.Error : LogLevel.Warn), it.ToString());
            }

            return result;
        }

        private static bool HasAnyEntry(string dir)
        {
            try
            {
                using (var e = Directory.EnumerateFileSystemEntries(LongPath.Fix(dir)).GetEnumerator())
                    return e.MoveNext();
            }
            catch { return true; }
        }

        // ---------------- 执行迁移 ----------------

        public static MigrationResult Execute(
            MigrationOptions opt,
            Action<LogLevel, string> log,
            Action<int, long, long> progress = null,
            Func<bool> cancelled = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new MigrationResult { Success = false };
            if (log == null) log = (l, m) => { };

            string source = LongPath.Unfix(opt.SourcePath);
            string targetRoot = LongPath.Unfix(opt.TargetRoot);
            string targetDir = ResolveTargetDir(source, targetRoot);

            var record = new MigrationRecord
            {
                Mode = opt.Mode == MigrateMode.Junction ? "junction"
                     : (opt.Mode == MigrateMode.EnvVar ? "env" : "partial"),
                SourcePath = source,
                TargetPath = targetDir,
                MergeMode = opt.MergeMode,
                TimestampLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Status = "completed",
            };
            result.Record = record;

            // 基本不变量自检：Execute 不假设调用方一定跑过 Preflight
            AssertInvariants(opt, source, targetDir);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string envName = string.IsNullOrWhiteSpace(opt.EnvVariableName)
                ? WorkBuddyPaths.EnvPrimary
                : opt.EnvVariableName.Trim();
            string envOriginal = WorkBuddyPaths.GetUserEnv(envName);

            try
            {
                log(LogLevel.Step, "=== 开始迁移 ===");
                log(LogLevel.Info, "源目录：" + source);
                log(LogLevel.Info, "目标目录：" + targetDir);

                Directory.CreateDirectory(LongPath.Fix(targetRoot));

                if (opt.Mode == MigrateMode.Partial)
                    RunPartial(opt, record, source, targetDir, stamp, log, progress, cancelled);
                else
                    RunFull(opt, record, source, targetDir, stamp, envName, envOriginal, log, progress, cancelled);

                if (cancelled != null && cancelled())
                    throw new OperationCanceledException("操作已取消。");

                // 记账失败不等于迁移失败：这里已经不写源目录了，绝不能再把刚搬好的数据回滚掉。
                try
                {
                    record.Save(opt.RecordPath);
                }
                catch (Exception sex)
                {
                    result.RecordSaved = false;
                    result.RecordSaveError = sex.Message;
                    log(LogLevel.Error, "迁移已完成，但回滚记录写入失败：" + sex.Message);
                    log(LogLevel.Warn, "  请手动抄下下面的备份目录，将来需要还原时靠它们：");
                    if (!string.IsNullOrEmpty(record.BackupPath)) log(LogLevel.Warn, "    · " + record.BackupPath);
                    foreach (PartialItemRecord it in record.Items)
                        if (!string.IsNullOrEmpty(it.BackupPath)) log(LogLevel.Warn, "    · " + it.BackupPath);
                }

                result.Success = true;
                result.Bytes = record.MigratedBytes;
                result.Files = record.MigratedFiles;
                if (!string.IsNullOrEmpty(record.BackupPath)) result.BackupPaths.Add(record.BackupPath);
                foreach (PartialItemRecord it in record.Items) result.BackupPaths.Add(it.BackupPath);

                log(LogLevel.Ok, $"=== 迁移完成，用时 {Fmt.Duration(sw.Elapsed)} ===");
                log(LogLevel.Info, $"已迁移 {Fmt.Count(record.MigratedFiles)} 个文件，{Fmt.Bytes(record.MigratedBytes)}。");

                if (result.BackupPaths.Count > 0)
                {
                    log(LogLevel.Warn, "原数据已改名保留（未删除），路径见下：");
                    foreach (string b in result.BackupPaths) log(LogLevel.Warn, "  · " + b);
                    log(LogLevel.Warn, "确认 WorkBuddy 运行正常后，可用「工具 → 清理备份目录」释放这部分空间。");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                result.Elapsed = sw.Elapsed;

                if (ex is OperationCanceledException)
                {
                    log(LogLevel.Warn, "操作已取消，正在还原…");
                    TryRollback(record, log, true);
                }
                else
                {
                    log(LogLevel.Error, "迁移失败：" + ex.Message);
                    TryRollback(record, log, true);
                }
            }
            finally
            {
                result.Elapsed = sw.Elapsed;
            }

            return result;
        }

        /// <summary>执行前的硬性不变量检查，防止绕过预检直接调用引擎造成误操作。</summary>
        private static void AssertInvariants(MigrationOptions opt, string source, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("源目录为空。");
            if (!LongPath.IsDirectory(source))
                throw new DirectoryNotFoundException("源目录不存在或不是目录：" + source);
            if (Junction.IsJunction(source))
                throw new InvalidOperationException("源目录已经是目录联接，拒绝重复迁移：" + source);
            if (string.IsNullOrWhiteSpace(targetDir) || targetDir.Length < 3)
                throw new ArgumentException("目标目录无效：" + targetDir);
            if (LongPath.PathEquals(source, targetDir))
                throw new InvalidOperationException("目标与源目录相同。");
            if (LongPath.IsSameOrUnder(source, targetDir))
                throw new InvalidOperationException("目标位于源目录内部，会造成递归复制。");
            if (LongPath.IsSameOrUnder(targetDir, source))
                throw new InvalidOperationException("目标位于源目录上级，会连带搬走原目录。");
            if (opt.Mode == MigrateMode.Partial &&
                (opt.PartialNames == null || opt.PartialNames.Count == 0))
                throw new ArgumentException("部分迁移未勾选任何子目录。");
        }

        private static void RunFull(
            MigrationOptions opt,
            MigrationRecord record,
            string source,
            string targetDir,
            string stamp,
            string envName,
            string envOriginal,
            Action<LogLevel, string> log,
            Action<int, long, long> progress,
            Func<bool> cancelled)
        {
            // 1) 扫描
            log(LogLevel.Step, "1/5 扫描源目录…");
            ScanResult scan = FileCopier.Scan(source, cancelled);

            // 读不到的目录必须当场中止：此时还没写目标、也没动源目录，风险为零
            FileCopier.ThrowIfScanFailed(scan, "源目录");

            log(LogLevel.Info, $"  文件 {Fmt.Count(scan.FileCount)}，目录 {Fmt.Count(scan.DirCount)}，" +
                               $"总大小 {Fmt.Bytes(scan.TotalBytes)}，跳过重解析点 {scan.SkippedReparse} 个");

            if (scan.SkippedReparse > 0)
            {
                log(LogLevel.Warn, $"  注意：源目录内有 {scan.SkippedReparse} 个目录联接/符号链接，已按设计跳过不复制（避免重复与自引用）。");
                foreach (string rp in scan.ReparsePaths.Take(5)) log(LogLevel.Warn, "    · " + rp);
                if (scan.ReparsePaths.Count > 5)
                    log(LogLevel.Warn, "    · …… 其余 " + (scan.ReparsePaths.Count - 5) + " 个省略");
                log(LogLevel.Warn, "    迁移后这些位置在新目录里不存在，它们原本指向的数据仍留在原处。");
            }

            // 2) 复制
            log(LogLevel.Step, "2/5 复制到目标位置（长路径安全，跳过重解析点）…");
            CopyStats stats = FileCopier.Copy(source, targetDir, scan, opt.MergeMode,
                (s, cur) =>
                {
                    if (progress != null)
                    {
                        long total = Math.Max(1, scan.TotalBytes);
                        int pct = (int)Math.Min(100, s.BytesCopied * 100 / total);
                        progress(pct, s.BytesCopied, scan.TotalBytes);
                    }
                }, cancelled);

            log(LogLevel.Info, $"  已复制 {Fmt.Count(stats.FilesCopied)} 个文件 / {Fmt.Bytes(stats.BytesCopied)}，" +
                               $"跳过 {Fmt.Count(stats.FilesSkipped)} 个（目标较新），新建目录 {Fmt.Count(stats.DirsCreated)} 个");

            record.MigratedBytes = scan.TotalBytes;
            record.MigratedFiles = scan.FileCount;

            // 3) 校验
            log(LogLevel.Step, "3/5 逐目录校验…");
            if (opt.MergeMode)
            {
                FileCopier.VerifyMerge(targetDir, scan, m => log(LogLevel.Info, "  " + m));
                log(LogLevel.Ok, "  校验通过：源里每个文件都已在目标就位且不旧于源");
            }
            else
            {
                FileCopier.VerifyStrict(targetDir, scan);
                log(LogLevel.Ok, $"  校验通过：文件数 {Fmt.Count(scan.FileCount)} 与总字节 {Fmt.Bytes(scan.TotalBytes)} 严格一致");
            }

            // 4) 改名
            log(LogLevel.Step, "4/5 原目录改名（不删除）…");
            string backup = source + ".moved-" + stamp;
            if (LongPath.Exists(backup))
                throw new IOException("备份路径已存在，请稍后重试：" + backup);

            MoveDirectory(source, backup);
            record.BackupPath = backup;
            log(LogLevel.Info, "  已改名为：" + backup);

            // 5) 建立联接 / 写入环境变量
            try
            {
                if (opt.Mode == MigrateMode.Junction)
                {
                    log(LogLevel.Step, "5/5 在原位置建立目录联接（Win32 API，不调用 mklink）…");
                    Junction.Create(source, targetDir);
                    if (!Junction.TryGetTarget(source, out string actual) || !LongPath.PathEquals(actual, targetDir))
                        throw new IOException("联接回读校验未通过。");
                    log(LogLevel.Ok, $"  联接已建立并回读确认：{source} → {targetDir}");
                }
                else
                {
                    log(LogLevel.Step, "5/5 写入用户级环境变量并广播刷新…");
                    record.EnvName = envName;
                    record.EnvOriginalValue = envOriginal;
                    record.EnvOriginalExisted = !string.IsNullOrWhiteSpace(envOriginal);

                    SetUserEnv(envName, targetDir);
                    if (!opt.SkipSettingsBroadcast) BroadcastSettingChange();

                    string check = WorkBuddyPaths.GetUserEnv(envName);
                    if (!LongPath.PathEquals(check, targetDir))
                        throw new IOException("环境变量回读不一致：期望 " + targetDir + "，实际 " + check);

                    log(LogLevel.Ok, $"  {envName} = {targetDir}");
                    log(LogLevel.Warn, "  提示：已广播 WM_SETTINGCHANGE，但部分启动入口仍需注销重登后才能读到新值。");
                }
            }
            catch
            {
                log(LogLevel.Error, "第 5 步失败，自动把原目录改回原名…");
                try
                {
                    if (LongPath.Exists(backup) && !LongPath.Exists(source))
                        MoveDirectory(backup, source);
                    record.BackupPath = null;
                    log(LogLevel.Ok, "  已还原：" + source);
                }
                catch (Exception rex)
                {
                    log(LogLevel.Error, "  还原失败，请手动把 " + backup + " 改回 " + source + "：" + rex.Message);
                }
                throw;
            }
        }

        private static void RunPartial(
            MigrationOptions opt,
            MigrationRecord record,
            string source,
            string targetDir,
            string stamp,
            Action<LogLevel, string> log,
            Action<int, long, long> progress,
            Func<bool> cancelled)
        {
            List<string> names = opt.PartialNames ?? new List<string>();
            int total = names.Count;
            int index = 0;

            long bytesAcc = 0;
            int filesAcc = 0;

            foreach (string name in names)
            {
                if (cancelled != null && cancelled())
                    throw new OperationCanceledException("操作已取消。");

                index++;
                string srcSub = Path.Combine(source, name);
                string tgtSub = Path.Combine(targetDir, name);

                log(LogLevel.Step, $"[{index}/{total}] {name}：扫描…");

                if (Junction.IsJunction(srcSub))
                {
                    log(LogLevel.Warn, $"  {name} 已经是目录联接，跳过。");
                    continue;
                }

                ScanResult scan = FileCopier.Scan(srcSub, cancelled);
                FileCopier.ThrowIfScanFailed(scan, name + " 目录");
                log(LogLevel.Info, $"  文件 {Fmt.Count(scan.FileCount)}，{Fmt.Bytes(scan.TotalBytes)}");

                if (scan.SkippedReparse > 0)
                {
                    log(LogLevel.Warn, $"  {name} 内有 {scan.SkippedReparse} 个联接/符号链接被跳过（不复制）：");
                    foreach (string rp in scan.ReparsePaths.Take(5)) log(LogLevel.Warn, "    · " + rp);
                    if (scan.ReparsePaths.Count > 5)
                        log(LogLevel.Warn, "    · …… 其余 " + (scan.ReparsePaths.Count - 5) + " 个省略");
                }

                log(LogLevel.Step, $"[{index}/{total}] {name}：复制…");
                FileCopier.Copy(srcSub, tgtSub, scan, opt.MergeMode,
                    (s, cur) =>
                    {
                        if (progress != null)
                        {
                            long totalBytes = Math.Max(1, scan.TotalBytes);
                            int pct = (int)Math.Min(100, s.BytesCopied * 100 / totalBytes);
                            progress(pct, bytesAcc + s.BytesCopied, Math.Max(1, opt.RequiredBytes));
                        }
                    }, cancelled);

                log(LogLevel.Step, $"[{index}/{total}] {name}：校验…");
                if (opt.MergeMode) FileCopier.VerifyMerge(tgtSub, scan, m => log(LogLevel.Info, "  " + m));
                else FileCopier.VerifyStrict(tgtSub, scan);
                log(LogLevel.Ok, "  校验通过");

                string backup = srcSub + ".moved-" + stamp;
                if (LongPath.Exists(backup))
                    throw new IOException("备份路径已存在：" + backup);

                MoveDirectory(srcSub, backup);

                try
                {
                    Junction.Create(srcSub, tgtSub);
                }
                catch
                {
                    log(LogLevel.Error, $"  {name} 建立联接失败，正在改回原名…");
                    try { if (LongPath.Exists(backup) && !LongPath.Exists(srcSub)) MoveDirectory(backup, srcSub); }
                    catch (Exception rex) { log(LogLevel.Error, "  改回失败，请手动处理：" + rex.Message); }
                    throw;
                }

                record.Items.Add(new PartialItemRecord
                {
                    Name = name,
                    JunctionPath = srcSub,
                    TargetPath = tgtSub,
                    BackupPath = backup,
                    Bytes = scan.TotalBytes,
                    Files = scan.FileCount,
                });

                bytesAcc += scan.TotalBytes;
                filesAcc += scan.FileCount;
                record.MigratedBytes = bytesAcc;
                record.MigratedFiles = filesAcc;

                log(LogLevel.Ok, $"  {name} 完成：{srcSub} → {tgtSub}");
            }
        }

        // ---------------- 回滚 ----------------

        public static bool TryRollback(MigrationRecord record, Action<LogLevel, string> log, bool skipFromEngine)
        {
            try
            {
                if (record == null) return false;
                Rollback(record, log, skipFromEngine, persist: false);
                return true;
            }
            catch (Exception ex)
            {
                log(LogLevel.Error, "自动回滚未完成：" + ex.Message);
                return false;
            }
        }

        /// <summary>一键回滚：移除联接、把 .moved-* 改回原名、还原环境变量。</summary>
        public static void Rollback(MigrationRecord record, Action<LogLevel, string> log,
            bool skipProcessCheck = false, bool persist = true)
        {
            if (record == null) throw new InvalidOperationException("没有可用的迁移记录。");
            if (log == null) log = (l, m) => { };

            log(LogLevel.Step, "=== 开始回滚 ===");

            if (!record.IsCompleted)
                throw new InvalidOperationException("这份迁移记录已被回滚过，无需重复操作。");

            if (!skipProcessCheck)
            {
                List<ProcInfo> procs = ProcessGuard.FindWorkBuddyProcesses();
                if (procs.Count > 0)
                {
                    throw new InvalidOperationException(
                        "WorkBuddy 仍在运行（" + string.Join("、", procs.Select(p => p.Name + " PID " + p.Id)) +
                        "），请从托盘完全退出后再回滚。");
                }
            }

            var failed = new List<string>();

            if (string.Equals(record.Mode, "partial", StringComparison.OrdinalIgnoreCase))
            {
                foreach (PartialItemRecord it in Enumerable.Reverse(record.Items ?? new List<PartialItemRecord>()))
                {
                    if (!RollbackOne(it.JunctionPath, it.BackupPath, log)) failed.Add(it.JunctionPath);
                }
            }
            else
            {
                if (!RollbackOne(record.SourcePath, record.BackupPath, log)) failed.Add(record.SourcePath);

                if (string.Equals(record.Mode, "env", StringComparison.OrdinalIgnoreCase))
                {
                    if (failed.Count > 0)
                    {
                        // 目录没还原成功时不动环境变量：它仍指向数据实际所在的位置，比改回去更安全
                        log(LogLevel.Warn, "  目录未还原，环境变量保持现状（仍指向目标目录）。");
                    }
                    else
                    {
                        string name = string.IsNullOrEmpty(record.EnvName) ? WorkBuddyPaths.EnvPrimary : record.EnvName;
                        if (record.EnvOriginalExisted && !string.IsNullOrWhiteSpace(record.EnvOriginalValue))
                        {
                            SetUserEnv(name, record.EnvOriginalValue);
                            log(LogLevel.Ok, $"  环境变量已还原：{name} = {record.EnvOriginalValue}");
                        }
                        else
                        {
                            SetUserEnv(name, null);
                            log(LogLevel.Ok, $"  环境变量已清空：{name}（迁移前未设置）");
                        }
                        BroadcastSettingChange();
                    }
                }
            }

            // 没还原干净就不能标记 rolledback —— 否则记录作废，备份再也没法通过工具恢复
            if (failed.Count > 0)
            {
                throw new InvalidOperationException(
                    "回滚未能完全还原：" + string.Join("、", failed) +
                    "。这些位置已被其它内容占用，或备份目录缺失，工具不会覆盖或删除它们。" +
                    "处理完占用后可再次执行回滚；迁移记录保持为可用状态。");
            }

            record.Status = "rolledback";

            // 迁移失败后的自动还原不写记录，避免覆盖上一次成功迁移的回滚信息
            if (persist)
            {
                try { record.Save(); } catch { }
            }

            log(LogLevel.Ok, "=== 回滚完成 ===");
        }

        /// <summary>还原一个位置。返回 false 表示没能还原（调用方不得把记录标成已回滚）。</summary>
        private static bool RollbackOne(string originalPath, string backupPath, Action<LogLevel, string> log)
        {
            if (string.IsNullOrEmpty(originalPath))
                return true;

            // 1) 移除联接（仅当它确实是联接）
            if (Junction.IsJunction(originalPath))
            {
                Junction.Delete(originalPath);
                log(LogLevel.Ok, "  已移除联接：" + originalPath);
            }
            else if (LongPath.IsDirectory(originalPath) && HasAnyEntry(originalPath))
            {
                log(LogLevel.Error,
                    $"  {originalPath} 已是含数据的实体目录，为避免误删已跳过。请先把它移走或改名，再重试回滚。");
                return false;
            }
            else if (LongPath.Exists(originalPath))
            {
                // 空目录（例如迁移后 WorkBuddy 用旧环境变量又建出来的空壳）会挡住改名，直接清掉
                try
                {
                    Directory.Delete(LongPath.Fix(originalPath));
                    log(LogLevel.Info, "  已清掉空目录：" + originalPath);
                }
                catch (Exception ex)
                {
                    log(LogLevel.Error, $"  空目录无法删除：{originalPath} —— {ex.Message}");
                    return false;
                }
            }

            if (string.IsNullOrEmpty(backupPath))
                return true;

            // 2) 把备份改回原名
            if (!LongPath.Exists(backupPath))
            {
                log(LogLevel.Error, "  备份目录不存在，无法还原：" + backupPath +
                    "（可能已被「清理备份」删除，请从回收站或其它备份恢复）");
                return false;
            }

            if (LongPath.Exists(originalPath))
            {
                log(LogLevel.Error, "  原名位置仍被占用，无法改回：" + originalPath);
                return false;
            }

            MoveDirectory(backupPath, originalPath);
            log(LogLevel.Ok, "  已改回原名：" + backupPath + " → " + originalPath);
            return true;
        }

        // ---------------- 清理备份 ----------------

        /// <summary>
        /// 清理迁移留下的 .moved-* / .bak 备份目录（这是真正释放源盘空间的步骤）。
        /// 只在当前配置确实指向新位置时才允许执行。
        /// </summary>
        public static int CleanupBackups(MigrationRecord record, Action<LogLevel, string> log)
        {
            if (record == null) throw new InvalidOperationException("没有可用的迁移记录。");
            if (log == null) log = (l, m) => { };

            var backups = new List<string>();
            if (!string.IsNullOrEmpty(record.BackupPath)) backups.Add(record.BackupPath);
            foreach (PartialItemRecord it in record.Items ?? new List<PartialItemRecord>())
                if (!string.IsNullOrEmpty(it.BackupPath)) backups.Add(it.BackupPath);

            if (backups.Count == 0)
            {
                log(LogLevel.Info, "没有需要清理的备份目录。");
                return 0;
            }

            AssertMigrationIntact(record);

            int removed = 0;
            foreach (string b in backups)
            {
                if (!LongPath.IsDirectory(b))
                {
                    log(LogLevel.Warn, "跳过（不存在）：" + b);
                    continue;
                }

                if (!WorkBuddyPaths.IsBackupName(Path.GetFileName(LongPath.Normalize(b))))
                {
                    log(LogLevel.Error, "安全保护：拒绝删除非本工具生成的目录：" + b);
                    continue;
                }

                log(LogLevel.Step, "删除备份目录：" + b);
                DeleteDirectoryForCleanup(b);
                removed++;
                log(LogLevel.Ok, "  已删除");
            }

            // 备份全部清理后，回滚已无退路，把记录标记为 cleaned，避免界面继续提供回滚
            if (removed > 0 && removed == backups.Count)
            {
                record.Status = "cleaned";
                try { record.Save(); } catch { }
                log(LogLevel.Info, "全部备份已清理，回滚记录已标记为 cleaned。");
            }

            return removed;
        }

        /// <summary>
        /// 清理备份前先确认迁移仍然完好。状态不一致时禁止清理，避免把唯一的退路删掉。
        /// </summary>
        private static void AssertMigrationIntact(MigrationRecord rec)
        {
            string mode = (rec.Mode ?? "").ToLowerInvariant();

            if (mode == "partial")
            {
                foreach (PartialItemRecord it in rec.Items ?? new List<PartialItemRecord>())
                {
                    if (!Junction.IsJunction(it.JunctionPath))
                        throw new InvalidOperationException(
                            "当前状态与迁移记录不一致：" + it.JunctionPath + " 已不是目录联接。请先一键回滚，不要清理备份。");

                    if (!LongPath.IsDirectory(it.TargetPath))
                        throw new InvalidOperationException(
                            "目标目录不存在：" + it.TargetPath + "（目标盘可能未接入）。请先回滚，不要清理备份。");
                }
            }
            else if (mode == "junction")
            {
                if (!Junction.IsJunction(rec.SourcePath))
                    throw new InvalidOperationException(
                        "当前状态与迁移记录不一致：" + rec.SourcePath + " 已不是目录联接。请先一键回滚，不要清理备份。");

                if (!LongPath.IsDirectory(rec.TargetPath))
                    throw new InvalidOperationException(
                        "目标目录不存在：" + rec.TargetPath + "（目标盘可能未接入）。请先回滚，不要清理备份。");
            }
            else if (mode == "env")
            {
                string name = string.IsNullOrEmpty(rec.EnvName) ? WorkBuddyPaths.EnvPrimary : rec.EnvName;
                string value = WorkBuddyPaths.GetUserEnv(name);

                if (!LongPath.PathEquals(value, rec.TargetPath))
                    throw new InvalidOperationException(
                        name + " 当前值（" + (value ?? "未设置") + "）与迁移记录不一致。请先回滚，不要清理备份。");

                if (!LongPath.IsDirectory(rec.TargetPath))
                    throw new InvalidOperationException(
                        "目标目录不存在：" + rec.TargetPath + "（目标盘可能未接入）。请先回滚，不要清理备份。");
            }
        }

        /// <summary>递归删除（仅用于本工具自己生成、且经过校验的备份 / 中转目录）。</summary>
        internal static void DeleteDirectoryForCleanup(string dir)
        {
            string root = LongPath.Fix(dir);

            // 先清掉目录内的联接与只读属性，避免删除失败
            var stack = new Stack<string>();
            stack.Push(root);
            var dirs = new List<string>();

            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                dirs.Add(cur);

                List<string> items;
                try { items = Directory.EnumerateFileSystemEntries(cur).ToList(); }
                catch { continue; }

                foreach (string item in items)
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(item); }
                    catch { continue; }

                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        // 备份里的联接：只移除联接本身，绝不穿透删除
                        try { NativeMethods.RemoveDirectory(item); } catch { }
                        continue;
                    }

                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        stack.Push(item);
                    }
                    else
                    {
                        try
                        {
                            if ((attr & FileAttributes.ReadOnly) != 0)
                                File.SetAttributes(item, attr & ~FileAttributes.ReadOnly);
                        }
                        catch { }
                    }
                }
            }

            for (int i = dirs.Count - 1; i >= 0; i--)
            {
                try { Directory.Delete(dirs[i], true); }
                catch (Exception ex) { throw new IOException("删除失败：" + LongPath.Unfix(dirs[i]) + " —— " + ex.Message, ex); }
            }
        }

        // ---------------- 工具方法 ----------------

        public static void MoveDirectory(string from, string to)
        {
            string f = LongPath.Fix(from);
            string t = LongPath.Fix(to);

            if (!LongPath.Exists(f))
                throw new DirectoryNotFoundException("源目录不存在：" + LongPath.Unfix(from));
            if (LongPath.Exists(t))
                throw new IOException("目标已存在，拒绝覆盖：" + LongPath.Unfix(to));

            try
            {
                Directory.Move(f, t);
                return;
            }
            catch (IOException)
            {
                if (NativeMethods.MoveFileEx(f, t, NativeMethods.MOVEFILE_WRITE_THROUGH))
                    return;
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                throw new IOException("重命名失败：" + LongPath.Unfix(from) + "（Win32 错误 " + err + "）");
            }
        }

        public static void SetUserEnv(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        }

        public static void BroadcastSettingChange()
        {
            try
            {
                NativeMethods.SendMessageTimeout(
                    NativeMethods.HWND_BROADCAST,
                    NativeMethods.WM_SETTINGCHANGE,
                    IntPtr.Zero,
                    "Environment",
                    NativeMethods.SMTO_ABORTIFHUNG,
                    3000,
                    out _);
            }
            catch { }
        }
    }
}
