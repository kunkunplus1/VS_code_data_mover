using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSCodeDataMover
{
    /// <summary>
    /// 统一的“修复”窗口：程序本体 / 快捷方式 / 注册表 / PATH / 数据联接，
    /// 以及数据丢失时的历史目录候选，全部在一个流程里完成。
    /// </summary>
    public class RepairForm : Form
    {
        private List<Entry> entries = new List<Entry>();
        private DoctorReport report;
        private ListView listFindings;
        private ListView listCandidates;
        private Label lblInfo;
        private Label lblCandidates;
        private Button btnScan;
        private Button btnFix;
        private Button btnClose;
        private Log log;
        private bool busy;

        public RepairForm()
        {
            Text = "一键修复 - 程序 / 快捷方式 / 数据";
            ClientSize = new Size(1020, 660);
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;

            log = new Log(Path.Combine(Paths.StateDir, "logs", "repair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"));

            BuildUi();
            Load += delegate { Scan(); };
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(10);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            lblInfo = new Label();
            lblInfo.Dock = DockStyle.Fill;
            lblInfo.Text = "正在检查……";
            lblInfo.AutoEllipsis = true;
            root.Controls.Add(lblInfo, 0, 0);

            listFindings = new ListView();
            listFindings.Dock = DockStyle.Fill;
            listFindings.View = View.Details;
            listFindings.FullRowSelect = true;
            listFindings.GridLines = true;
            listFindings.HideSelection = false;
            listFindings.ShowItemToolTips = true;
            listFindings.Columns.Add("级别", 70);
            listFindings.Columns.Add("问题", 250);
            listFindings.Columns.Add("详情", 520);
            listFindings.Columns.Add("修复方式", 90);
            root.Controls.Add(listFindings, 0, 1);

            lblCandidates = new Label();
            lblCandidates.Dock = DockStyle.Fill;
            lblCandidates.Text = "历史数据候选：勾选后，“一键修复”会用它们恢复对应数据项（恢复前会备份当前数据）。";
            root.Controls.Add(lblCandidates, 0, 2);

            listCandidates = new ListView();
            listCandidates.Dock = DockStyle.Fill;
            listCandidates.View = View.Details;
            listCandidates.CheckBoxes = true;
            listCandidates.FullRowSelect = true;
            listCandidates.GridLines = true;
            listCandidates.ShowItemToolTips = true;
            listCandidates.Columns.Add("数据项", 150);
            listCandidates.Columns.Add("线索来源", 110);
            listCandidates.Columns.Add("候选目录", 360);
            listCandidates.Columns.Add("修改时间", 110);
            listCandidates.Columns.Add("大小", 80);
            listCandidates.Columns.Add("说明", 150);
            listCandidates.ItemCheck += Candidates_ItemCheck;
            listCandidates.ItemChecked += delegate { UpdateButtons(); };
            root.Controls.Add(listCandidates, 0, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;

            btnScan = MakeButton("重新检查", 96);
            btnFix = MakeButton("一键修复", 110);
            btnClose = MakeButton("关闭", 80);

            btnScan.Click += delegate { Scan(); };
            btnFix.Click += delegate { FixAll(); };
            btnClose.Click += delegate { Close(); };

            buttons.Controls.Add(btnScan);
            buttons.Controls.Add(btnFix);
            buttons.Controls.Add(btnClose);
            root.Controls.Add(buttons, 0, 4);

            Controls.Add(root);
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

        private void Scan()
        {
            if (busy) return;
            busy = true;
            UpdateButtons();
            lblInfo.Text = "正在检查程序本体、快捷方式、注册表、PATH、数据联接和历史线索……";
            listFindings.Items.Clear();
            listCandidates.Items.Clear();

            Task.Factory.StartNew(delegate
            {
                List<Entry> scanEntries = Locator.Detect(StateStore.Load(), true);
                DoctorReport r = Doctor.Diagnose(scanEntries, true);
                return new object[] { scanEntries, r };
            }).ContinueWith(delegate(Task<object[]> t)
            {
                busy = false;
                if (t.IsFaulted)
                {
                    lblInfo.Text = "检查失败: " + t.Exception.GetBaseException().Message;
                    UpdateButtons();
                    return;
                }

                entries = (List<Entry>)t.Result[0];
                report = (DoctorReport)t.Result[1];
                FillFindings();
                FillCandidates();
                lblInfo.Text = "发现 " + report.Problems + " 个问题：自动可修 " + report.AutoFixable +
                               " 项，历史恢复待选 " + report.HistoryPending + " 项。";
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void FillFindings()
        {
            listFindings.BeginUpdate();
            listFindings.Items.Clear();
            foreach (DoctorFinding f in report.Findings)
            {
                ListViewItem item = new ListViewItem(f.Severity);
                item.SubItems.Add(f.Title);
                item.SubItems.Add(f.Detail.Replace("\r", " ").Replace("\n", " "));
                item.SubItems.Add(ActionLabel(f.Action));
                item.Tag = f;

                if (f.Severity == "错误") item.ForeColor = Color.FromArgb(190, 30, 30);
                else if (f.Severity == "警告") item.ForeColor = Color.FromArgb(180, 110, 0);
                else if (f.Severity == "正常") item.ForeColor = Color.FromArgb(0, 110, 60);

                listFindings.Items.Add(item);
            }
            listFindings.EndUpdate();
        }

        private static string ActionLabel(string action)
        {
            if (string.Equals(action, "shortcut", StringComparison.OrdinalIgnoreCase)) return "自动";
            if (string.Equals(action, "registry", StringComparison.OrdinalIgnoreCase)) return "自动";
            if (string.Equals(action, "path", StringComparison.OrdinalIgnoreCase)) return "自动";
            if (string.Equals(action, "data", StringComparison.OrdinalIgnoreCase)) return "自动";
            if (string.Equals(action, "history", StringComparison.OrdinalIgnoreCase)) return "选历史";
            return "—";
        }

        private void FillCandidates()
        {
            listCandidates.BeginUpdate();
            listCandidates.Items.Clear();
            foreach (HistoryCandidate c in report.Candidates)
            {
                ListViewItem item = new ListViewItem(c.EntryName);
                item.SubItems.Add(c.Source);
                item.SubItems.Add(c.Path);
                item.SubItems.Add(c.LastWrite == DateTime.MinValue ? "—" : c.LastWrite.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(FormatSize(c.Size));
                item.SubItems.Add(c.Note);
                item.Tag = c;
                item.ToolTipText = c.Path + Environment.NewLine + c.Note;
                if (c.Confidence == "高") item.ForeColor = Color.FromArgb(0, 110, 60);
                else if (c.Confidence == "中") item.ForeColor = Color.FromArgb(0, 90, 170);
                listCandidates.Items.Add(item);
            }
            listCandidates.EndUpdate();
            lblCandidates.Visible = report.Candidates.Count > 0;
            listCandidates.Visible = true;
        }

        private void Candidates_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.NewValue != CheckState.Checked) return;
            HistoryCandidate current = listCandidates.Items[e.Index].Tag as HistoryCandidate;
            if (current == null) return;

            foreach (ListViewItem item in listCandidates.Items)
            {
                if (item.Index == e.Index) continue;
                HistoryCandidate c = item.Tag as HistoryCandidate;
                if (c == null) continue;
                if (!string.Equals(c.EntryKey, current.EntryKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Checked) item.Checked = false;
            }
        }

        private void FixAll()
        {
            if (busy || report == null) return;

            Dictionary<string, string> chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in listCandidates.Items)
            {
                if (!item.Checked) continue;
                HistoryCandidate c = item.Tag as HistoryCandidate;
                if (c != null) chosen[c.EntryKey] = c.Path;
            }

            if (report.AutoFixable == 0 && chosen.Count == 0)
            {
                MessageBox.Show(this, "当前没有可执行的修复项。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string msg = "将执行：" + Environment.NewLine +
                         "· 自动修复 " + report.AutoFixable + " 项（快捷方式 / 注册表 / PATH / 数据联接）" + Environment.NewLine +
                         "· 用选中的历史目录恢复 " + chosen.Count + " 项数据" + Environment.NewLine + Environment.NewLine +
                         "注意：只修复“数据”和“指向程序的路径”，不会重新生成丢失的程序文件。" + Environment.NewLine +
                         "如果程序本体确实不存在，请先重新安装 VS Code。确定继续吗？";
            if (MessageBox.Show(this, msg, "确认一键修复", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            busy = true;
            UpdateButtons();
            lblInfo.Text = "正在修复……";

            Task.Factory.StartNew(delegate
            {
                return Doctor.Fix(report, entries, chosen, log);
            }).ContinueWith(delegate(Task<OpResult> t)
            {
                busy = false;
                OpResult r = t.IsFaulted ? Fail(t.Exception.GetBaseException().Message) : t.Result;
                MessageBox.Show(this, string.Join(Environment.NewLine, r.Lines.ToArray()), r.Summary,
                    MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                Scan();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static OpResult Fail(string message)
        {
            OpResult r = new OpResult();
            r.Ok = false;
            r.Summary = "修复失败";
            r.Add("错误: " + message);
            return r;
        }

        private void UpdateButtons()
        {
            int checkedCount = 0;
            foreach (ListViewItem item in listCandidates.Items)
            {
                if (item.Checked) checkedCount++;
            }
            bool canFix = !busy && report != null && (report.AutoFixable > 0 || checkedCount > 0);
            btnScan.Enabled = !busy;
            btnFix.Enabled = canFix;
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
