using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkBuddyDataMover.Core;

namespace WorkBuddyDataMover
{
    public class SubdirItem
    {
        public SubdirStat Stat { get; }
        public SubdirItem(SubdirStat stat) { Stat = stat; }
        public override string ToString() => Stat.Display;
    }

    public class MainForm : Form
    {
        private MenuStrip _menu;

        // 状态区
        private Label _lblPathValue;
        private Label _lblTypeValue;
        private Label _lblEnvValue;
        private Label _lblSizeValue;
        private Label _lblTierValue;
        private Button _btnOpenDir;

        // 选项区
        private ComboBox _cboDrive;
        private TextBox _txtTarget;
        private Label _lblTargetPreview;
        private RadioButton _rbJunction;
        private RadioButton _rbEnv;
        private RadioButton _rbPartial;
        private CheckBox _chkMerge;
        private CheckedListBox _clb;
        private Button _btnPreCache;
        private Button _btnPreCacheRedl;
        private Button _btnPre100;
        private Button _btnPreClear;
        private Button _btnPreflight;
        private Button _btnMigrate;
        private Button _btnRollback;
        private Button _btnCleanup;

        // 日志区
        private RichTextBox _log;
        private ProgressBar _bar;
        private Label _lblProgress;
        private Button _btnCancel;

        private DirSnapshot _snapshot;
        private volatile bool _cancelRequested;
        private bool _busy;
        private bool _suppressCheck;
        private bool _suppressDriveEvent;

        public MainForm() : this(false) { }

        public MainForm(bool skipAutoLoad)
        {
            BuildUi();
            LoadAppIcon();
            if (!skipAutoLoad)
                Load += async (s, e) => await LoadSnapshotAsync();
        }

        /// <summary>从嵌入资源加载应用图标，保证标题栏与任务栏都用本工具自己的图标。</summary>
        private void LoadAppIcon()
        {
            try
            {
                using (Stream st = typeof(MainForm).Assembly
                    .GetManifestResourceStream("WorkBuddyDataMover.app.ico"))
                {
                    if (st != null) Icon = new Icon(st);
                }
            }
            catch { }
        }

        /// <summary>界面校验用：暴露关键控件及其当前尺寸。</summary>
        public IEnumerable<KeyValuePair<string, Control>> KeyControls()
        {
            yield return new KeyValuePair<string, Control>("部分迁移清单", _clb);
            yield return new KeyValuePair<string, Control>("日志区", _log);
            yield return new KeyValuePair<string, Control>("进度条", _bar);
            yield return new KeyValuePair<string, Control>("仅预检按钮", _btnPreflight);
            yield return new KeyValuePair<string, Control>("开始迁移按钮", _btnMigrate);
            yield return new KeyValuePair<string, Control>("一键回滚按钮", _btnRollback);
            yield return new KeyValuePair<string, Control>("清理备份按钮", _btnCleanup);
            yield return new KeyValuePair<string, Control>("目标位置下拉", _cboDrive);
            yield return new KeyValuePair<string, Control>("目标路径输入框", _txtTarget);
            yield return new KeyValuePair<string, Control>("迁移方式-联接", _rbJunction);
            yield return new KeyValuePair<string, Control>("迁移方式-环境变量", _rbEnv);
            yield return new KeyValuePair<string, Control>("迁移方式-部分迁移", _rbPartial);
            yield return new KeyValuePair<string, Control>("数据构成标签", _lblTierValue);
        }

        // ======================= 界面构建 =======================

        private void BuildUi()
        {
            SuspendLayout();

            Text = "WorkBuddy 数据目录迁移工具";
            ClientSize = new Size(1060, 820);
            MinimumSize = new Size(980, 740);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(246, 247, 249);
            AutoScaleMode = AutoScaleMode.Font;

            BuildMenu();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10, 6, 10, 10),
                BackColor = Color.Transparent,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 196F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 356F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            root.Controls.Add(BuildStatusGroup(), 0, 0);
            root.Controls.Add(BuildOptionsGroup(), 0, 1);
            root.Controls.Add(BuildLogGroup(), 0, 2);

            Controls.Add(root);
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            ResumeLayout(false);
            PerformLayout();
        }

        private void BuildMenu()
        {
            _menu = new MenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };

            var mFile = new ToolStripMenuItem("文件(&F)");
            mFile.DropDownItems.Add("打开数据目录", null, (s, e) => OpenDataDir());
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add("退出", null, (s, e) => Close());

            var mTools = new ToolStripMenuItem("工具(&T)");
            mTools.DropDownItems.Add("运行环境自检", null, async (s, e) => await RunSelfTestAsync());
            mTools.DropDownItems.Add("重新检测", null, async (s, e) => await LoadSnapshotAsync());
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add("查看迁移记录", null, (s, e) => ViewRecord());
            mTools.DropDownItems.Add("打开迁移记录所在文件夹", null, (s, e) => OpenRecordFolder());
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add("清理备份目录…", null, async (s, e) => await CleanupAsync());

            var mHelp = new ToolStripMenuItem("帮助(&H)");
            mHelp.DropDownItems.Add("使用说明", null, (s, e) => ShowHelp());
            mHelp.DropDownItems.Add("关于", null, (s, e) => ShowAbout());

            _menu.Items.Add(mFile);
            _menu.Items.Add(mTools);
            _menu.Items.Add(mHelp);
        }

        private GroupBox BuildStatusGroup()
        {
            var gb = new GroupBox
            {
                Text = "当前状态",
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 8, 12, 10),
                Margin = new Padding(0, 0, 0, 8),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(38, 42, 50),
            };

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 5,
                BackColor = Color.Transparent,
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104F));
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 27F));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _lblPathValue = ValueLabel();
            _btnOpenDir = new Button
            {
                Text = "打开目录",
                Dock = DockStyle.Fill,
                Height = 24,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(4, 1, 0, 2),
            };
            _btnOpenDir.Click += (s, e) => OpenDataDir();

            _lblTypeValue = ValueLabel();
            _lblEnvValue = ValueLabel();
            _lblSizeValue = ValueLabel();
            _lblTierValue = ValueLabel();
            _lblTierValue.AutoSize = false;
            _lblTierValue.Dock = DockStyle.Fill;
            _lblTierValue.AutoEllipsis = false;
            _lblTierValue.Padding = new Padding(0, 3, 0, 0);

            AddRow(t, 0, "数据目录", _lblPathValue, _btnOpenDir);
            AddRow(t, 1, "目录类型", _lblTypeValue);
            AddRow(t, 2, "环境变量", _lblEnvValue);
            AddRow(t, 3, "占用空间", _lblSizeValue);
            AddRow(t, 4, "数据构成", _lblTierValue);

            gb.Controls.Add(t);
            return gb;
        }

        private static Label KeyLabel(string text) => new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(110, 116, 128),
            Margin = new Padding(0, 0, 6, 0),
        };

        private static Label ValueLabel() => new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(28, 32, 40),
            Margin = new Padding(0),
        };

        private void AddRow(TableLayoutPanel t, int row, string key, Control value, Control extra = null)
        {
            t.Controls.Add(KeyLabel(key), 0, row);

            if (extra == null)
            {
                t.Controls.Add(value, 1, row);
                t.SetColumnSpan(value, 2);
            }
            else
            {
                t.Controls.Add(value, 1, row);
                t.Controls.Add(extra, 2, row);
            }
        }

        private GroupBox BuildOptionsGroup()
        {
            var gb = new GroupBox
            {
                Text = "迁移选项",
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 8, 12, 10),
                Margin = new Padding(0, 0, 0, 8),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(38, 42, 50),
            };

            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
            };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53F));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47F));

            outer.Controls.Add(BuildLeftOptions(), 0, 0);
            outer.Controls.Add(BuildPartialPanel(), 1, 0);

            gb.Controls.Add(outer);
            return gb;
        }

        private Control BuildLeftOptions()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 12, 0),
            };
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 128F));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // --- 目标位置 ---
            var pTarget = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
            };
            pTarget.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            pTarget.RowStyles.Add(new RowStyle(SizeType.Absolute, 27F));
            pTarget.RowStyles.Add(new RowStyle(SizeType.Absolute, 21F));

            var row0 = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };

            var lbl = new Label
            {
                Text = "目标位置：",
                AutoSize = false,
                Width = 76,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(110, 116, 128),
                Margin = new Padding(0, 3, 0, 0),
            };

            _cboDrive = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 300,
                Margin = new Padding(0, 1, 6, 0),
            };
            _cboDrive.SelectedIndexChanged += (s, e) => ApplyDriveSelection();

            var btnBrowse = new Button
            {
                Text = "浏览…",
                Width = 62,
                Height = 24,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(0, 0, 0, 0),
            };
            btnBrowse.Click += (s, e) => BrowseTarget();

            row0.Controls.Add(lbl);
            row0.Controls.Add(_cboDrive);
            row0.Controls.Add(btnBrowse);

            _txtTarget = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
            _txtTarget.TextChanged += (s, e) => UpdateTargetPreview();

            _lblTargetPreview = new Label
            {
                Text = "将迁移到：—",
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(96, 132, 200),
                Margin = new Padding(0),
                TextAlign = ContentAlignment.MiddleLeft,
            };

            pTarget.Controls.Add(row0, 0, 0);
            pTarget.Controls.Add(_txtTarget, 0, 1);
            pTarget.Controls.Add(_lblTargetPreview, 0, 2);

            // --- 迁移方式 ---
            var gbMode = new GroupBox
            {
                Text = "迁移方式",
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 6, 10, 6),
                Margin = new Padding(0, 4, 0, 4),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(38, 42, 50),
            };

            _rbJunction = new RadioButton
            {
                Text = "目录联接（推荐）—— 对启动方式透明，不改任何配置",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Checked = true,
            };
            _rbEnv = new RadioButton
            {
                Text = "环境变量 —— 不在原位置留联接，需注销重登",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            _rbPartial = new RadioButton
            {
                Text = "部分迁移（缓存专项）—— 只搬大体积缓存，核心数据留在 C 盘",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            _rbPartial.CheckedChanged += (s, e) => UpdateModeEnabled();

            var hint = new Label
            {
                Text = "目录联接通过 Win32 API 直接创建（不调用 mklink），创建后会立即回读复核。",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(130, 136, 148),
                Margin = new Padding(0),
                TextAlign = ContentAlignment.TopLeft,
            };

            var modeLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
            };
            modeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23F));
            modeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23F));
            modeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23F));
            modeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            modeLayout.Controls.Add(_rbJunction, 0, 0);
            modeLayout.Controls.Add(_rbEnv, 0, 1);
            modeLayout.Controls.Add(_rbPartial, 0, 2);
            modeLayout.Controls.Add(hint, 0, 3);

            gbMode.Controls.Add(modeLayout);

            // --- 合并开关 ---
            _chkMerge = new CheckBox
            {
                Text = "允许合并到非空目标（只补不覆盖，绝不回退目标上的新数据）",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(60, 66, 78),
                Margin = new Padding(0, 4, 0, 0),
            };

            // --- 操作按钮 ---
            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 6, 0, 0),
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));

            _btnPreflight = ActionButton("仅预检", FlatStyle.System);
            _btnPreflight.Click += async (s, e) => await PreflightOnlyAsync();

            _btnMigrate = ActionButton("开始迁移", FlatStyle.Standard);
            _btnMigrate.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnMigrate.Click += async (s, e) => await MigrateAsync();

            _btnRollback = ActionButton("一键回滚", FlatStyle.System);
            _btnRollback.Enabled = false;
            _btnRollback.Click += async (s, e) => await RollbackAsync();

            _btnCleanup = ActionButton("清理备份", FlatStyle.System);
            _btnCleanup.Enabled = false;
            _btnCleanup.Click += async (s, e) => await CleanupAsync();

            buttons.Controls.Add(_btnPreflight, 0, 0);
            buttons.Controls.Add(_btnMigrate, 1, 0);
            buttons.Controls.Add(_btnRollback, 0, 1);
            buttons.Controls.Add(_btnCleanup, 1, 1);

            t.Controls.Add(pTarget, 0, 0);
            t.Controls.Add(gbMode, 0, 1);
            t.Controls.Add(_chkMerge, 0, 2);
            t.Controls.Add(buttons, 0, 3);

            return t;
        }

        private static Button ActionButton(string text, FlatStyle style) => new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = style,
            Margin = new Padding(0, 0, 6, 6),
        };

        private Control BuildPartialPanel()
        {
            var gb = new GroupBox
            {
                Text = "部分迁移清单（仅“部分迁移”方式生效）",
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(0),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(38, 42, 50),
            };

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
            };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));

            _clb = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 0, 0, 4),
                HorizontalScrollbar = true,
            };
            _clb.ItemCheck += Clb_ItemCheck;

            var presets = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
            };
            for (int i = 0; i < 4; i++) presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            _btnPreCache = PresetButton("全选缓存");
            _btnPreCache.Click += (s, e) => ApplyPreset(RiskTier.Cache);

            _btnPreCacheRedl = PresetButton("缓存+可重下 ★");
            _btnPreCacheRedl.Click += (s, e) => ApplyPreset(RiskTier.Redownload);

            _btnPre100 = PresetButton("仅选 ≥100MB");
            _btnPre100.Click += (s, e) => ApplySizePreset(100L * 1024 * 1024);

            _btnPreClear = PresetButton("清空");
            _btnPreClear.Click += (s, e) => ApplyPreset(null);

            presets.Controls.Add(_btnPreCache, 0, 0);
            presets.Controls.Add(_btnPreCacheRedl, 1, 0);
            presets.Controls.Add(_btnPre100, 2, 0);
            presets.Controls.Add(_btnPreClear, 3, 0);

            t.Controls.Add(_clb, 0, 0);
            t.Controls.Add(presets, 0, 1);

            gb.Controls.Add(t);
            return gb;
        }

        private static Button PresetButton(string text) => new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 0, 4, 0),
            Font = new Font("Microsoft YaHei UI", 8.5F),
        };

        private GroupBox BuildLogGroup()
        {
            var gb = new GroupBox
            {
                Text = "日志与进度",
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 8, 12, 10),
                Margin = new Padding(0),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(38, 42, 50),
            };

            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
            };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            _log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(252, 252, 253),
                ForeColor = Color.FromArgb(40, 44, 52),
                Font = new Font("Consolas", 9F),
                DetectUrls = false,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Margin = new Padding(0, 0, 0, 6),
            };

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));

            _bar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Margin = new Padding(0, 8, 10, 8),
            };

            _lblProgress = new Label
            {
                Text = "就绪",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(96, 102, 114),
                Margin = new Padding(0),
            };

            _btnCancel = new Button
            {
                Text = "取消",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.System,
                Enabled = false,
                Margin = new Padding(0, 6, 0, 6),
            };
            _btnCancel.Click += (s, e) =>
            {
                _cancelRequested = true;
                _btnCancel.Enabled = false;
                Log(LogLevel.Warn, "已请求取消，将在当前文件处理完后停止…");
            };

            bottom.Controls.Add(_bar, 0, 0);
            bottom.Controls.Add(_lblProgress, 1, 0);
            bottom.Controls.Add(_btnCancel, 2, 0);

            t.Controls.Add(_log, 0, 0);
            t.Controls.Add(bottom, 0, 1);

            gb.Controls.Add(t);
            return gb;
        }

        // ======================= 状态刷新 =======================

        private async Task LoadSnapshotAsync()
        {
            if (_busy) return;

            SetBusy(true, cancelable: false);
            _bar.Style = ProgressBarStyle.Marquee;
            _lblProgress.Text = "正在统计数据目录体积（大目录可能需要十几秒）…";

            try
            {
                string root = WorkBuddyPaths.ResolveConfigDir(out string resolvedBy);

                DirSnapshot snap = await Task.Run(() => ProcessGuard.Snapshot(
                    root, null,
                    name =>
                    {
                        try
                        {
                            BeginInvoke(new Action(() => _lblProgress.Text = "正在统计：" + name));
                        }
                        catch { }
                    }));

                snap.ResolvedBy = resolvedBy;
                _snapshot = snap;

                RefreshDrives();
                RenderStatus();
                RenderPartialList();
                UpdateRollbackAvailability();
                UpdateTargetPreview();
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "统计失败：" + ex.Message);
            }
            finally
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 0;
                _lblProgress.Text = "就绪";
                SetBusy(false);
            }
        }

        private void RefreshDrives()
        {
            string sourceRoot = null;
            try { sourceRoot = Path.GetPathRoot(LongPath.Unfix(_snapshot?.Root ?? "")); } catch { }

            // 用户已填/已选的目标路径不能被"重新检测"冲掉
            bool keepUserTarget = !string.IsNullOrWhiteSpace(_txtTarget.Text);

            List<DriveOption> drives = Disks.GetCandidateDrives();

            _suppressDriveEvent = true;
            try
            {
                _cboDrive.BeginUpdate();
                try
                {
                    _cboDrive.Items.Clear();
                    foreach (DriveOption d in drives) _cboDrive.Items.Add(d);
                }
                finally
                {
                    _cboDrive.EndUpdate();
                }

                if (drives.Count == 0)
                {
                    _txtTarget.Text = "";
                    _lblTargetPreview.Text = "未找到支持目录联接的本地磁盘（需要 NTFS / ReFS）。";
                    return;
                }

                int pick = -1;

                if (keepUserTarget)
                {
                    string currentRoot = null;
                    try { currentRoot = Path.GetPathRoot(LongPath.Unfix(_txtTarget.Text)); } catch { }

                    if (!string.IsNullOrEmpty(currentRoot))
                    {
                        pick = drives.FindIndex(d => string.Equals(
                            LongPath.Normalize(d.Root),
                            LongPath.Normalize(currentRoot),
                            StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (pick < 0)
                {
                    pick = drives.FindIndex(d => !string.Equals(
                        LongPath.Normalize(d.Root),
                        LongPath.Normalize(sourceRoot ?? ""),
                        StringComparison.OrdinalIgnoreCase));
                }

                if (pick < 0) pick = 0;

                _cboDrive.SelectedIndex = pick;
            }
            finally
            {
                _suppressDriveEvent = false;
            }

            if (!keepUserTarget) ApplyDriveSelection();
            UpdateTargetPreview();
        }

        private void RenderStatus()
        {
            if (_snapshot == null) return;

            _lblPathValue.Text = _snapshot.Root;

            if (!_snapshot.Exists)
            {
                _lblTypeValue.Text = "目录不存在";
                _lblTypeValue.ForeColor = Color.FromArgb(190, 70, 70);
                _lblSizeValue.Text = "—";
                _lblTierValue.Text = "—";
            }
            else if (_snapshot.IsJunction)
            {
                _lblTypeValue.Text = "目录联接 → " + (_snapshot.JunctionTarget ?? "（无法读取）");
                _lblTypeValue.ForeColor = Color.FromArgb(30, 130, 90);
                _lblSizeValue.Text = "已迁移，本工具不再重复统计";
                _lblTierValue.Text = "—";
            }
            else
            {
                _lblTypeValue.Text = $"真实目录（按{_snapshot.ResolvedBy}解析）";
                _lblTypeValue.ForeColor = Color.FromArgb(38, 42, 50);
                _lblSizeValue.Text = $"{Fmt.Bytes(_snapshot.TotalBytes)}（{Fmt.Count(_snapshot.FileCount)} 文件 / " +
                                     $"{_snapshot.Subdirs.Count} 个一级子目录）";

                string Tier(RiskTier tier) =>
                    $"{WorkBuddyPaths.TierName(tier)} {Fmt.Bytes(_snapshot.BytesOfTier(tier))}（{_snapshot.ByTier(tier).Count()} 个）";

                _lblTierValue.Text = Tier(RiskTier.Cache) + "      " + Tier(RiskTier.Redownload) +
                                     Environment.NewLine +
                                     Tier(RiskTier.Stateful) + "      " + Tier(RiskTier.Core) +
                                     "      " + Tier(RiskTier.Unknown);
            }

            string envUser = WorkBuddyPaths.GetUserEnv(WorkBuddyPaths.EnvPrimary);
            string envProc = WorkBuddyPaths.GetProcessEnv(WorkBuddyPaths.EnvPrimary);

            if (!string.IsNullOrWhiteSpace(envUser))
            {
                _lblEnvValue.Text = $"{WorkBuddyPaths.EnvPrimary} = {envUser}（用户级）";
                _lblEnvValue.ForeColor = Color.FromArgb(30, 110, 170);
            }
            else if (!string.IsNullOrWhiteSpace(envProc))
            {
                _lblEnvValue.Text = $"{WorkBuddyPaths.EnvPrimary} =（未持久设置；当前进程继承 {envProc}）";
                _lblEnvValue.ForeColor = Color.FromArgb(150, 120, 40);
            }
            else
            {
                _lblEnvValue.Text = $"{WorkBuddyPaths.EnvPrimary} =（未设置）";
                _lblEnvValue.ForeColor = Color.FromArgb(96, 102, 114);
            }
        }

        private void RenderPartialList()
        {
            _suppressCheck = true;
            _clb.BeginUpdate();
            try
            {
                _clb.Items.Clear();
                if (_snapshot == null || !_snapshot.Exists || _snapshot.IsJunction) return;

                foreach (SubdirStat s in _snapshot.Subdirs)
                {
                    var item = new SubdirItem(s);
                    int idx = _clb.Items.Add(item);
                    if (WorkBuddyPaths.IsSelectable(s.Tier) && WorkBuddyPaths.IsDefaultChecked(s.Tier))
                        _clb.SetItemChecked(idx, true);
                }
            }
            finally
            {
                _clb.EndUpdate();
                _suppressCheck = false;
            }
        }

        private void ApplyDriveSelection()
        {
            if (_suppressDriveEvent) return;

            if (_cboDrive.SelectedItem is DriveOption d)
            {
                _txtTarget.Text = Path.Combine(d.Root, "WorkBuddyData");
                UpdateTargetPreview();
            }
        }

        private void BrowseTarget()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择目标文件夹（将在此文件夹下创建 .workbuddy 子目录）";
                dlg.ShowNewFolderButton = true;
                dlg.UseDescriptionForTitle = true;

                string current = _txtTarget.Text.Trim();
                if (!string.IsNullOrEmpty(current) && Directory.Exists(LongPath.Fix(current)))
                    dlg.SelectedPath = current;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtTarget.Text = dlg.SelectedPath;
                    UpdateTargetPreview();
                }
            }
        }

        private void UpdateTargetPreview()
        {
            string source = _snapshot?.Root ?? WorkBuddyPaths.ResolveConfigDir();
            string targetRoot = _txtTarget.Text.Trim();

            if (string.IsNullOrEmpty(targetRoot))
            {
                _lblTargetPreview.Text = "将迁移到：—";
                return;
            }

            try
            {
                string targetDir = MigrationEngine.ResolveTargetDir(source, targetRoot);
                _lblTargetPreview.Text = "将迁移到：" + targetDir;
            }
            catch
            {
                _lblTargetPreview.Text = "将迁移到：—";
            }
        }

        private void UpdateModeEnabled()
        {
            bool partial = _rbPartial.Checked;
            _clb.Enabled = partial;
            _btnPreCache.Enabled = partial;
            _btnPreCacheRedl.Enabled = partial;
            _btnPre100.Enabled = partial;
            _btnPreClear.Enabled = partial;
        }

        private void UpdateRollbackAvailability()
        {
            MigrationRecord rec = MigrationRecord.Load();
            bool can = rec != null && rec.IsCompleted;
            if (_btnRollback != null) _btnRollback.Enabled = can && !_busy;
            if (_btnCleanup != null) _btnCleanup.Enabled = can && !_busy;
        }

        // ======================= 清单交互 =======================

        private void Clb_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressCheck) return;

            if (_clb.Items[e.Index] is SubdirItem item)
            {
                if (e.NewValue == CheckState.Checked && !WorkBuddyPaths.IsSelectable(item.Stat.Tier))
                {
                    e.NewValue = CheckState.Unchecked;
                    Log(LogLevel.Warn,
                        $"「{item.Stat.Name}」属于{WorkBuddyPaths.TierName(item.Stat.Tier)}数据，已强制保持未勾选。");
                }
            }
        }

        private void ApplyPreset(RiskTier? tier)
        {
            _suppressCheck = true;
            _clb.BeginUpdate();
            try
            {
                for (int i = 0; i < _clb.Items.Count; i++)
                {
                    if (!(_clb.Items[i] is SubdirItem item)) continue;

                    bool selectable = WorkBuddyPaths.IsSelectable(item.Stat.Tier);
                    bool wanted;
                    if (tier == null) wanted = false;
                    else if (tier == RiskTier.Cache) wanted = item.Stat.Tier == RiskTier.Cache;
                    else wanted = item.Stat.Tier == RiskTier.Cache || item.Stat.Tier == RiskTier.Redownload;

                    _clb.SetItemChecked(i, selectable && wanted);
                }
            }
            finally
            {
                _clb.EndUpdate();
                _suppressCheck = false;
            }

            Log(LogLevel.Info, tier == null ? "已清空勾选。" : "已套用预设：" + (tier == RiskTier.Cache ? "全选缓存" : "缓存 + 可重下"));
        }

        private void ApplySizePreset(long threshold)
        {
            _suppressCheck = true;
            _clb.BeginUpdate();
            try
            {
                for (int i = 0; i < _clb.Items.Count; i++)
                {
                    if (!(_clb.Items[i] is SubdirItem item)) continue;
                    bool selectable = WorkBuddyPaths.IsSelectable(item.Stat.Tier);
                    _clb.SetItemChecked(i, selectable && item.Stat.Bytes >= threshold);
                }
            }
            finally
            {
                _clb.EndUpdate();
                _suppressCheck = false;
            }

            Log(LogLevel.Info, "已套用预设：仅选 ≥ " + Fmt.Bytes(threshold));
        }

        private List<string> SelectedPartialNames()
        {
            var list = new List<string>();
            foreach (object o in _clb.CheckedItems)
                if (o is SubdirItem item) list.Add(item.Stat.Name);
            return list;
        }

        // ======================= 选项组装 =======================

        private MigrationOptions BuildOptions()
        {
            var opt = new MigrationOptions
            {
                SourcePath = _snapshot?.Root ?? WorkBuddyPaths.ResolveConfigDir(),
                TargetRoot = _txtTarget.Text.Trim(),
                MergeMode = _chkMerge.Checked,
                Mode = _rbJunction.Checked ? MigrateMode.Junction
                     : (_rbEnv.Checked ? MigrateMode.EnvVar : MigrateMode.Partial),
                PartialNames = SelectedPartialNames(),
            };

            if (_snapshot != null)
            {
                if (opt.Mode == MigrateMode.Partial)
                {
                    long sum = 0;
                    foreach (string name in opt.PartialNames)
                    {
                        SubdirStat sub = _snapshot.Subdirs.FirstOrDefault(x => x.Name == name);
                        if (sub != null) sum += sub.Bytes;
                    }
                    opt.RequiredBytes = sum;
                }
                else
                {
                    // 整体迁移直接复用快照体积，省掉预检阶段一次全量遍历
                    opt.RequiredBytes = _snapshot.TotalBytes;
                }
            }

            return opt;
        }

        // ======================= 预检 =======================

        private async Task PreflightOnlyAsync()
        {
            if (_busy) return;

            MigrationOptions opt = BuildOptions();
            if (!ValidateOptions(opt)) return;

            SetBusy(true, cancelable: false);
            _bar.Style = ProgressBarStyle.Marquee;
            _lblProgress.Text = "正在预检…";

            try
            {
                PreflightResult pf = await Task.Run(() => MigrationEngine.Preflight(opt, Log));

                if (pf.Passed)
                {
                    Log(LogLevel.Ok, "预检通过，可以开始迁移。");
                    _lblProgress.Text = "预检通过";
                    MessageBox.Show(this,
                        "预检通过。\r\n\r\n" + string.Join("\r\n", pf.Items.Select(i => "· " + i.ToString())),
                        "预检结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    var fails = pf.Failures.ToList();
                    Log(LogLevel.Error, $"预检未通过，发现 {fails.Count} 个阻断项。");
                    _lblProgress.Text = "预检未通过";
                    MessageBox.Show(this,
                        "预检未通过：\r\n\r\n" + string.Join("\r\n\r\n", fails.Select(i => "· " + i.Name + "：" + i.Detail)),
                        "预检结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "预检异常：" + ex.Message);
            }
            finally
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 0;
                SetBusy(false);
            }
        }

        private bool ValidateOptions(MigrationOptions opt)
        {
            if (_snapshot == null || !_snapshot.Exists)
            {
                MessageBox.Show(this, "数据目录不存在或尚未检测完成，请先点「工具 → 重新检测」。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (_snapshot.IsJunction)
            {
                MessageBox.Show(this,
                    "当前数据目录已经是目录联接（已迁移过），无需再次迁移。\r\n如需改指向，请先一键回滚。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (string.IsNullOrWhiteSpace(opt.TargetRoot))
            {
                MessageBox.Show(this, "请先选择目标位置。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (opt.Mode == MigrateMode.Partial && opt.PartialNames.Count == 0)
            {
                MessageBox.Show(this, "部分迁移至少要勾选一个子目录。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }

        // ======================= 迁移 =======================

        private async Task MigrateAsync()
        {
            if (_busy) return;

            MigrationOptions opt = BuildOptions();
            if (!ValidateOptions(opt)) return;

            PreflightResult pf = await Task.Run(() => MigrationEngine.Preflight(opt, Log));
            if (!pf.Passed)
            {
                var fails = pf.Failures.ToList();
                MessageBox.Show(this,
                    "预检未通过，已阻止迁移：\r\n\r\n" + string.Join("\r\n\r\n", fails.Select(i => "· " + i.Name + "：" + i.Detail)),
                    "预检未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string body = BuildConfirmText(opt, pf);
            if (MessageBox.Show(this, body, "确认迁移", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            SetBusy(true);
            _cancelRequested = false;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 0;

            bool refreshAfter = false;

            try
            {
                MigrationResult res = await Task.Run(() =>
                    MigrationEngine.Execute(opt, Log,
                        (pct, done, total) =>
                        {
                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    _bar.Value = Math.Max(0, Math.Min(100, pct));
                                    _lblProgress.Text = $"{pct}%    {Fmt.Bytes(done)} / {Fmt.Bytes(total)}";
                                }));
                            }
                            catch { }
                        },
                        () => _cancelRequested));

                _bar.Value = res.Success ? 100 : 0;

                if (res.Success)
                {
                    _lblProgress.Text = "迁移完成";
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("迁移完成。");
                    sb.AppendLine();
                    sb.AppendLine($"方式：{res.Record.ModeName}");
                    sb.AppendLine($"源目录：{res.Record.SourcePath}");
                    sb.AppendLine($"目标目录：{res.Record.TargetPath}");
                    sb.AppendLine($"迁移：{Fmt.Count(res.Files)} 个文件，{Fmt.Bytes(res.Bytes)}");
                    sb.AppendLine($"用时：{Fmt.Duration(res.Elapsed)}");
                    sb.AppendLine();
                    sb.AppendLine("原数据已改名保留，并未删除。请先启动 WorkBuddy 确认一切正常，");
                    sb.AppendLine("再执行「清理备份」释放这部分空间。");
                    sb.AppendLine();
                    sb.AppendLine("回滚记录：" + WorkBuddyPaths.LastRecordPath);

                    MessageBox.Show(this, sb.ToString(), "迁移完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);

                    refreshAfter = true;
                }
                else
                {
                    _lblProgress.Text = "迁移失败";
                    MessageBox.Show(this,
                        "迁移未完成：\r\n\r\n" + (res.Error ?? "未知错误") +
                        "\r\n\r\n源数据未被删除。请查看日志了解具体停在哪一步。",
                        "迁移失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "迁移异常：" + ex);
                MessageBox.Show(this, "迁移异常：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                UpdateRollbackAvailability();
                // 必须等 _busy 归位后再重新检测，否则会被 LoadSnapshotAsync 的忙判定直接挡掉
                if (refreshAfter) await LoadSnapshotAsync();
            }
        }

        private string BuildConfirmText(MigrationOptions opt, PreflightResult pf)
        {
            string targetDir = pf.TargetDir;
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("即将执行迁移，请再次确认：");
            sb.AppendLine();
            sb.AppendLine($"方式：{(_rbJunction.Checked ? "目录联接（推荐）" : (_rbEnv.Checked ? "环境变量" : "部分迁移"))}");
            sb.AppendLine($"源目录：{opt.SourcePath}");
            sb.AppendLine($"目标目录：{targetDir}");
            if (opt.Mode == MigrateMode.Partial)
                sb.AppendLine($"勾选目录：{opt.PartialNames.Count} 个，合计 {Fmt.Bytes(pf.RequiredBytes)}");
            else
                sb.AppendLine($"数据总量：{Fmt.Bytes(pf.RequiredBytes)}");
            sb.AppendLine();
            sb.AppendLine("· WorkBuddy 必须已完全退出（含托盘右键“退出”）。");
            sb.AppendLine("· 目标盘符必须长期固定。移动硬盘若盘符漂移，联接会失效。");
            sb.AppendLine("· 原数据只会被改名保留，不会被删除；确认正常后可再清理备份。");
            sb.AppendLine("· 迁移过程可随时取消，取消时会自动还原到迁移前状态。");

            return sb.ToString();
        }

        // ======================= 回滚 / 清理 =======================

        private async Task RollbackAsync()
        {
            if (_busy) return;

            MigrationRecord rec = MigrationRecord.Load();
            if (rec == null || !rec.IsCompleted)
            {
                MessageBox.Show(this, "没有可回滚的迁移记录。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("将把以下迁移完整还原：");
            sb.AppendLine();
            sb.AppendLine($"方式：{rec.ModeName}    时间：{rec.TimestampLocal}");
            sb.AppendLine($"源目录：{rec.SourcePath}");
            sb.AppendLine($"目标目录：{rec.TargetPath}");
            if (!string.IsNullOrEmpty(rec.BackupPath)) sb.AppendLine($"备份：{rec.BackupPath}");
            foreach (PartialItemRecord it in rec.Items) sb.AppendLine($"备份：{it.BackupPath}");
            sb.AppendLine();
            sb.AppendLine("回滚前请确保 WorkBuddy 已完全退出。");
            sb.AppendLine("目标盘上的数据不会被删除，只会把原目录改回原名。");

            if (MessageBox.Show(this, sb.ToString(), "确认回滚", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            SetBusy(true, cancelable: false);
            _bar.Style = ProgressBarStyle.Marquee;
            _lblProgress.Text = "正在回滚…";

            bool refreshAfter = false;

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        MigrationEngine.Rollback(rec, Log, false);
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Error, "回滚失败：" + ex.Message);
                        throw;
                    }
                });

                _lblProgress.Text = "回滚完成";
                MessageBox.Show(this, "回滚完成，原目录已还原。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                refreshAfter = true;
            }
            catch (Exception ex)
            {
                _lblProgress.Text = "回滚失败";
                MessageBox.Show(this, "回滚未完成：" + ex.Message, "回滚失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 0;
                SetBusy(false);
                UpdateRollbackAvailability();
                if (refreshAfter) await LoadSnapshotAsync();
            }
        }

        private async Task CleanupAsync()
        {
            if (_busy) return;

            MigrationRecord rec = MigrationRecord.Load();
            if (rec == null || !rec.IsCompleted)
            {
                MessageBox.Show(this, "没有需要清理的备份（或已完成回滚）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var backups = new List<string>();
            if (!string.IsNullOrEmpty(rec.BackupPath)) backups.Add(rec.BackupPath);
            foreach (PartialItemRecord it in rec.Items) if (!string.IsNullOrEmpty(it.BackupPath)) backups.Add(it.BackupPath);

            if (backups.Count == 0)
            {
                MessageBox.Show(this, "记录里没有备份目录。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var existing = backups.Where(p => Directory.Exists(LongPath.Fix(p))).ToList();
            if (existing.Count == 0)
            {
                MessageBox.Show(this, "备份目录已不存在。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("⚠ 此操作会永久删除以下备份目录，不可恢复：");
            sb.AppendLine();
            foreach (string p in existing) sb.AppendLine("· " + p);
            sb.AppendLine();
            sb.AppendLine("删除前请先启动 WorkBuddy，确认登录、会话、历史记录一切正常。");
            sb.AppendLine("如果出现任何异常，请改用「一键回滚」，不要清理。");

            if (MessageBox.Show(this, sb.ToString(), "确认清理备份", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            SetBusy(true, cancelable: false);
            _bar.Style = ProgressBarStyle.Marquee;
            _lblProgress.Text = "正在清理备份…";

            bool refreshAfter = false;

            try
            {
                int removed = await Task.Run(() => MigrationEngine.CleanupBackups(rec, Log));
                _lblProgress.Text = $"已清理 {removed} 个备份目录";
                MessageBox.Show(this, $"已清理 {removed} 个备份目录，空间已释放。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                refreshAfter = true;
            }
            catch (Exception ex)
            {
                _lblProgress.Text = "清理失败";
                Log(LogLevel.Error, "清理失败：" + ex.Message);
                MessageBox.Show(this, "清理未完成：" + ex.Message, "清理失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 0;
                SetBusy(false);
                UpdateRollbackAvailability();
                if (refreshAfter) await LoadSnapshotAsync();
            }
        }

        // ======================= 自检 / 菜单动作 =======================

        private async Task RunSelfTestAsync()
        {
            if (_busy) return;

            if (MessageBox.Show(this,
                    "自检会在系统临时目录里新建一个沙箱，创建/复制/删除临时数据，\r\n" +
                    "不会读取或修改真实的 .workbuddy 目录。\r\n\r\n继续吗？",
                    "运行环境自检", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            SetBusy(true, cancelable: false);
            _bar.Style = ProgressBarStyle.Marquee;
            _lblProgress.Text = "正在运行自检…";

            try
            {
                int failed = await Task.Run(() => SelfTest.Run(m => Log(LogLevel.Info, m)));

                _lblProgress.Text = failed == 0 ? "自检全部通过" : $"自检有 {failed} 项失败";

                MessageBox.Show(this,
                    failed == 0
                        ? "自检全部通过。详细输出见下方日志区。"
                        : $"自检有 {failed} 项失败，详细输出见下方日志区。",
                    "自检结果",
                    MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = 0;
                SetBusy(false);
            }
        }

        private void OpenDataDir()
        {
            string dir = _snapshot?.Root ?? WorkBuddyPaths.ResolveConfigDir();
            if (!Directory.Exists(LongPath.Fix(dir)))
            {
                MessageBox.Show(this, "目录不存在：" + dir, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch (Exception ex) { Log(LogLevel.Error, "打开目录失败：" + ex.Message); }
        }

        private void ViewRecord()
        {
            string path = WorkBuddyPaths.LastRecordPath;
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "还没有迁移记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log(LogLevel.Error, "打开记录失败：" + ex.Message); }
        }

        private void OpenRecordFolder()
        {
            string dir = WorkBuddyPaths.RecordDirectory;
            Directory.CreateDirectory(dir);
            try { Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch (Exception ex) { Log(LogLevel.Error, "打开文件夹失败：" + ex.Message); }
        }

        private void ShowHelp()
        {
            string text =
                "使用步骤\r\n" +
                "────────────────────────\r\n" +
                "1. 从托盘右键完全退出 WorkBuddy，并在任务管理器中确认没有残留进程。\r\n" +
                "2. 在「目标位置」选择另一个本地磁盘（NTFS / ReFS），工具会自动在下面追加 .workbuddy。\r\n" +
                "3. 选择迁移方式：\r\n" +
                "     · 目录联接（推荐）—— 对启动方式透明，不改任何配置。\r\n" +
                "     · 环境变量 —— 不在原位置留联接，需注销重登。\r\n" +
                "     · 部分迁移 —— 只搬缓存和可重下的目录，核心数据留在 C 盘。\r\n" +
                "4. 先点「仅预检」确认没有阻断项，再点「开始迁移」。\r\n" +
                "5. 启动 WorkBuddy 确认一切正常后，再点「清理备份」释放空间。\r\n" +
                "6. 出现问题点「一键回滚」，可完整还原。\r\n\r\n" +
                "安全设计\r\n" +
                "────────────────────────\r\n" +
                "· 迁移顺序固定：复制 → 逐目录校验 → 原目录改名 → 建联接 → 写回滚记录。\r\n" +
                "· 原目录只会被改名成 .moved-<时间>，工具从不自动删除。\r\n" +
                "· 第 4 步失败会自动把原名改回去。\r\n" +
                "· 移除联接走 RemoveDirectory，并在删除前校验它确实是联接。\r\n" +
                "· 全程使用 \\\\?\\ 长路径，跳过重解析点，不依赖注册表长路径开关。\r\n\r\n" +
                "限制\r\n" +
                "────────────────────────\r\n" +
                "· 目标盘符必须固定，移动硬盘盘符漂移会导致联接失效。\r\n" +
                "· 不支持多台电脑共用同一数据目录（凭据、device-id、数据库都会冲突）。\r\n" +
                "· 「工具 → 运行环境自检」只在临时沙箱里跑，不碰真实数据。";

            using (var f = new Form())
            {
                f.Text = "使用说明";
                f.ClientSize = new Size(680, 560);
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.Font = Font;

                var box = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    Text = text,
                    BorderStyle = BorderStyle.None,
                    Font = new Font("Microsoft YaHei UI", 9F),
                    BackColor = Color.White,
                    Margin = new Padding(0),
                };
                f.Controls.Add(box);
                f.ShowDialog(this);
            }
        }

        private void ShowAbout()
        {
            MessageBox.Show(this,
                "WorkBuddy 数据目录迁移工具  v1.0\r\n\r\n" +
                "把 .workbuddy 数据目录从 C 盘安全搬到其他盘（含移动硬盘）。\r\n" +
                "带预检、带逐字节校验、带一键回滚，破坏性操作全部可逆。\r\n\r\n" +
                "技术栈：.NET 6 + WinForms；目录联接通过 Win32 API 创建，不依赖 mklink。\r\n\r\n" +
                "回滚记录：" + WorkBuddyPaths.LastRecordPath,
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ======================= 通用 =======================

        private void SetBusy(bool busy, bool cancelable = true)
        {
            _busy = busy;
            UseWaitCursor = busy;

            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            _btnPreflight.Enabled = !busy;
            _btnMigrate.Enabled = !busy;
            _btnCancel.Enabled = busy && cancelable;

            if (busy)
            {
                _btnRollback.Enabled = false;
                _btnCleanup.Enabled = false;
            }

            _cboDrive.Enabled = !busy;
            _txtTarget.Enabled = !busy;
            _rbJunction.Enabled = !busy;
            _rbEnv.Enabled = !busy;
            _rbPartial.Enabled = !busy;
            _chkMerge.Enabled = !busy;

            if (busy)
            {
                _clb.Enabled = false;
                _btnPreCache.Enabled = false;
                _btnPreCacheRedl.Enabled = false;
                _btnPre100.Enabled = false;
                _btnPreClear.Enabled = false;
            }
            else
            {
                UpdateModeEnabled();
                UpdateRollbackAvailability();
            }
        }

        private void Log(LogLevel level, string message)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => Log(level, message))); } catch { }
                return;
            }
            AppendLog(level, message);
        }

        private void AppendLog(LogLevel level, string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            Color color;
            switch (level)
            {
                case LogLevel.Step: color = Color.FromArgb(20, 90, 170); break;
                case LogLevel.Ok: color = Color.FromArgb(22, 128, 80); break;
                case LogLevel.Warn: color = Color.FromArgb(178, 110, 20); break;
                case LogLevel.Error: color = Color.FromArgb(190, 50, 50); break;
                default: color = Color.FromArgb(60, 64, 72); break;
            }

            string prefix = level == LogLevel.Step ? "" : "  ";
            string line = prefix + message;

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = color;

            if (level == LogLevel.Step)
                _log.SelectionFont = new Font(_log.Font, FontStyle.Bold);

            _log.AppendText(line + Environment.NewLine);
            _log.SelectionFont = new Font(_log.Font, FontStyle.Regular);
            _log.SelectionColor = _log.ForeColor;
            _log.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                if (MessageBox.Show(this,
                        "任务正在执行中。现在关闭会中断操作（源数据不会被删除，但可能需要手动处理中间状态）。\r\n确定关闭吗？",
                        "确认关闭", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancelRequested = true;
            }

            base.OnFormClosing(e);
        }
    }
}
