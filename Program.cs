using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using WorkBuddyDataMover.Core;

namespace WorkBuddyDataMover
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // 仅用于排障：--selftest [日志路径]，跑完自检直接把结果写文件，不开界面
            if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            {
                string outPath = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(),
                        "WorkBuddyDataMover-selftest-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

                var sb = new StringBuilder();
                int failed = SelfTest.Run(line =>
                {
                    sb.AppendLine(line);
                    try { Console.WriteLine(line); } catch { }
                });

                try
                {
                    string dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }

                return failed == 0 ? 0 : 1;
            }

            // 仅用于排障：--probe [报告路径]，只读统计真实数据目录并输出分级覆盖情况
            if (args.Length > 0 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
            {
                string outPath = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "WorkBuddyDataMover-probe.txt");

                var sb = new StringBuilder();
                string root = WorkBuddyPaths.ResolveConfigDir(out string by);
                DirSnapshot snap = ProcessGuard.Snapshot(root);

                sb.AppendLine("数据目录：" + snap.Root);
                sb.AppendLine("解析来源：" + by);
                sb.AppendLine("存在：" + snap.Exists + "    是联接：" + snap.IsJunction +
                              (snap.IsJunction ? "  → " + snap.JunctionTarget : ""));
                sb.AppendLine($"总量：{Fmt.Bytes(snap.TotalBytes)}   文件 {Fmt.Count(snap.FileCount)}   " +
                              $"一级子目录 {snap.Subdirs.Count}");
                sb.AppendLine($"根目录散落文件：{Fmt.Count(snap.RootLooseFiles)} 个 / {Fmt.Bytes(snap.RootLooseBytes)}");
                sb.AppendLine();

                foreach (RiskTier t in new[]
                {
                    RiskTier.Cache, RiskTier.Redownload, RiskTier.Stateful, RiskTier.Core, RiskTier.Unknown
                })
                {
                    var items = snap.ByTier(t).ToList();
                    sb.AppendLine($"[{WorkBuddyPaths.TierName(t)}] {items.Count} 个 / {Fmt.Bytes(snap.BytesOfTier(t))}");
                    foreach (SubdirStat s in items)
                        sb.AppendLine($"    {s.Name,-32} {Fmt.Bytes(s.Bytes),10}  {Fmt.Count(s.Files),7} 文件");
                }

                try
                {
                    string dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }

                try { Console.WriteLine(sb.ToString()); } catch { }
                return 0;
            }

            // 仅用于排障：--uicheck [报告路径]，真实布局窗口并输出控件边界校验报告
            if (args.Length > 0 && args[0].Equals("--uicheck", StringComparison.OrdinalIgnoreCase))
            {
                string reportPath = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "WorkBuddyDataMover-uicheck.txt");

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

                using (var form = new MainForm(skipAutoLoad: true))
                {
                    form.Show();
                    Application.DoEvents();
                    form.PerformLayout();
                    Application.DoEvents();

                    string report = UiCheck.Run(form);
                    try
                    {
                        string dir = Path.GetDirectoryName(reportPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(reportPath, report, new UTF8Encoding(false));
                    }
                    catch { }

                    form.Hide();
                }

                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

            Application.ThreadException += (s, e) =>
            {
                MessageBox.Show("发生未处理的异常：\r\n\r\n" + e.Exception.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm());
            return 0;
        }
    }
}
