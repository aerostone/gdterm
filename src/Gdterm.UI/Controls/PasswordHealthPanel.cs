using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 密码健康分析面板 — 显示弱密码、重复密码、空密码、过期密码
    /// </summary>
    public class PasswordHealthPanel : UserControl
    {
        private readonly IKeePassService _keepass;
        private Panel _summaryPanel;
        private Label _scoreLabel;
        private Label _summaryLabel;
        private Label _statsLabel;
        private TabControl _issueTabs;
        private ListView _weakList;
        private ListView _duplicateList;
        private ListView _emptyList;
        private ListView _expiredList;
        private Button _refreshBtn;
        private Button _generateBtn;
        private PasswordHealthReport _currentReport;

        public PasswordHealthPanel(IKeePassService keepass)
        {
            _keepass = keepass;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            BackColor = Color.FromArgb(30, 30, 30);
            Font = new Font("Microsoft YaHei", 9f);

            // === 顶部摘要 ===
            _summaryPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 120,
                BackColor = Color.FromArgb(40, 40, 40),
                Padding = new Padding(16)
            };

            _scoreLabel = new Label
            {
                Text = "—",
                Font = new Font("Consolas", 36f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 200, 100),
                Location = new Point(16, 16),
                AutoSize = true
            };

            _summaryLabel = new Label
            {
                Text = "点击「分析」按钮开始密码健康检查",
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 11f),
                Location = new Point(160, 20),
                AutoSize = true,
                MaximumSize = new Size(600, 40)
            };

            _statsLabel = new Label
            {
                Text = "",
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = new Point(160, 60),
                AutoSize = true
            };

            _refreshBtn = new Button
            {
                Text = "分析",
                Size = new Size(80, 32),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _refreshBtn.Click += (s, e) => RefreshReport();

            _generateBtn = new Button
            {
                Text = "生成密码",
                Size = new Size(80, 32),
                BackColor = Color.FromArgb(60, 100, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _generateBtn.Click += (s, e) =>
            {
                var form = new PasswordGeneratorForm();
                form.ShowDialog(FindForm());
            };

            _summaryPanel.Controls.AddRange(new Control[]
            {
                _scoreLabel, _summaryLabel, _statsLabel, _refreshBtn, _generateBtn
            });

            // === 问题列表 Tabs ===
            _issueTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed
            };
            _issueTabs.DrawItem += (s, e) =>
            {
                var g = e.Graphics;
                var rect = e.Bounds;
                bool selected = e.Index == _issueTabs.SelectedIndex;
                g.FillRectangle(new SolidBrush(selected ? Color.FromArgb(50, 50, 50) : Color.FromArgb(35, 35, 35)), rect);
                var text = _issueTabs.TabPages[e.Index].Text;
                var textColor = selected ? Color.White : Color.FromArgb(180, 180, 180);
                using (var brush = new SolidBrush(textColor))
                    g.DrawString(text, Font, brush, rect.X + 8, rect.Y + 6);
            };

            _weakList = CreateIssueList("弱密码");
            _duplicateList = CreateIssueList("重复密码");
            _emptyList = CreateIssueList("空密码");
            _expiredList = CreateIssueList("过期密码");

            _issueTabs.TabPages.Add(CreateTab("弱密码 (0)", _weakList));
            _issueTabs.TabPages.Add(CreateTab("重复 (0)", _duplicateList));
            _issueTabs.TabPages.Add(CreateTab("空密码 (0)", _emptyList));
            _issueTabs.TabPages.Add(CreateTab("过期 (0)", _expiredList));

            Controls.Add(_issueTabs);
            Controls.Add(_summaryPanel);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            _refreshBtn.Location = new Point(Width - 200, 16);
            _generateBtn.Location = new Point(Width - 110, 16);
        }

        private ListView CreateIssueList(string column)
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                Font = new Font("Consolas", 9f)
            };
            list.Columns.Add("标题", 160);
            list.Columns.Add("用户名", 120);
            list.Columns.Add("分组", 140);
            list.Columns.Add("问题", 200);
            list.Columns.Add("强度", 60);
            return list;
        }

        private TabPage CreateTab(string text, ListView list)
        {
            var page = new TabPage(text)
            {
                BackColor = Color.FromArgb(30, 30, 30)
            };
            page.Controls.Add(list);
            return page;
        }

        public void RefreshReport()
        {
            if (!_keepass.IsUnlocked)
            {
                MessageBox.Show("请先解锁密码库", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _currentReport = _keepass.AnalyzeHealth();
                DisplayReport(_currentReport);
            }
            catch (Exception ex)
            {
                MessageBox.Show("分析失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DisplayReport(PasswordHealthReport report)
        {
            // 评分
            _scoreLabel.Text = report.HealthScore.ToString();
            if (report.HealthScore >= 90)
                _scoreLabel.ForeColor = Color.FromArgb(100, 200, 100);
            else if (report.HealthScore >= 70)
                _scoreLabel.ForeColor = Color.FromArgb(200, 200, 100);
            else if (report.HealthScore >= 50)
                _scoreLabel.ForeColor = Color.FromArgb(220, 150, 50);
            else
                _scoreLabel.ForeColor = Color.FromArgb(220, 80, 80);

            _summaryLabel.Text = report.Summary;
            _statsLabel.Text = $"总计 {report.TotalEntries} 个条目 | 弱密码 {report.WeakPasswords.Count} | " +
                               $"重复 {report.DuplicatePasswords.Count} | 空密码 {report.EmptyPasswords.Count} | " +
                               $"过期 {report.ExpiredPasswords.Count}";

            // 弱密码
            _weakList.Items.Clear();
            foreach (var issue in report.WeakPasswords)
                _weakList.Items.Add(CreateListViewItem(issue));

            // 空密码
            _emptyList.Items.Clear();
            foreach (var issue in report.EmptyPasswords)
                _emptyList.Items.Add(CreateListViewItem(issue));

            // 过期密码
            _expiredList.Items.Clear();
            foreach (var issue in report.ExpiredPasswords)
                _expiredList.Items.Add(CreateListViewItem(issue));

            // 重复密码
            _duplicateList.Items.Clear();
            foreach (var group in report.DuplicatePasswords)
            {
                foreach (var entry in group.Entries)
                {
                    var item = CreateListViewItem(entry);
                    item.SubItems.Add(group.PasswordHash);
                    _duplicateList.Items.Add(item);
                }
            }

            // 更新 Tab 标题
            _issueTabs.TabPages[0].Text = $"弱密码 ({report.WeakPasswords.Count})";
            _issueTabs.TabPages[1].Text = $"重复 ({report.DuplicatePasswords.Count})";
            _issueTabs.TabPages[2].Text = $"空密码 ({report.EmptyPasswords.Count})";
            _issueTabs.TabPages[3].Text = $"过期 ({report.ExpiredPasswords.Count})";
        }

        private ListViewItem CreateListViewItem(PasswordIssue issue)
        {
            var item = new ListViewItem(issue.Title ?? "");
            item.SubItems.Add(issue.Username ?? "");
            item.SubItems.Add(issue.GroupPath ?? "");
            item.SubItems.Add(issue.Issue ?? "");
            item.SubItems.Add(issue.StrengthScore.ToString());
            item.Tag = issue.EntryId;

            // 颜色编码
            if (issue.StrengthScore <= 20)
                item.ForeColor = Color.FromArgb(220, 80, 80);
            else if (issue.StrengthScore <= 40)
                item.ForeColor = Color.FromArgb(220, 150, 50);

            return item;
        }
    }
}
