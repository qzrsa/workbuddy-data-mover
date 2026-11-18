using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WorkBuddyDataMover.Core
{
    public enum RestoreMode
    {
        /// <summary>原位是目录联接，删联接后把数据放回。</summary>
        Junction = 0,

        /// <summary>数据靠用户级环境变量指向别处，清掉变量后把数据放回。</summary>
        EnvVar = 1
    }

    public class RestorePreflight
    {
        public List<CheckItem> Items { get; set; } = new List<CheckItem>();
        public bool Passed => Items.All(i => i.Ok || !i.Fatal);
        public IEnumerable<CheckItem> Failures => Items.Where(i => !i.Ok && i.Fatal);

        /// <summary>数据现在在哪（例如 D:\WorkBuddyData\.workbuddy）。</summary>
        public string DataDir { get; set; }

        /// <summary>要还原到哪（默认 %USERPROFILE%\.workbuddy）。</summary>
        public string RestoreDir { get; set; }

        /// <summary>中转目录，与 RestoreDir 同卷，用于原子改名。</summary>
        public string TempDir { get; set; }

        public long RequiredBytes { get; set; }
        public int RequiredFiles { get; set; }
        public RestoreMode Mode { get; set; }
        public string EnvName { get; set; }
    }

    public class RestoreResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public long Bytes { get; set; }
        public int Files { get; set; }
        public TimeSpan Elapsed { get; set; }
        public MigrationRecord Record { get; set; }
        public bool RecordSaved { get; set; } = true;
        public string RecordSaveError { get; set; }

        /// <summary>数据原来所在位置（还原成功后可以自行删除）。</summary>
        public string DataDir { get; set; }
    }

    /// <summary>
    /// 反向迁移：把已经搬到别的盘的数据目录搬回默认位置。
    ///
    /// 安全设计的核心是"先复制到同盘中转目录、校验通过后再原子改名"：
    ///   1. 复制到 %USERPROFILE%\.workbuddy.restoring-&lt;时间&gt;
    ///   2. 逐文件校验
    ///   3. 删联接（或清环境变量）
    ///   4. 中转目录改名成 .workbuddy（同卷改名是原子操作，不存在"改了一半"）
    ///
    /// 因此第 1、2 步失败时重定向完全没动过；第 4 步失败则重建重定向，退回搬回之前的状态。
    /// </summary>
    public static class RestoreEngine
    {
        private const long SpaceHeadroomBytes = 64L * 1024 * 1024;

        // ---------------- 预检 ----------------

        public static RestorePreflight Preflight(
            string restoreDir = null,
            string dataDir = null,
            bool skipProcessCheck = false,
            Action<LogLevel, string> log = null)
        {
            var result = new RestorePreflight();

            void Add(string name, bool ok, string detail, bool fatal = true)
                => result.Items.Add(new CheckItem { Name = name, Ok = ok, Detail = detail, Fatal = fatal });

            // 1) 还原位置：只看默认路径，绝不被环境变量带走
            string restore = LongPath.Unfix(string.IsNullOrWhiteSpace(restoreDir)
                ? WorkBuddyPaths.DefaultConfigDir
                : restoreDir).TrimEnd('\\', '/');

            if (string.IsNullOrWhiteSpace(restore) || restore.Length < 3)
            {
                Add("还原位置", false, "还原位置无效：" + restore);
                return result;
            }

            result.RestoreDir = restore;

            // 2) 先看清当前是什么状态，再决定数据来源
            bool restoreIsJunction = Junction.IsJunction(restore);
            string junctionTarget = null;
            if (restoreIsJunction) Junction.TryGetTarget(restore, out junctionTarget);

            string envName = WorkBuddyPaths.EnvPrimary;
            string envValue = WorkBuddyPaths.GetUserEnv(envName);
            if (string.IsNullOrWhiteSpace(envValue))
            {
                string sec = WorkBuddyPaths.GetUserEnv(WorkBuddyPaths.EnvSecondary);
                if (!string.IsNullOrWhiteSpace(sec)) { envName = WorkBuddyPaths.EnvSecondary; envValue = sec; }
            }

            // 数据来源：显式指定优先，否则按当前重定向状态推导
            string derived;
            if (!string.IsNullOrWhiteSpace(dataDir)) derived = dataDir;
            else if (restoreIsJunction && !string.IsNullOrWhiteSpace(junctionTarget)) derived = junctionTarget;
            else if (!string.IsNullOrWhiteSpace(envValue)) derived = envValue;
            else derived = restore;

            string data = LongPath.Unfix(derived ?? "").TrimEnd('\\', '/');
            result.DataDir = data;

            // 3) 模式判定
            if (restoreIsJunction)
            {
                if (!LongPath.PathEquals(junctionTarget, data))
                {
                    Add("迁移方式", false,
                        "原位是目录联接，但指向 " + (junctionTarget ?? "（无法读取）") +
                        "，与推导出的数据目录 " + data + " 不一致。为避免误操作已拒绝。");
                    return result;
                }

                result.Mode = RestoreMode.Junction;
                Add("迁移方式", true, "目录联接 → " + junctionTarget + "（搬回后变回实体目录）");
            }
            else if (LongPath.Exists(restore))
            {
                if (LongPath.IsDirectory(restore))
                {
                    Add("迁移方式", false,
                        "默认位置已经是实体目录（" + restore + "），不是联接；数据可能本来就在原处。" +
                        "为避免覆盖其中的数据已拒绝，请先确认它是什么。");
                }
                else
                {
                    Add("迁移方式", false, "默认位置已存在一个同名文件，无法作为目录使用：" + restore);
                }
                return result;
            }
            else if (!string.IsNullOrWhiteSpace(envValue) && LongPath.PathEquals(envValue, data))
            {
                result.Mode = RestoreMode.EnvVar;
                result.EnvName = envName;
                Add("迁移方式", true, envName + " → " + data + "（搬回后清空该变量）");
            }
            else
            {
                Add("迁移方式", false,
                    "默认位置既不是联接，环境变量也没有指向数据目录，" +
                    "看不出数据是怎么迁出去的，已拒绝自动搬回。");
                return result;
            }

            // 4) 数据来源必须真实存在、且本身不是联接
            if (!LongPath.IsDirectory(data))
            {
                Add("数据位置", false, "数据目录不存在或不是目录：" + data);
                return result;
            }

            if (Junction.IsJunction(data))
            {
                Add("数据位置", false, "数据目录本身也是目录联接，存在多层重定向，请先理清：" + data);
                return result;
            }

            if (LongPath.PathEquals(restore, data))
            {
                Add("数据位置", false, "数据已经就在默认位置，无需搬回。");
                return result;
            }

            if (LongPath.IsSameOrUnder(data, restore) || LongPath.IsSameOrUnder(restore, data))
            {
                Add("数据位置", false, "还原位置与数据位置互相包含，无法安全搬回。");
                return result;
            }

            Add("数据位置", true, data);

            // 5) 中转目录（必须与还原位置同卷，否则改名不是原子操作）
            string temp = null;
            for (int i = 0; i < 20; i++)
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + (i == 0 ? "" : "-" + (i + 1));
                string candidate = restore + ".restoring-" + stamp;
                if (!LongPath.Exists(candidate)) { temp = candidate; break; }
            }

            if (temp == null)
            {
                Add("中转目录", false, "无法为中转目录找到可用名字，请稍后重试。");
                return result;
            }

            result.TempDir = temp;

            string restoreVolume = null, tempVolume = null;
            try { restoreVolume = Path.GetPathRoot(restore); } catch { }
            try { tempVolume = Path.GetPathRoot(temp); } catch { }

            if (string.IsNullOrEmpty(restoreVolume) ||
                !string.Equals(LongPath.Normalize(restoreVolume), LongPath.Normalize(tempVolume ?? ""),
                    StringComparison.OrdinalIgnoreCase))
            {
                Add("中转目录", false, "中转目录与还原位置不在同一个卷，无法原子改名。");
                return result;
            }

            Add("中转目录", true, temp + "（与还原位置同卷，改名是原子操作）", false);

            // 6) 空间
            DirStat stat = ProcessGuard.MeasureDirectory(data);
            result.RequiredBytes = stat.Bytes;
            result.RequiredFiles = stat.Files;

            DriveOption drive = Disks.GetDriveOf(restore);
            if (drive == null)
            {
                Add("还原盘", false, "还原位置所在盘未就绪：" + restore);
                return result;
            }

            if (drive.IsNetwork)
            {
                Add("还原盘", false, "还原位置不能是网络驱动器。");
                return result;
            }

            if (!drive.SupportsJunction)
            {
                Add("还原盘", false, $"还原位置所在盘为 {drive.Format}，目录联接需要 NTFS / ReFS。");
                return result;
            }

            long need = result.RequiredBytes + SpaceHeadroomBytes;
            if (drive.FreeBytes < need)
            {
                Add("还原盘空间", false,
                    $"空间不足：需要 {Fmt.Bytes(need)}（数据 {Fmt.Bytes(result.RequiredBytes)} + 64MB 余量），" +
                    $"可用 {Fmt.Bytes(drive.FreeBytes)}。");
            }
            else
            {
                Add("还原盘空间", true,
                    $"需要 {Fmt.Bytes(need)}，可用 {Fmt.Bytes(drive.FreeBytes)}" +
                    "（中转副本校验通过后才会删除联接）。");
            }

            // 7) 进程占用
            if (skipProcessCheck)
            {
                Add("进程占用", true, "已跳过（自检模式）。", false);
            }
            else
            {
                List<ProcInfo> procs = ProcessGuard.FindWorkBuddyProcesses();
                if (procs.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append("检测到 WorkBuddy 相关进程仍在运行：");
                    foreach (ProcInfo p in procs.Take(6))
                        sb.Append(" | ").Append(p.Name).Append(" (PID ").Append(p.Id).Append(")");
                    sb.Append("。搬回过程会删除联接，必须先完全退出。");
                    Add("进程占用", false, sb.ToString());
                }
                else
                {
                    Add("进程占用", true, "未检测到 WorkBuddy 进程。");
                }
            }

            if (log != null)
            {
                foreach (CheckItem it in result.Items)
                    log(it.Ok ? LogLevel.Ok : (it.Fatal ? LogLevel.Error : LogLevel.Warn), it.ToString());
            }

            return result;
        }

        // ---------------- 执行 ----------------

        public static RestoreResult Execute(
            RestorePreflight pf,
            Action<LogLevel, string> log,
            Action<int, long, long> progress = null,
            Func<bool> cancelled = null,
            string recordPath = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new RestoreResult { Success = false };

            if (log == null) log = (l, m) => { };
            if (pf == null) { result.Error = "缺少预检结果。"; return result; }

            result.DataDir = pf.DataDir;

            if (!pf.Passed)
            {
                result.Error = "预检未通过：" + string.Join("；", pf.Failures.Select(f => f.Name + "：" + f.Detail));
                return result;
            }

            string dataDir = pf.DataDir;
            string restoreDir = pf.RestoreDir;
            string tempDir = pf.TempDir;

            var record = new MigrationRecord
            {
                Mode = "restore",
                SourcePath = restoreDir,
                TargetPath = dataDir,
                TimestampLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Status = "completed",
            };
            result.Record = record;

            bool redirectRemoved = false;

            try
            {
                log(LogLevel.Step, "=== 开始搬回默认位置 ===");
                log(LogLevel.Info, "数据来源：" + dataDir);
                log(LogLevel.Info, "还原到：  " + restoreDir);
                log(LogLevel.Info, "中转目录：" + tempDir);

                // 1) 扫描
                log(LogLevel.Step, "1/5 扫描数据目录…");
                ScanResult scan = FileCopier.Scan(dataDir, cancelled);
                FileCopier.ThrowIfScanFailed(scan, "数据目录");
                log(LogLevel.Info, $"  文件 {Fmt.Count(scan.FileCount)}，目录 {Fmt.Count(scan.DirCount)}，" +
                                   $"总大小 {Fmt.Bytes(scan.TotalBytes)}");

                if (scan.SkippedReparse > 0)
                {
                    log(LogLevel.Warn, $"  注意：数据目录内有 {scan.SkippedReparse} 个联接/符号链接，按设计不复制：");
                    foreach (string rp in scan.ReparsePaths.Take(5)) log(LogLevel.Warn, "    · " + rp);
                }

                if (cancelled != null && cancelled()) throw new OperationCanceledException("操作已取消。");

                // 2) 复制到中转目录
                log(LogLevel.Step, "2/5 复制到同盘中转目录（此步失败不影响现有重定向）…");
                FileCopier.Copy(dataDir, tempDir, scan, false,
                    (s, cur) =>
                    {
                        if (progress != null)
                        {
                            long total = Math.Max(1, scan.TotalBytes);
                            int pct = (int)Math.Min(100, s.BytesCopied * 100 / total);
                            progress(pct, s.BytesCopied, scan.TotalBytes);
                        }
                    }, cancelled);

                if (cancelled != null && cancelled()) throw new OperationCanceledException("操作已取消。");

                // 3) 校验
                log(LogLevel.Step, "3/5 逐文件校验中转副本…");
                FileCopier.VerifyStrict(tempDir, scan);
                log(LogLevel.Ok, $"  校验通过：{Fmt.Count(scan.FileCount)} 个文件 / {Fmt.Bytes(scan.TotalBytes)} 全部就位");

                // 4) 撤掉重定向
                log(LogLevel.Step, "4/5 撤掉原位置的重定向…");
                if (pf.Mode == RestoreMode.Junction)
                {
                    Junction.Delete(restoreDir);   // 内部校验"确实是联接"，绝不递归删除
                    redirectRemoved = true;
                    log(LogLevel.Ok, "  已移除目录联接（目标数据未受影响）");
                }
                else
                {
                    MigrationEngine.SetUserEnv(pf.EnvName, null);
                    if (!string.IsNullOrWhiteSpace(WorkBuddyPaths.GetUserEnv(pf.EnvName)))
                        throw new IOException("环境变量未能清空：" + pf.EnvName);
                    MigrationEngine.BroadcastSettingChange();
                    redirectRemoved = true;
                    log(LogLevel.Ok, "  已清空环境变量 " + pf.EnvName);
                }

                // 5) 原子改名
                log(LogLevel.Step, "5/5 把中转目录改名到正式位置（同卷原子操作）…");
                if (LongPath.Exists(restoreDir))
                    throw new IOException("正式位置已被占用，拒绝覆盖：" + restoreDir);

                MigrationEngine.MoveDirectory(tempDir, restoreDir);
                log(LogLevel.Ok, "  已就位：" + restoreDir);

                record.MigratedBytes = scan.TotalBytes;
                record.MigratedFiles = scan.FileCount;

                // 旧记录另存一份，避免"原来迁到哪"的信息被覆盖后无从追溯
                // （自检用临时记录路径时不做这一步，不能碰真实的记录目录）
                if (recordPath == null)
                {
                    try
                    {
                        string prev = WorkBuddyPaths.LastRecordPath;
                        if (File.Exists(prev))
                        {
                            string bak = Path.Combine(WorkBuddyPaths.RecordDirectory,
                                "last-migration.pre-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                            File.Copy(prev, bak, true);
                        }
                    }
                    catch { }
                }

                try
                {
                    record.Save(recordPath);
                }
                catch (Exception sex)
                {
                    result.RecordSaved = false;
                    result.RecordSaveError = sex.Message;
                    log(LogLevel.Error, "搬回已完成，但记录写入失败：" + sex.Message);
                }

                result.Success = true;
                result.Bytes = scan.TotalBytes;
                result.Files = scan.FileCount;

                log(LogLevel.Ok, $"=== 搬回完成，用时 {Fmt.Duration(sw.Elapsed)} ===");
                log(LogLevel.Warn, "原数据仍完整保留在：" + dataDir);
                log(LogLevel.Warn, "请先启动 WorkBuddy 确认正常，再自行决定是否删除它。");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                log(LogLevel.Error, "搬回失败：" + ex.Message);

                if (redirectRemoved)
                {
                    log(LogLevel.Warn, "  重定向已被撤掉，正在恢复回去…");
                    try
                    {
                        if (pf.Mode == RestoreMode.Junction)
                        {
                            // 正式位置可能被留了个空壳，先清掉再建联接
                            if (LongPath.IsDirectory(restoreDir) && !Junction.IsJunction(restoreDir) && IsEmptyDir(restoreDir))
                                Directory.Delete(LongPath.Fix(restoreDir));

                            if (!LongPath.Exists(restoreDir))
                            {
                                Junction.Create(restoreDir, dataDir);
                                log(LogLevel.Ok, "  已重建联接：" + restoreDir + " → " + dataDir);
                            }
                            else
                            {
                                log(LogLevel.Error, "  正式位置已被占用，无法重建联接，请手动处理。");
                            }
                        }
                        else
                        {
                            MigrationEngine.SetUserEnv(pf.EnvName, dataDir);
                            MigrationEngine.BroadcastSettingChange();
                            log(LogLevel.Ok, "  已恢复环境变量：" + pf.EnvName + " = " + dataDir);
                        }
                    }
                    catch (Exception rex)
                    {
                        log(LogLevel.Error, "  恢复重定向失败：" + rex.Message);
                        log(LogLevel.Error, "  数据不会丢，但需要手动处理：");
                        log(LogLevel.Error, "    中转副本：" + (LongPath.IsDirectory(tempDir) ? tempDir : "（已并入正式位置或不存在）"));
                        log(LogLevel.Error, "    原始数据：" + dataDir);
                    }
                }
                else
                {
                    log(LogLevel.Info, "  现有联接/环境变量未被改动，数据仍在原位。");
                }
            }
            finally
            {
                result.Elapsed = sw.Elapsed;
            }

            return result;
        }

        private static bool IsEmptyDir(string dir)
        {
            try
            {
                using (var e = Directory.EnumerateFileSystemEntries(LongPath.Fix(dir)).GetEnumerator())
                    return !e.MoveNext();
            }
            catch { return false; }
        }

        /// <summary>清理失败时留下的中转目录（只删本工具命名的）。</summary>
        public static bool CleanupTemp(string tempDir, Action<LogLevel, string> log)
        {
            if (string.IsNullOrWhiteSpace(tempDir)) return false;
            if (!LongPath.IsDirectory(tempDir)) return true;

            string name = Path.GetFileName(LongPath.Normalize(tempDir));
            if (name.IndexOf(".restoring-", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (log != null) log(LogLevel.Error, "安全保护：拒绝删除非本工具生成的中转目录：" + tempDir);
                return false;
            }

            try
            {
                MigrationEngine.DeleteDirectoryForCleanup(tempDir);
                if (log != null) log(LogLevel.Ok, "已清理中转目录：" + tempDir);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log(LogLevel.Error, "中转目录清理失败：" + ex.Message);
                return false;
            }
        }
    }
}
