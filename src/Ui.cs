using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSCodeDataMover
{
    public class MainForm : Form
    {
        private ListView list;
        private TextBox txtTarget;
        private Button btnBrowse;
        private Button btnRefresh;
        private Button btnMigrate;
        private Button btnRestore;
        private Button btnCloseCode;
        private Button btnLog;
        private Button btnHelp;
        private Button btnRepair;
        private Button btnUninstall;
        private CheckBox chkDeep;
        private ProgressBar bar;
        private Label lblStatus;
        private Label lblInfo;
        private TextBox txtLog;
        private ToolTip tip;

        private List<Entry> entries = new List<Entry>();
        private Log log;
        private bool busy;
        private bool startupChecked;
        private string installPath;

        public MainForm()
        {
            Text = "VS Code 数据迁移工具  v1.0";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1060, 760);
            MinimumSize = new Size(920, 680);
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            log = new Log(Path.Combine(Paths.StateDir, "logs", "mover-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"));

            BuildUi();
            Load += delegate { RefreshDetection(); };
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.Padding = new Padding(10);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));

            lblInfo = new Label();
            lblInfo.Dock = DockStyle.Fill;
            lblInfo.Text = "正在检测本机 VS Code 数据位置……";
            lblInfo.AutoEllipsis = true;
            root.Controls.Add(lblInfo, 0, 0);

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = false;
            list.ShowItemToolTips = true;
            list.Columns.Add("项目", 200);
            list.Columns.Add("状态", 70);
            list.Columns.Add("源位置（C 盘）", 270);
            list.Columns.Add("当前位置 / 迁移后", 310);
            list.Columns.Add("大小", 130);
            list.DoubleClick += delegate { OpenSelectedInExplorer(); };
            root.Controls.Add(list, 0, 1);

            GroupBox gbTarget = new GroupBox();
            gbTarget.Text = "迁移目标（建议选择 D 盘或 E 盘的空文件夹，工具会自动创建子目录）";
            gbTarget.Dock = DockStyle.Fill;

            txtTarget = new TextBox();
            txtTarget.Location = new Point(12, 24);
            txtTarget.Height = 28;
            txtTarget.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            txtTarget.TextChanged += delegate { UpdateButtons(); };

            btnBrowse = new Button();
            btnBrowse.Text = "浏览…";
            btnBrowse.AutoSize = true;
            btnBrowse.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btnBrowse.MinimumSize = new Size(92, 30);
            btnBrowse.Padding = new Padding(10, 4, 10, 4);
            btnBrowse.TextAlign = ContentAlignment.MiddleCenter;
            btnBrowse.Top = 22;
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Click += delegate { BrowseTarget(); };

            chkDeep = new CheckBox();
            chkDeep.Text = "深度校验（逐文件 SHA256，更慢但更稳）";
            chkDeep.Location = new Point(12, 60);
            chkDeep.AutoSize = true;
            chkDeep.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

            gbTarget.Controls.Add(txtTarget);
            gbTarget.Controls.Add(btnBrowse);
            gbTarget.Controls.Add(chkDeep);
            gbTarget.Resize += delegate
            {
                btnBrowse.Left = gbTarget.ClientSize.Width - btnBrowse.Width - 12;
                txtTarget.Width = gbTarget.ClientSize.Width - txtTarget.Left - btnBrowse.Width - 24;
                chkDeep.Top = gbTarget.ClientSize.Height - chkDeep.Height - 10;
            };
            root.Controls.Add(gbTarget, 0, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;

            btnRefresh = MakeButton("重新检测", 92);
            btnMigrate = MakeButton("一键迁移", 110);
            btnRestore = MakeButton("一键还原", 110);
            btnCloseCode = MakeButton("关闭 VS Code", 110);
            btnLog = MakeButton("打开日志", 92);
            btnHelp = MakeButton("使用说明", 92);
            btnRepair = MakeButton("一键修复…", 110);
            btnUninstall = MakeButton("卸载…", 90);

            btnRefresh.Click += delegate { RefreshDetection(); };
            btnMigrate.Click += delegate { StartMigrate(); };
            btnRestore.Click += delegate { StartRestore(); };
            btnCloseCode.Click += delegate { CloseVsCode(); };
            btnLog.Click += delegate { OpenLogFolder(); };
            btnHelp.Click += delegate { ShowHelp(); };
            btnRepair.Click += delegate { ShowRepair(); };
            btnUninstall.Click += delegate { StartUninstall(); };

            buttons.Controls.Add(btnRefresh);
            buttons.Controls.Add(btnMigrate);
            buttons.Controls.Add(btnRestore);
            buttons.Controls.Add(btnCloseCode);
            buttons.Controls.Add(btnLog);
            buttons.Controls.Add(btnHelp);
            buttons.Controls.Add(btnRepair);
            buttons.Controls.Add(btnUninstall);
            root.Controls.Add(buttons, 0, 3);

            TableLayoutPanel progressPanel = new TableLayoutPanel();
            progressPanel.Dock = DockStyle.Fill;
            progressPanel.ColumnCount = 2;
            progressPanel.RowCount = 1;
            progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            bar = new ProgressBar();
            bar.Dock = DockStyle.Fill;
            bar.Minimum = 0;
            bar.Maximum = 100;
            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.Text = "就绪";
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            progressPanel.Controls.Add(bar, 0, 0);
            progressPanel.Controls.Add(lblStatus, 1, 0);
            root.Controls.Add(progressPanel, 0, 4);

            GroupBox gbLog = new GroupBox();
            gbLog.Text = "运行日志";
            gbLog.Dock = DockStyle.Fill;
            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.White;
            txtLog.Font = new Font("Consolas", 8.5F);
            gbLog.Controls.Add(txtLog);
            root.Controls.Add(gbLog, 0, 5);

            Controls.Add(root);

            tip = new ToolTip();
            tip.SetToolTip(chkDeep, "默认快速校验：文件数量、大小、关键文件哈希。深度校验会逐个文件算 SHA256，适合迁移后首次验证。");
            tip.SetToolTip(txtTarget, "例如 D:\\VS Code Data。工具会自动在该目录下创建 Roaming、User、Local 等子目录。");
        }

        private Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(width, 32);
            b.Padding = new Padding(8, 3, 8, 3);
            b.Margin = new Padding(0, 6, 8, 0);
            return b;
        }

        private void BrowseTarget()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择迁移目标文件夹（建议 D 盘或 E 盘）";
                dlg.ShowNewFolderButton = true;
                string current = txtTarget.Text.Trim();
                if (current.Length > 0 && Directory.Exists(current)) dlg.SelectedPath = current;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtTarget.Text = dlg.SelectedPath;
                }
            }
        }

        private void RefreshDetection()
        {
            if (busy) return;
            busy = true;
            UpdateButtons();
            lblStatus.Text = "正在检测…";
            bar.Value = 0;

            Task.Factory.StartNew(delegate
            {
                Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
                List<Entry> detected = Locator.Detect(state);
                foreach (Entry e in detected) Locator.Measure(e);
                string install = Locator.DetectInstallPath("VS Code");
                return new object[] { detected, install };
            }).ContinueWith(delegate(Task<object[]> t)
            {
                busy = false;
                if (t.IsFaulted)
                {
                    lblStatus.Text = "检测失败";
                    AppendLog("检测失败: " + t.Exception.GetBaseException().Message);
                    UpdateButtons();
                    return;
                }
                entries = (List<Entry>)t.Result[0];
                installPath = (string)t.Result[1];
                FillList();
                lblInfo.Text = BuildInfo();
                if (txtTarget.Text.Trim().Length == 0) txtTarget.Text = SuggestTarget();
                lblStatus.Text = "就绪";
                UpdateButtons();
                RunStartupCheck();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void FillList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Entry e in entries)
            {
                ListViewItem item = new ListViewItem(e.DisplayName);
                item.SubItems.Add(e.StateText);
                item.SubItems.Add(e.Source);
                item.SubItems.Add(string.IsNullOrEmpty(e.Target) ? "—" : e.Target);
                item.SubItems.Add(FormatSize(e.SizeBytes));
                item.Tag = e;
                item.ToolTipText = e.Source +
                    (string.IsNullOrEmpty(e.Target) ? "" : Environment.NewLine + "-> " + e.Target) +
                    Environment.NewLine + e.FileCount + " 个文件";

                if (e.State == EntryState.Migrated) item.ForeColor = Color.FromArgb(0, 90, 170);
                else if (e.State == EntryState.BrokenTargetMissing || e.State == EntryState.InterruptedRename) item.ForeColor = Color.FromArgb(190, 30, 30);
                else if (e.State == EntryState.RestoreLeftover || e.State == EntryState.CopyLeftover) item.ForeColor = Color.FromArgb(180, 110, 0);

                list.Items.Add(item);
            }
            list.EndUpdate();
        }

        private string BuildInfo()
        {
            string install = string.IsNullOrEmpty(installPath) ? "未检测到（可能只安装了绿色版）" : installPath;
            string portable = Locator.IsPortable(installPath) ? "  ！检测到安装目录下有 data 文件夹，可能已启用便携模式。" : "";
            int migrated = 0;
            int normal = 0;
            int bad = 0;
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Migrated) migrated++;
                else if (e.State == EntryState.Normal) normal++;
                else if (e.State == EntryState.BrokenTargetMissing || e.State == EntryState.InterruptedRename || e.State == EntryState.CopyLeftover) bad++;
            }
            string running = Mover.RunningProcesses().Count > 0 ? "VS Code 正在运行（迁移前需要先关闭）。" : "VS Code 未运行。";
            return "VS Code 安装位置: " + install + portable + Environment.NewLine +
                   "共 " + entries.Count + " 项数据：" + migrated + " 项已迁移，" + normal + " 项未迁移，" + bad + " 项异常。" + running + Environment.NewLine +
                   "注意：本工具迁移的是“数据”（设置、扩展、缓存），不会移动 VS Code 程序安装目录。";
        }

        private string SuggestTarget()
        {
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Migrated && !string.IsNullOrEmpty(e.Target))
                {
                    try
                    {
                        string parent = Path.GetDirectoryName(e.Target);
                        if (parent != null)
                        {
                            string grand = Path.GetDirectoryName(parent);
                            string parentName = Path.GetFileName(parent);
                            if (string.Equals(parentName, "Roaming", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(parentName, "User", StringComparison.OrdinalIgnoreCase))
                                return grand;
                        }
                        return parent;
                    }
                    catch { }
                }
            }
            string[] drives = new string[] { "D:\\", "E:\\" };
            foreach (string d in drives)
            {
                try
                {
                    if (Directory.Exists(d)) return Path.Combine(d, "VS Code Data");
                }
                catch { }
            }
            return "";
        }

        private void StartMigrate()
        {
            if (busy) return;
            string target = txtTarget.Text.Trim();
            if (target.Length == 0)
            {
                MessageBox.Show(this, "请先选择迁移目标文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int count = 0;
            long size = 0;
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Normal) { count++; size += e.SizeBytes; }
            }
            if (count == 0)
            {
                MessageBox.Show(this, "没有可迁移的项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string message = "将迁移 " + count + " 项数据（" + FormatSize(size) + "）到:" + Environment.NewLine + target +
                             Environment.NewLine + Environment.NewLine +
                             "注意：迁移的是 VS Code 的“数据”（设置、扩展、缓存），不是程序安装目录。" + Environment.NewLine +
                             "迁移前备份会保存在 " + Path.Combine(target, "VSCodeDataMover", "backups") + "（额外占用约 " + FormatSize(size) + "）。" + Environment.NewLine +
                             "迁移前请确认：VS Code 已完全退出、重要文件已保存。" + Environment.NewLine +
                             "工具会先复制并校验，校验通过后才替换为目录联接；失败会自动回滚。";
            if (MessageBox.Show(this, message, "确认迁移", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            RunOperation(delegate
            {
                Mover m = new Mover();
                m.Log = log;
                m.Deep = chkDeep.Checked;
                m.Progress = OnProgress;
                return m.MigrateAll(entries, target);
            }, "迁移");
        }

        private void StartRestore()
        {
            if (busy) return;
            int count = 0;
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Migrated || e.State == EntryState.BrokenTargetMissing ||
                    e.State == EntryState.RestoreLeftover || e.State == EntryState.CopyLeftover) count++;
            }
            if (count == 0)
            {
                MessageBox.Show(this, "当前没有已迁移的项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string message = "将把 " + count + " 项数据从迁移位置搬回 C 盘原路径，并删除 D 盘副本。" + Environment.NewLine +
                             "迁移后产生的新改动会一并带回。" + Environment.NewLine + Environment.NewLine +
                             "请确认 VS Code 已完全退出。";
            if (MessageBox.Show(this, message, "确认还原", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            RunOperation(delegate
            {
                Mover m = new Mover();
                m.Log = log;
                m.Deep = chkDeep.Checked;
                m.Progress = OnProgress;
                return m.RestoreAll(entries);
            }, "还原");
        }

        private void RunOperation(Func<OpResult> work, string title)
        {
            busy = true;
            UpdateButtons();
            bar.Value = 0;
            lblStatus.Text = title + "中…";
            txtLog.Clear();

            Task.Factory.StartNew(delegate
            {
                try { return work(); }
                catch (Exception ex)
                {
                    OpResult r = new OpResult();
                    r.Ok = false;
                    r.Summary = title + "失败";
                    r.Add("错误: " + ex.Message);
                    return r;
                }
            }).ContinueWith(delegate(Task<OpResult> t)
            {
                busy = false;
                OpResult r = t.Result;
                AppendLog("");
                AppendLog("—— " + r.Summary + " ——");
                foreach (string line in r.Lines) AppendLog("  " + line);
                lblStatus.Text = r.Summary;
                UpdateButtons();
                MessageBox.Show(this, string.Join(Environment.NewLine, r.Lines.ToArray()),
                    r.Summary, MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                RefreshDetection();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnProgress(int percent, string message)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (percent < 0) percent = 0;
                    if (percent > 100) percent = 100;
                    bar.Value = percent;
                    lblStatus.Text = message;
                    AppendLog(message);
                }));
            }
            catch { }
        }

        private void CloseVsCode()
        {
            if (busy) return;
            if (Mover.RunningProcesses().Count == 0)
            {
                MessageBox.Show(this, "VS Code 当前没有运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "会向 VS Code 发送关闭请求（等同点击窗口右上角关闭）。" + Environment.NewLine +
                "如果有未保存的文件，VS Code 可能弹出保存提示。是否继续？", "关闭 VS Code",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            lblStatus.Text = "正在关闭 VS Code…";
            bool ok = Mover.CloseRunning(25000);
            lblStatus.Text = ok ? "VS Code 已关闭" : "VS Code 仍在运行，请手动退出";
            MessageBox.Show(this, ok ? "VS Code 已关闭，可以开始迁移。" : "VS Code 仍未退出，请手动关闭所有窗口后重试。",
                "关闭 VS Code", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            UpdateButtons();
        }

        private void OpenLogFolder()
        {
            try
            {
                string dir = Path.Combine(Paths.StateDir, "logs");
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开日志", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartUninstall()
        {
            if (busy) return;

            int migrated = 0;
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Migrated || e.State == EntryState.BrokenTargetMissing ||
                    e.State == EntryState.InterruptedRename || e.State == EntryState.CopyLeftover ||
                    e.State == EntryState.RestoreLeftover)
                    migrated++;
            }

            bool restoreFirst = false;
            if (migrated > 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "检测到 " + migrated + " 项数据仍在迁移状态（C 盘是目录联接）。" + Environment.NewLine + Environment.NewLine +
                    "“是”：先还原数据到 C 盘，再卸载（推荐）" + Environment.NewLine +
                    "“否”：保留迁移状态，只删除工具（VS Code 仍可正常使用）" + Environment.NewLine +
                    "“取消”：返回",
                    "卸载前确认", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (answer == DialogResult.Cancel) return;
                restoreFirst = (answer == DialogResult.Yes);
            }

            bool deleteExe = MessageBox.Show(this,
                "是否同时删除 VSCodeDataMover.exe？" + Environment.NewLine + Environment.NewLine +
                "删除后无法再用本工具；src、README、build.ps1 不受影响。",
                "删除程序", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

            string summary = "将执行：" + Environment.NewLine +
                (restoreFirst ? "· 还原数据到 C 盘" : "· 保留当前迁移状态（联接不动）") + Environment.NewLine +
                "· 删除工具数据（state / logs / backups）" + Environment.NewLine +
                "· 删除注册表项 HKCU\\Software\\VSCodeDataMover" + Environment.NewLine +
                (deleteExe ? "· 删除程序本身（退出后）" : "· 保留程序");
            if (MessageBox.Show(this, summary + Environment.NewLine + Environment.NewLine + "确定卸载吗？",
                "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            busy = true;
            UpdateButtons();
            lblStatus.Text = "正在卸载清理…";

            Task.Factory.StartNew(delegate
            {
                return Uninstaller.Run(entries, restoreFirst, deleteExe, log);
            }).ContinueWith(delegate(Task<OpResult> t)
            {
                busy = false;
                UpdateButtons();
                OpResult r = t.IsFaulted ? FailUninstall(t.Exception.GetBaseException().Message) : t.Result;
                MessageBox.Show(this, string.Join(Environment.NewLine, r.Lines.ToArray()), r.Summary,
                    MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (deleteExe && !t.IsFaulted && t.Result.Ok)
                {
                    Close();
                    return;
                }
                RefreshDetection();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static OpResult FailUninstall(string message)
        {
            OpResult r = new OpResult();
            r.Ok = false;
            r.Summary = "卸载失败";
            r.Add("错误: " + message);
            return r;
        }

        private void ShowRepair()
        {
            if (busy) return;
            using (RepairForm dlg = new RepairForm())
            {
                dlg.ShowDialog(this);
            }
            RefreshDetection();
        }

        private void RunStartupCheck()
        {
            if (startupChecked || busy || entries.Count == 0) return;
            startupChecked = true;

            Task.Factory.StartNew(delegate
            {
                return Doctor.Diagnose(Locator.Detect(StateStore.Load(), true), true);
            }).ContinueWith(delegate(Task<DoctorReport> t)
            {
                if (t.IsFaulted) return;
                DoctorReport r = t.Result;
                if (r.Problems <= 0) return;

                DialogResult answer = MessageBox.Show(this,
                    "启动体检发现 " + r.Problems + " 个问题（程序本体 / 快捷方式 / 注册表 / PATH / 数据）。" + Environment.NewLine +
                    "是否现在打开“一键修复”？",
                    "体检结果", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes) ShowRepair();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowHelp()
        {
            string readme = Path.Combine(Application.StartupPath, "README.md");
            if (File.Exists(readme))
            {
                try { Process.Start("notepad.exe", "\"" + readme + "\""); return; }
                catch { }
            }
            MessageBox.Show(this,
                "1. 本工具迁移的是 VS Code 的“数据”（设置、扩展、缓存），不会移动程序安装目录。\n" +
                "2. 工具会自动检测 VS Code 的用户数据、扩展、共享存储和 C++ 缓存。\n" +
                "3. 选择目标文件夹，点击“一键迁移”。\n" +
                "4. 还原时点击“一键还原”，数据会搬回 C 盘原路径。\n" +
                "5. 迁移前必须完全退出 VS Code。\n" +
                "6. 所有操作都会先复制校验，失败自动回滚。\n" +
                "7. 如果之前手动移动过数据导致异常，点“一键修复…”会自动检查并修复。\n" +
                "8. 不再使用时点“卸载…”：可先还原数据，再清除工具数据、注册表，并可删除程序本身。",
                "使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenSelectedInExplorer()
        {
            if (list.SelectedItems.Count == 0) return;
            Entry e = list.SelectedItems[0].Tag as Entry;
            if (e == null) return;
            try
            {
                string path = string.IsNullOrEmpty(e.Target) ? e.Source : e.Target;
                if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        private void UpdateButtons()
        {
            bool hasNormal = false;
            bool hasMigrated = false;
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Normal) hasNormal = true;
                if (e.State == EntryState.Migrated || e.State == EntryState.BrokenTargetMissing ||
                    e.State == EntryState.RestoreLeftover || e.State == EntryState.CopyLeftover) hasMigrated = true;
            }
            btnMigrate.Enabled = !busy && hasNormal && txtTarget.Text.Trim().Length > 0;
            btnRestore.Enabled = !busy && hasMigrated;
            btnRefresh.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            btnCloseCode.Enabled = !busy;
            btnRepair.Enabled = !busy;
            btnUninstall.Enabled = !busy;
            txtTarget.Enabled = !busy;
            chkDeep.Enabled = !busy;
        }

        private void AppendLog(string line)
        {
            if (txtLog.TextLength > 200000) txtLog.Clear();
            txtLog.AppendText(line + Environment.NewLine);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return bytes + " B";
        }
    }
}
