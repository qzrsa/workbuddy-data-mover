using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WorkBuddyDataMover.Core
{
    /// <summary>
    /// 内置环境自检：全部在系统临时目录的沙箱里跑，绝不触碰任何真实数据目录。
    /// </summary>
    public static class SelfTest
    {
        private static int _failed;
        private static int _total;
        private static Action<string> _log;

        public static int Run(Action<string> log)
        {
            _log = log ?? (s => { });
            _failed = 0;
            _total = 0;

            string sandbox = Path.Combine(Path.GetTempPath(), "WorkBuddyDataMoverSelftest",
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(LongPath.Fix(sandbox));

            _log("========== WorkBuddy 数据目录迁移工具 · 环境自检 ==========");
            _log("沙箱目录：" + sandbox);
            _log("说明：所有用例只在沙箱内创建临时数据，不读取、不修改真实数据目录。");
            _log("");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Case0(sandbox);
                Case1(sandbox);
                Case2(sandbox);
                Case3(sandbox);
                Case4(sandbox);
                Case4Cleanup(sandbox);
                Case4FullJunction(sandbox);
                Case4EnvVar(sandbox);
                Case6(sandbox);
                Case7(sandbox);
                Case8(sandbox);
                Case9(sandbox);
                Case10(sandbox);
                Case11(sandbox);
                Case12(sandbox);
            }
            catch (Exception ex)
            {
                _failed++;
                _log("[异常] 自检流程中断：" + ex);
            }
            finally
            {
                try { TryDeleteTree(sandbox); } catch { }
                try
                {
                    string parent = Path.GetDirectoryName(sandbox);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) &&
                        !Directory.EnumerateFileSystemEntries(parent).Any())
                        Directory.Delete(parent);
                }
                catch { }
            }

            sw.Stop();
            _log("");
            _log($"========== 自检结束：{_total - _failed}/{_total} 项通过，用时 {Fmt.Duration(sw.Elapsed)} ==========");
            if (_failed > 0) _log($"存在 {_failed} 项失败，请把上面的日志反馈给开发者。");
            else _log("全部通过。");

            return _failed;
        }

        // ---------------- 断言 ----------------

        private static void Check(string name, bool ok, string detail = "")
        {
            _total++;
            if (!ok) _failed++;
            _log((ok ? "  [通过] " : "  [失败] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  —— " + detail));
        }

        private static void Group(string title)
        {
            _log("");
            _log("· " + title);
        }

        // ---------------- 用例 0 ----------------

        private static void Case0(string sandbox)
        {
            Group("用例 0：联接重解析数据布局穷举（4 种布局 × 3 种句柄标志）");

            string root = Path.Combine(sandbox, "case0");
            Directory.CreateDirectory(LongPath.Fix(root));

            var layouts = new List<string>
            {
                "目标 目录A",                          // 中文 + 空格
                "Target With Spaces",                  // 纯空格
                Path.Combine("深层", "嵌套", "路径"),   // 中文多级
                "a",                                   // 极短名
            };

            var flagsets = new List<(string Name, uint Access, uint Flags)>
            {
                ("READ_ATTRIBUTES+BACKUP_SEMANTICS",
                    NativeMethods.FILE_READ_ATTRIBUTES,
                    NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT | NativeMethods.FILE_FLAG_BACKUP_SEMANTICS),
                ("GENERIC_READ+BACKUP_SEMANTICS",
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT | NativeMethods.FILE_FLAG_BACKUP_SEMANTICS),
                ("0 access+BACKUP_SEMANTICS",
                    0u,
                    NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT | NativeMethods.FILE_FLAG_BACKUP_SEMANTICS),
            };

            int idx = 0;
            foreach (string layout in layouts)
            {
                idx++;
                string targetAbs = Path.Combine(root, "targets", layout);
                Directory.CreateDirectory(LongPath.Fix(targetAbs));

                string link = Path.Combine(root, "link" + idx);
                bool created = false;
                string err = null;

                try
                {
                    Junction.Create(link, targetAbs);
                    created = true;
                }
                catch (Exception ex) { err = ex.Message; }

                Check($"布局「{layout}」创建联接", created, err ?? "");

                if (created)
                {
                    foreach (var fs in flagsets)
                    {
                        bool ok = Junction.TryRead(link, fs.Access, 7, fs.Flags, out string read);
                        Check($"  读回（{fs.Name}）", ok && LongPath.PathEquals(read, targetAbs),
                            ok ? read : "读取失败");
                    }

                    try { Junction.Delete(link); Check("  移除联接", !Junction.IsJunction(link)); }
                    catch (Exception ex) { Check("  移除联接", false, ex.Message); }
                }
            }
        }

        // ---------------- 用例 1 ----------------

        private static void Case1(string sandbox)
        {
            Group("用例 1：目录联接 创建 / 读取 / 穿透读写 / 移除");

            string root = Path.Combine(sandbox, "case1");
            string target = Path.Combine(root, "real");
            string link = Path.Combine(root, "link");
            Directory.CreateDirectory(LongPath.Fix(target));

            try
            {
                Junction.Create(link, target);
                Check("创建联接", Junction.IsJunction(link));
                Check("读取目标", Junction.TryGetTarget(link, out string t) && LongPath.PathEquals(t, target), t ?? "");

                // 通过联接路径写入，应在真实目标上出现
                string probe = Path.Combine(link, "hello.txt");
                File.WriteAllText(LongPath.Fix(probe), "穿透写入", new UTF8Encoding(false));

                string real = Path.Combine(target, "hello.txt");
                Check("穿透写：目标出现文件", File.Exists(LongPath.Fix(real)));
                Check("穿透读：内容一致",
                    File.Exists(LongPath.Fix(real)) &&
                    File.ReadAllText(LongPath.Fix(real), Encoding.UTF8) == "穿透写入");

                // 通过联接路径读取
                Check("经联接路径读回", File.Exists(LongPath.Fix(probe)));

                Junction.Delete(link);
                Check("移除联接后不再是联接", !Junction.IsJunction(link));
                Check("移除联接后目标数据保留", File.Exists(LongPath.Fix(real)));
            }
            catch (Exception ex)
            {
                Check("用例 1 执行", false, ex.Message);
            }
        }

        // ---------------- 用例 2 ----------------

        private static void Case2(string sandbox)
        {
            Group("用例 2：非递归删除保护（真实目录必须被拒绝）");

            string root = Path.Combine(sandbox, "case2");
            string realDir = Path.Combine(root, "this-is-a-real-dir");
            string file = Path.Combine(realDir, "keep.txt");
            Directory.CreateDirectory(LongPath.Fix(realDir));
            File.WriteAllText(LongPath.Fix(file), "必须保留", new UTF8Encoding(false));

            bool threw = false;
            string msg = "";
            try { Junction.Delete(realDir); }
            catch (InvalidOperationException ex) { threw = true; msg = ex.Message; }
            catch (Exception ex) { msg = ex.GetType().Name + "：" + ex.Message; }

            Check("拒绝删除真实目录（抛出安全保护异常）", threw, msg);
            Check("真实目录内容未被删除", File.Exists(LongPath.Fix(file)));

            // 悬空联接也必须能被识别并安全移除
            string dangling = Path.Combine(root, "dangling");
            string ghost = Path.Combine(root, "ghost-target");
            Directory.CreateDirectory(LongPath.Fix(ghost));
            try
            {
                Junction.Create(dangling, ghost);
                Directory.Delete(LongPath.Fix(ghost), true); // 目标没了，联接悬空
                Check("悬空联接仍可读取目标", Junction.TryGetTarget(dangling, out _));
                Junction.Delete(dangling);
                Check("悬空联接可安全移除", !LongPath.Exists(dangling));
            }
            catch (Exception ex)
            {
                Check("悬空联接处理", false, ex.Message);
            }
        }

        // ---------------- 用例 3 ----------------

        private static void Case3(string sandbox)
        {
            Group("用例 3：长路径复制 + 重解析点跳过（实测 416 字符路径）");

            string root = Path.Combine(sandbox, "case3");
            string deep = Path.Combine(root, "deep");
            Directory.CreateDirectory(LongPath.Fix(deep));

            string cur = deep;
            for (int i = 0; i < 9; i++)
            {
                cur = Path.Combine(cur, "seg" + i.ToString("00") + new string('x', 36));
                Directory.CreateDirectory(LongPath.Fix(cur));
            }

            Check($"构造出超过 416 字符的路径（实测 {cur.Length} 字符）", cur.Length > 416, cur.Length + " 字符");

            File.WriteAllText(LongPath.Fix(Path.Combine(cur, "deep1.txt")), "深层文件1", new UTF8Encoding(false));
            File.WriteAllText(LongPath.Fix(Path.Combine(cur, "deep2.txt")), "深层文件2", new UTF8Encoding(false));

            // 在源里放一个联接（指向含 5 个文件的目录），扫描时必须被跳过
            string sideTarget = Path.Combine(root, "side-target");
            Directory.CreateDirectory(LongPath.Fix(sideTarget));
            for (int i = 0; i < 5; i++)
                File.WriteAllText(LongPath.Fix(Path.Combine(sideTarget, "s" + i + ".txt")), "side" + i, new UTF8Encoding(false));

            string linkInside = Path.Combine(deep, "linked");
            Junction.Create(linkInside, sideTarget);

            ScanResult scan = FileCopier.Scan(deep);
            Check("扫描跳过重解析点", scan.SkippedReparse >= 1, "跳过 " + scan.SkippedReparse + " 个");
            Check("扫描文件数 = 2（未把联接内容算进来）", scan.FileCount == 2, "实际 " + scan.FileCount);
            Check("被跳过的联接连同相对路径一起留痕（可告知用户）",
                scan.ReparsePaths.Any(p => p.EndsWith("linked", StringComparison.OrdinalIgnoreCase)),
                string.Join("、", scan.ReparsePaths));

            string target = Path.Combine(root, "copy-target");
            CopyStats stats = FileCopier.Copy(deep, target, scan, false);
            Check("长路径复制成功", stats.FilesCopied == 2, "复制 " + stats.FilesCopied + " 个");

            string relDeep = cur.Substring(deep.Length).TrimStart('\\');
            string copiedDeep1 = Path.Combine(target, relDeep, "deep1.txt");
            Check("深层文件已就位", File.Exists(LongPath.Fix(copiedDeep1)), copiedDeep1);

            try { Junction.Delete(linkInside); } catch { }
        }

        // ---------------- 用例 4 ----------------

        private static void Case4(string sandbox)
        {
            Group("用例 4：迁移引擎端到端（部分迁移 + 回滚还原）");

            string root = Path.Combine(sandbox, "case4");
            string src = Path.Combine(root, "src", ".workbuddy");
            string dst = Path.Combine(root, "dst");

            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "logs", "sub")));
            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "projects")));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "logs", "a.log")), "log-a", new UTF8Encoding(false));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "logs", "sub", "b.log")), "log-b", new UTF8Encoding(false));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "projects", "keep.json")), "{\"k\":1}", new UTF8Encoding(false));

            string recordPath = Path.Combine(root, "record.json");

            var opt = new MigrationOptions
            {
                SourcePath = src,
                TargetRoot = dst,
                Mode = MigrateMode.Partial,
                MergeMode = false,
                PartialNames = new List<string> { "logs" },
                SkipProcessCheck = true,
                RecordPath = recordPath,
                RequiredBytes = ProcessGuard.MeasureDirectory(Path.Combine(src, "logs")).Bytes,
            };

            MigrationResult res = MigrationEngine.Execute(opt, (l, m) => { });
            Check("迁移返回成功", res.Success, res.Error ?? "");
            if (!res.Success) return;

            string srcLogs = Path.Combine(src, "logs");
            string tgtLogs = Path.Combine(dst, ".workbuddy", "logs");

            Check("原位置已变成目录联接", Junction.IsJunction(srcLogs));
            Check("联接指向目标",
                Junction.TryGetTarget(srcLogs, out string t) && LongPath.PathEquals(t, tgtLogs), t ?? "");
            Check("目标文件已就位", File.Exists(LongPath.Fix(Path.Combine(tgtLogs, "a.log"))));
            Check("备份目录已生成（未删除）", res.BackupPaths.Any(p => Directory.Exists(LongPath.Fix(p))));
            Check("回滚记录已写入", File.Exists(recordPath));
            Check("未勾选的 projects 未受影响", Directory.Exists(LongPath.Fix(Path.Combine(src, "projects")))
                && !Junction.IsJunction(Path.Combine(src, "projects")));

            MigrationRecord rec = MigrationRecord.Load(recordPath);
            Check("记录可反序列化", rec != null && rec.Mode == "partial" && rec.Items.Count == 1);

            if (rec != null)
            {
                try
                {
                    MigrationEngine.Rollback(rec, (l, m) => { }, skipProcessCheck: true);
                    Check("回滚后原位置恢复为实体目录",
                        LongPath.IsDirectory(srcLogs) && !Junction.IsJunction(srcLogs));
                    Check("回滚后文件回来了", File.Exists(LongPath.Fix(Path.Combine(srcLogs, "a.log"))));
                    Check("回滚后记录状态为 rolledback",
                        (MigrationRecord.Load(recordPath)?.Status ?? "") == "rolledback");
                }
                catch (Exception ex)
                {
                    Check("回滚执行", false, ex.Message);
                }
            }
        }

        // ---------------- 用例 4b ----------------

        private static void Case4Cleanup(string sandbox)
        {
            Group("用例 4b：清理备份（删备份 → 记录标记 cleaned → 安全保护生效）");

            string root = Path.Combine(sandbox, "case4b");
            string src = Path.Combine(root, "src", ".workbuddy");
            string dst = Path.Combine(root, "dst");

            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "logs")));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "logs", "x.log")), "x", new UTF8Encoding(false));

            string recPath = Path.Combine(root, "record.json");
            var opt = new MigrationOptions
            {
                SourcePath = src,
                TargetRoot = dst,
                Mode = MigrateMode.Partial,
                PartialNames = new List<string> { "logs" },
                SkipProcessCheck = true,
                RecordPath = recPath,
                RequiredBytes = 1,
            };

            MigrationResult res = MigrationEngine.Execute(opt, (l, m) => { });
            Check("清理前置：迁移成功", res.Success, res.Error ?? "");
            if (!res.Success) return;

            string backup = res.BackupPaths.FirstOrDefault();
            Check("备份目录存在（清理前）", backup != null && Directory.Exists(LongPath.Fix(backup)));

            // 先验证安全保护：状态与记录不一致时必须拒绝清理
            string fakeBackup = Path.Combine(root, "fake.moved-20260101-000000");
            Directory.CreateDirectory(LongPath.Fix(fakeBackup));
            File.WriteAllText(LongPath.Fix(Path.Combine(fakeBackup, "keep.txt")), "keep", new UTF8Encoding(false));

            var fake = new MigrationRecord
            {
                Mode = "junction",
                SourcePath = Path.Combine(root, "no-such-source"),
                TargetPath = Path.Combine(root, "no-such-target"),
                BackupPath = fakeBackup,
            };

            bool refused = false;
            try { MigrationEngine.CleanupBackups(fake, (l, m) => { }); }
            catch (InvalidOperationException) { refused = true; }
            catch { }

            Check("状态不一致时拒绝清理", refused);
            Check("被拒绝时未删除备份", Directory.Exists(LongPath.Fix(fakeBackup)));

            // 正常清理
            MigrationRecord rec = MigrationRecord.Load(recPath);
            int removed = MigrationEngine.CleanupBackups(rec, (l, m) => { });

            Check("清理成功 1 个", removed == 1, "实际 " + removed);
            Check("备份目录已删除", !Directory.Exists(LongPath.Fix(backup)));
            Check("清理不影响联接（联接仍存在）", Junction.IsJunction(Path.Combine(src, "logs")));
            Check("迁移数据仍可通过联接读取",
                File.Exists(LongPath.Fix(Path.Combine(src, "logs", "x.log"))));
            Check("记录已标记为 cleaned", (MigrationRecord.Load(recPath)?.Status ?? "") == "cleaned");
        }

        // ---------------- 用例 4c：整体迁移（目录联接）----------------

        private static void Case4FullJunction(string sandbox)
        {
            Group("用例 4c：整体迁移（目录联接方式）端到端 + 预检拦截 + 回滚");

            string root = Path.Combine(sandbox, "case4c");
            string src = Path.Combine(root, "src", ".workbuddy");
            string dst = Path.Combine(root, "dst");

            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "logs")));
            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "projects")));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "logs", "a.log")), "A", new UTF8Encoding(false));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "projects", "p.json")), "{\"p\":1}", new UTF8Encoding(false));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "workbuddy.db")), "DB", new UTF8Encoding(false));

            string recPath = Path.Combine(root, "record.json");
            var opt = new MigrationOptions
            {
                SourcePath = src,
                TargetRoot = dst,
                Mode = MigrateMode.Junction,
                SkipProcessCheck = true,
                RecordPath = recPath,
            };

            PreflightResult pf = MigrationEngine.Preflight(opt);
            Check("预检通过（跳过进程检测）", pf.Passed,
                pf.Passed ? pf.TargetDir : string.Join("；", pf.Failures.Select(f => f.Name + "：" + f.Detail)));
            Check("预检推导出正确目标目录",
                LongPath.PathEquals(pf.TargetDir, Path.Combine(dst, ".workbuddy")), pf.TargetDir ?? "");

            MigrationResult res = MigrationEngine.Execute(opt, (l, m) => { });
            Check("整体迁移成功", res.Success, res.Error ?? "");

            if (res.Success)
            {
                Check("原位置已变成目录联接", Junction.IsJunction(src));
                Check("联接指向目标", Junction.TryGetTarget(src, out string t) &&
                    LongPath.PathEquals(t, Path.Combine(dst, ".workbuddy")), t ?? "");
                Check("子目录数据已搬迁", File.Exists(LongPath.Fix(Path.Combine(src, "logs", "a.log"))));
                Check("根目录散落文件已搬迁", File.Exists(LongPath.Fix(Path.Combine(src, "workbuddy.db"))));
                Check("备份目录已生成", Directory.Exists(LongPath.Fix(res.Record.BackupPath ?? "")));
                Check("记录方式为 junction", res.Record.Mode == "junction");

                // 已迁移后再预检，必须被拦截
                PreflightResult again = MigrationEngine.Preflight(opt);
                Check("已迁移状态下预检阻止重复迁移", !again.Passed);

                MigrationRecord rec = MigrationRecord.Load(recPath);
                try
                {
                    MigrationEngine.Rollback(rec, (l, m) => { }, skipProcessCheck: true);
                    Check("回滚后不再是联接", !Junction.IsJunction(src));
                    Check("回滚后数据回到原目录", File.Exists(LongPath.Fix(Path.Combine(src, "projects", "p.json"))));
                    Check("回滚后目标数据保留（未删除）",
                        File.Exists(LongPath.Fix(Path.Combine(dst, ".workbuddy", "logs", "a.log"))));
                }
                catch (Exception ex)
                {
                    Check("回滚执行", false, ex.Message);
                }
            }
        }

        // ---------------- 用例 4d：环境变量方式 ----------------

        private static void Case4EnvVar(string sandbox)
        {
            Group("用例 4d：环境变量方式端到端 + 还原（使用临时变量名，不动真实配置）");

            string envName = "WBDM_SELFTEST_ENV_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string root = Path.Combine(sandbox, "case4d");
            string src = Path.Combine(root, "src", ".workbuddy");
            string dst = Path.Combine(root, "dst");

            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "logs")));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "logs", "e.log")), "E", new UTF8Encoding(false));

            string recPath = Path.Combine(root, "record.json");

            try
            {
                var opt = new MigrationOptions
                {
                    SourcePath = src,
                    TargetRoot = dst,
                    Mode = MigrateMode.EnvVar,
                    SkipProcessCheck = true,
                    SkipSettingsBroadcast = true,
                    EnvVariableName = envName,
                    RecordPath = recPath,
                };

                MigrationResult res = MigrationEngine.Execute(opt, (l, m) => { });
                Check("环境变量方式迁移成功", res.Success, res.Error ?? "");

                if (res.Success)
                {
                    string expected = Path.Combine(dst, ".workbuddy");
                    Check("环境变量已写入并指向目标",
                        LongPath.PathEquals(WorkBuddyPaths.GetUserEnv(envName), expected),
                        WorkBuddyPaths.GetUserEnv(envName) ?? "未设置");
                    Check("原位置已不存在（改名而非删除）", !LongPath.Exists(src));
                    Check("备份目录已生成", Directory.Exists(LongPath.Fix(res.Record.BackupPath ?? "")));
                    Check("记录中保存了变量名与原值状态",
                        res.Record.EnvName == envName && !res.Record.EnvOriginalExisted);

                    MigrationRecord rec = MigrationRecord.Load(recPath);
                    MigrationEngine.Rollback(rec, (l, m) => { }, skipProcessCheck: true);

                    Check("回滚后环境变量已清空（迁移前未设置）",
                        string.IsNullOrEmpty(WorkBuddyPaths.GetUserEnv(envName)),
                        WorkBuddyPaths.GetUserEnv(envName) ?? "已清空");
                    Check("回滚后原目录恢复", Directory.Exists(LongPath.Fix(src)) &&
                        File.Exists(LongPath.Fix(Path.Combine(src, "logs", "e.log"))));
                }
            }
            catch (Exception ex)
            {
                Check("环境变量方式执行", false, ex.Message);
            }
            finally
            {
                try { MigrationEngine.SetUserEnv(envName, null); } catch { }
            }
        }

        // ---------------- 用例 6 ----------------

        private static void Case6(string sandbox)
        {
            Group("用例 6：子目录分级表（17 项断言 + 真实目录兜底扫描）");

            Check("logs → 缓存", WorkBuddyPaths.Classify("logs") == RiskTier.Cache);
            Check("cache → 缓存", WorkBuddyPaths.Classify("cache") == RiskTier.Cache);
            Check("binaries → 可重下", WorkBuddyPaths.Classify("binaries") == RiskTier.Redownload);
            Check("plugins → 可重下", WorkBuddyPaths.Classify("plugins") == RiskTier.Redownload);
            Check("app → 有状态", WorkBuddyPaths.Classify("app") == RiskTier.Stateful);
            Check("workspace → 有状态", WorkBuddyPaths.Classify("workspace") == RiskTier.Stateful);
            Check("projects → 核心", WorkBuddyPaths.Classify("projects") == RiskTier.Core);
            Check("security → 核心", WorkBuddyPaths.Classify("security") == RiskTier.Core);
            Check("credentials → 核心", WorkBuddyPaths.Classify("credentials") == RiskTier.Core);
            Check("sessions → 核心", WorkBuddyPaths.Classify("sessions") == RiskTier.Core);
            Check("未知目录名 → 未识别", WorkBuddyPaths.Classify("完全未知的目录XYZ") == RiskTier.Unknown);
            Check("大小写不敏感（LOGS → 缓存）", WorkBuddyPaths.Classify("LOGS") == RiskTier.Cache);
            Check("分级表覆盖 60+ 个目录名", WorkBuddyPaths.RiskTableCount >= 60, "实际 " + WorkBuddyPaths.RiskTableCount + " 条");
            Check("核心级别禁止勾选", !WorkBuddyPaths.IsSelectable(RiskTier.Core));
            Check("缓存级别允许勾选", WorkBuddyPaths.IsSelectable(RiskTier.Cache));
            Check("默认勾选：缓存+可重下，不勾有状态/核心",
                WorkBuddyPaths.IsDefaultChecked(RiskTier.Cache) &&
                WorkBuddyPaths.IsDefaultChecked(RiskTier.Redownload) &&
                !WorkBuddyPaths.IsDefaultChecked(RiskTier.Stateful) &&
                !WorkBuddyPaths.IsDefaultChecked(RiskTier.Core));

            var realTiers = new[] { RiskTier.Cache, RiskTier.Redownload, RiskTier.Stateful, RiskTier.Core };
            int minTier = realTiers.Min(t => WorkBuddyPaths.RiskTable.Values.Count(v => v == t));
            Check("每个级别至少 3 个条目", minTier >= 3, "最少级别条目数 " + minTier);

            // 兜底扫描：真实目录里出现未收录名字时，必须落到"未识别"而不是抛错
            string root = Path.Combine(sandbox, "case6", "scan");
            Directory.CreateDirectory(LongPath.Fix(Path.Combine(root, "logs")));
            Directory.CreateDirectory(LongPath.Fix(Path.Combine(root, "projects")));
            Directory.CreateDirectory(LongPath.Fix(Path.Combine(root, "某个新版本才有的目录")));
            File.WriteAllText(LongPath.Fix(Path.Combine(root, "loose.txt")), "x", new UTF8Encoding(false));

            DirSnapshot snap = ProcessGuard.Snapshot(root);
            bool covered = snap.Subdirs.Count == 3 &&
                           snap.Subdirs.Any(s => s.Name == "logs" && s.Tier == RiskTier.Cache) &&
                           snap.Subdirs.Any(s => s.Name == "projects" && s.Tier == RiskTier.Core) &&
                           snap.Subdirs.Any(s => s.Name == "某个新版本才有的目录" && s.Tier == RiskTier.Unknown);
            Check("GUID 形式目录 → 核心", WorkBuddyPaths.Classify("8ee3283b-3e9c-4cf9-b279-3dfa4d5bbeb9") == RiskTier.Core);
            Check("user-<guid>-personal → 核心",
                WorkBuddyPaths.Classify("user-8da3c8bb-638a-4fc0-bf6d-a4d0dd502279-personal") == RiskTier.Core);
            Check("*-tmp 后缀 → 缓存", WorkBuddyPaths.Classify("knowledge-upload-tmp") == RiskTier.Cache);
            Check("真实目录兜底扫描与分级全覆盖", covered);
            Check("根目录散落文件单独统计", snap.RootLooseFiles == 1 && snap.RootLooseBytes > 0);
        }

        // ---------------- 用例 7 ----------------

        private static void Case7(string sandbox)
        {
            Group("用例 7：合并到非空目标（12 项断言，含幂等性与 ScanPending 归零）");

            string root = Path.Combine(sandbox, "case7");
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(LongPath.Fix(src));
            Directory.CreateDirectory(LongPath.Fix(dst));

            DateTime t2020 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime t2019 = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime t2021 = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime t2022 = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            void Make(string path, string content, DateTime t)
            {
                File.WriteAllText(LongPath.Fix(path), content, new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(LongPath.Fix(path), t);
            }

            Make(Path.Combine(src, "a.txt"), "AAA", t2020);   // 目标更新 → 保留目标
            Make(Path.Combine(src, "b.txt"), "BBB", t2022);   // 目标更旧 → 覆盖
            Make(Path.Combine(src, "c.txt"), "CCC", t2020);   // 内容相同且不旧 → 保留目标
            Make(Path.Combine(src, "d.txt"), "DDD", t2020);   // 目标缺失 → 补齐

            Make(Path.Combine(dst, "a.txt"), "AAA-newer", t2021);
            Make(Path.Combine(dst, "b.txt"), "old", t2019);
            Make(Path.Combine(dst, "c.txt"), "CCC", t2020);
            Make(Path.Combine(dst, "e.txt"), "EEE", t2020);   // 目标独有

            ScanResult scan = FileCopier.Scan(src);
            int minPending = int.MaxValue;
            CopyStats stats = FileCopier.Copy(src, dst, scan, true,
                (s, cur) => { if (s.ScanPending < minPending) minPending = s.ScanPending; });

            string ReadTxt(string p) => File.ReadAllText(LongPath.Fix(Path.Combine(dst, p)), Encoding.UTF8);

            Check("目标更新时保留目标（不回退）", ReadTxt("a.txt") == "AAA-newer", ReadTxt("a.txt"));
            Check("目标更旧时用源覆盖", ReadTxt("b.txt") == "BBB", ReadTxt("b.txt"));
            Check("内容相同且不旧时保留目标", ReadTxt("c.txt") == "CCC");
            Check("目标缺失时补齐", File.Exists(LongPath.Fix(Path.Combine(dst, "d.txt"))));
            Check("目标独有文件被保留", ReadTxt("e.txt") == "EEE");
            Check("复制计数正确（2 复制 / 2 跳过）", stats.FilesCopied == 2 && stats.FilesSkipped == 2,
                $"复制 {stats.FilesCopied}，跳过 {stats.FilesSkipped}");
            Check("ScanPending 归零", stats.ScanPending == 0, "实际 " + stats.ScanPending);
            Check("ScanPending 全程不为负（计数无重复扣减）", minPending >= 0, "最小值 " + minPending);

            bool verifyOk = true;
            var notes = new List<string>();
            try { notes = FileCopier.VerifyMerge(dst, scan); }
            catch (Exception ex) { verifyOk = false; notes.Add(ex.Message); }
            Check("合并模式校验通过", verifyOk, notes.Count > 0 ? notes[0] : "");
            Check("目标独有文件只提示、不判错", notes.Any(n => n.Contains("源里没有")));

            CopyStats again = FileCopier.Copy(src, dst, FileCopier.Scan(src), true);
            Check("幂等性：重跑复制 0 个文件", again.FilesCopied == 0, "实际 " + again.FilesCopied);
            Check("幂等性：重跑 ScanPending 归零", again.ScanPending == 0, "实际 " + again.ScanPending);
            Check("幂等性：重跑后文件内容不变", ReadTxt("a.txt") == "AAA-newer" && ReadTxt("b.txt") == "BBB");
        }

        // ---------------- 用例 8：扫描错误必须阻断 ----------------

        private static void Case8(string sandbox)
        {
            Group("用例 8：读不到的目录必须阻断迁移（否则会静默漏拷却报「校验通过」）");

            string root = Path.Combine(sandbox, "case8");
            Directory.CreateDirectory(LongPath.Fix(root));

            // 干净目录不得被误杀
            string clean = Path.Combine(root, "clean");
            Directory.CreateDirectory(LongPath.Fix(clean));
            File.WriteAllText(LongPath.Fix(Path.Combine(clean, "a.txt")), "ok", new UTF8Encoding(false));

            ScanResult cleanScan = FileCopier.Scan(clean);
            bool cleanThrew = false;
            try { FileCopier.ThrowIfScanFailed(cleanScan, "测试目录"); }
            catch { cleanThrew = true; }
            Check("扫描无错误时不误报", !cleanThrew && cleanScan.ErrorMessages.Count == 0);

            // 不存在的目录 → Scan 记错误 → 必须被拦下
            string ghost = Path.Combine(root, "path-that-does-not-exist");
            ScanResult bad = FileCopier.Scan(ghost);
            Check("不存在的目录产生扫描错误", bad.ErrorMessages.Count > 0, "错误数 " + bad.ErrorMessages.Count);

            bool threw = false;
            string msg = "";
            try { FileCopier.ThrowIfScanFailed(bad, "源目录"); }
            catch (IOException ex) { threw = true; msg = ex.Message; }
            catch (Exception ex) { msg = ex.GetType().Name + "：" + ex.Message; }

            Check("扫描有错误时抛异常阻断", threw, msg);
            Check("阻断信息说清了「已中止、源数据未改动」",
                msg.Contains("已中止") && msg.Contains("无法读取"), msg.Split('\n').FirstOrDefault() ?? "");
        }

        // ---------------- 用例 9：盘根目标路径 ----------------

        private static void Case9(string sandbox)
        {
            Group("用例 9：目标位置填盘根时的路径推导（防止拼成 D 盘的相对路径）");

            string driveRoot = Path.GetPathRoot(LongPath.Normalize(sandbox)) ?? @"C:\";
            string letter = driveRoot.TrimEnd('\\');   // "C:"

            string fromRoot = MigrationEngine.ResolveTargetDir(@"C:\Users\someone\.workbuddy", driveRoot);
            Check($"盘根「{driveRoot}」→ {letter}\\.workbuddy",
                string.Equals(fromRoot, letter + @"\.workbuddy", StringComparison.OrdinalIgnoreCase), fromRoot);

            string fromBare = MigrationEngine.ResolveTargetDir(@"C:\Users\someone\.workbuddy", letter);
            Check($"盘根「{letter}」（无尾分隔符）推导一致",
                string.Equals(fromBare, letter + @"\.workbuddy", StringComparison.OrdinalIgnoreCase), fromBare);
            Check("推导结果不是盘符相对路径（不以「盘符:」紧跟名字结尾）",
                !fromBare.StartsWith(letter + ".", StringComparison.OrdinalIgnoreCase), fromBare);

            string normal = MigrationEngine.ResolveTargetDir(@"C:\Users\someone\.workbuddy", @"D:\WorkBuddyData\");
            Check("普通目录仍正确追加叶子名",
                string.Equals(normal, @"D:\WorkBuddyData\.workbuddy", StringComparison.OrdinalIgnoreCase), normal);
        }

        // ---------------- 用例 10：逐文件校验 ----------------

        private static void Case10(string sandbox)
        {
            Group("用例 10：严格校验必须逐文件比对（总量相同不等于复制完整）");

            string root = Path.Combine(sandbox, "case10");
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(LongPath.Fix(src));

            File.WriteAllText(LongPath.Fix(Path.Combine(src, "a.bin")), new string('A', 1000), new UTF8Encoding(false));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "b.bin")), new string('B', 1000), new UTF8Encoding(false));

            ScanResult scan = FileCopier.Scan(src);
            FileCopier.Copy(src, dst, scan, false);

            bool okClean = true;
            try { FileCopier.VerifyStrict(dst, scan); } catch { okClean = false; }
            Check("正常复制后校验通过", okClean);

            // 一长一短：文件数与总字节都不变，只有逐文件比对才能发现
            File.WriteAllText(LongPath.Fix(Path.Combine(dst, "a.bin")), new string('A', 900), new UTF8Encoding(false));
            File.WriteAllText(LongPath.Fix(Path.Combine(dst, "b.bin")), new string('B', 1100), new UTF8Encoding(false));

            ScanResult tampered = FileCopier.Scan(dst);
            Check("篡改后文件数仍相同（总数校验会漏判）", tampered.FileCount == scan.FileCount,
                $"源 {scan.FileCount} / 目标 {tampered.FileCount}");
            Check("篡改后总字节仍相同（总数校验会漏判）", tampered.TotalBytes == scan.TotalBytes,
                $"源 {scan.TotalBytes} / 目标 {tampered.TotalBytes}");

            bool caught = false;
            string msg = "";
            try { FileCopier.VerifyStrict(dst, scan); }
            catch (IOException ex) { caught = true; msg = ex.Message; }

            Check("逐文件校验发现了长度不一致", caught, msg.Split('\n').FirstOrDefault() ?? "");
            Check("报告指出了具体文件", msg.Contains("a.bin") || msg.Contains("b.bin"));
        }

        // ---------------- 用例 11：回滚失败不得假装成功 ----------------

        private static void Case11(string sandbox)
        {
            Group("用例 11：回滚受阻时必须如实报错，且不把记录标成已回滚");

            string root = Path.Combine(sandbox, "case11");
            string src = Path.Combine(root, "src", ".workbuddy");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "logs")));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "logs", "a.log")), "A", new UTF8Encoding(false));

            string recPath = Path.Combine(root, "record.json");
            var opt = new MigrationOptions
            {
                SourcePath = src,
                TargetRoot = dst,
                Mode = MigrateMode.Partial,
                PartialNames = new List<string> { "logs" },
                SkipProcessCheck = true,
                RecordPath = recPath,
                RequiredBytes = 1,
            };

            MigrationResult res = MigrationEngine.Execute(opt, (l, m) => { });
            Check("前置：部分迁移成功", res.Success, res.Error ?? "");
            if (!res.Success) return;

            string srcLogs = Path.Combine(src, "logs");
            string backup = res.BackupPaths.FirstOrDefault();

            // 模拟「原位置被别的东西占用了」：拆掉联接，另建一个含数据的真实目录
            try { Junction.Delete(srcLogs); } catch { }
            Directory.CreateDirectory(LongPath.Fix(srcLogs));
            File.WriteAllText(LongPath.Fix(Path.Combine(srcLogs, "someone-else.txt")), "别人的数据", new UTF8Encoding(false));

            MigrationRecord rec = MigrationRecord.Load(recPath);
            bool threw = false;
            string msg = "";
            try { MigrationEngine.Rollback(rec, (l, m) => { }, skipProcessCheck: true); }
            catch (InvalidOperationException ex) { threw = true; msg = ex.Message; }
            catch (Exception ex) { msg = ex.GetType().Name + "：" + ex.Message; }

            Check("回滚受阻时抛出异常而不是静默成功", threw, msg);
            Check("别人的数据没有被删", File.Exists(LongPath.Fix(Path.Combine(srcLogs, "someone-else.txt"))));
            Check("备份目录仍在（退路没丢）", backup != null && Directory.Exists(LongPath.Fix(backup)));
            Check("记录仍为 completed（还能再次回滚）",
                (MigrationRecord.Load(recPath)?.Status ?? "") != "rolledback",
                MigrationRecord.Load(recPath)?.Status ?? "读取失败");

            // 清理占用后再回滚，应当成功
            try { TryDeleteTree(srcLogs); } catch { }
            bool second = true;
            msg = "";
            try { MigrationEngine.Rollback(MigrationRecord.Load(recPath), (l, m) => { }, skipProcessCheck: true); }
            catch (Exception ex) { second = false; msg = ex.Message; }

            Check("占用消除后重新回滚成功", second, msg);
            Check("重新回滚后记录为 rolledback",
                (MigrationRecord.Load(recPath)?.Status ?? "") == "rolledback",
                MigrationRecord.Load(recPath)?.Status ?? "");
            Check("重新回滚后源文件回来了", File.Exists(LongPath.Fix(Path.Combine(srcLogs, "a.log"))));
        }

        // ---------------- 用例 12：记账失败不得回滚 ----------------

        private static void Case12(string sandbox)
        {
            Group("用例 12：回滚记录写入失败时，迁移成果必须保留（不能因为记账失败就回滚）");

            string root = Path.Combine(sandbox, "case12");
            string src = Path.Combine(root, "src", ".workbuddy");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(LongPath.Fix(Path.Combine(src, "projects")));
            File.WriteAllText(LongPath.Fix(Path.Combine(src, "projects", "p.json")), "P", new UTF8Encoding(false));

            // 把记录路径指到一个"目录"上，写文件必然失败
            string badRecordPath = Path.Combine(root, "record-is-a-directory");
            Directory.CreateDirectory(LongPath.Fix(badRecordPath));

            var opt = new MigrationOptions
            {
                SourcePath = src,
                TargetRoot = dst,
                Mode = MigrateMode.Junction,
                SkipProcessCheck = true,
                RecordPath = badRecordPath,
            };

            MigrationResult res = MigrationEngine.Execute(opt, (l, m) => { });
            Check("记账失败时迁移仍判定为成功", res.Success, res.Error ?? "");
            Check("结果里标出了记录未落盘", !res.RecordSaved, res.RecordSaveError ?? "");
            Check("联接仍然建立（成果保留）", Junction.IsJunction(src));
            Check("数据仍可经联接读取", File.Exists(LongPath.Fix(Path.Combine(src, "projects", "p.json"))));
            Check("备份目录仍在，可据此手工还原",
                res.Record?.BackupPath != null && Directory.Exists(LongPath.Fix(res.Record.BackupPath)),
                res.Record?.BackupPath ?? "");

            // 收尾：用内存里的记录直接回滚，不写盘
            try { MigrationEngine.Rollback(res.Record, (l, m) => { }, skipProcessCheck: true, persist: false); }
            catch { }
        }

        // ---------------- 工具 ----------------

        private static void TryDeleteTree(string dir)
        {
            if (!Directory.Exists(LongPath.Fix(dir))) return;

            var stack = new Stack<string>();
            stack.Push(LongPath.Fix(dir));
            var dirs = new List<string>();

            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                dirs.Add(cur);

                IEnumerable<string> items;
                try { items = Directory.EnumerateFileSystemEntries(cur).ToList(); }
                catch { continue; }

                foreach (string item in items)
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(item); }
                    catch { continue; }

                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        try { NativeMethods.RemoveDirectory(item); } catch { }
                        continue;
                    }

                    if ((attr & FileAttributes.Directory) != 0) stack.Push(item);
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
                try { Directory.Delete(dirs[i], true); } catch { }
            }
        }
    }
}
