using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.Security;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 密码健康报告对话框
    /// 显示：弱密码/重复密码/空密码/过期密码/健康评分
    /// 需要主密码二次验证才能打开
    /// </summary>
    public class PasswordHealthForm : Form
    {
        private readonly IKeePassService _keepassService;
        private Label _scoreLabel;
        private Label _summaryLabel;
        private TabControl _tabControl;

        public PasswordHealthForm(IKeePassService keepassService)
        {
            _keepassService = keepassService;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            LoadReport();
        }

        private void InitializeComponent()
        {
            Text = "密码健康报告";
            Size = new Size(700, 550);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(35, 35, 35);

            // 顶部评分区
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                BackColor = Color.FromArgb(45, 45, 45),
                Padding = new Padding(15, 10, 15, 5)
            };

            _scoreLabel = new Label
            {
                Text = "健康评分：—",
                Font = new Font("Microsoft YaHei", 18f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(15, 10),
                Size = new Size(250, 35)
            };

            _summaryLabel = new Label
            {
                Text = "正在分析...",
                Font = new Font("Microsoft YaHei", 10f),
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = new Point(15, 48),
                Size = new Size(650, 25)
            };

            headerPanel.Controls.AddRange(new Control[] { _scoreLabel, _summaryLabel });

            // 标签页容器
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 9.5f)
            };

            Controls.Add(_tabControl);
            Controls.Add(headerPanel);
        }

        private void LoadReport()
        {
            try
            {
                var report = _keepassService.AnalyzeHealth();
                DisplayReport(report);
            }
            catch (Exception ex)
            {
                _scoreLabel.Text = "分析失败";
                _scoreLabel.ForeColor = Color.FromArgb(255, 100, 100);
                _summaryLabel.Text = ex.Message;
            }
        }

        private void DisplayReport(PasswordHealthReport report)
        {
            // 评分颜色
            Color scoreColor;
            if (report.HealthScore >= 90) scoreColor = Color.FromArgb(80, 220, 80);
            else if (report.HealthScore >= 70) scoreColor = Color.FromArgb(255, 200, 60);
            else if (report.HealthScore >= 50) scoreColor = Color.FromArgb(255, 150, 50);
            else scoreColor = Color.FromArgb(255, 80, 80);

            _scoreLabel.Text = $"健康评分：{report.HealthScore}/100";
            _scoreLabel.ForeColor = scoreColor;
            _summaryLabel.Text = $"{report.Summary}（共 {report.TotalEntries} 个条目）";

            // 清空标签页
            _tabControl.TabPages.Clear();

            // 空密码标签页
            AddIssueTab("空密码", report.EmptyPasswords, Color.FromArgb(255, 80, 80));

            // 弱密码标签页
            AddIssueTab("弱密码", report.WeakPasswords, Color.FromArgb(255, 150, 50));

            // 重复密码标签页
            AddDuplicateTab("重复密码", report.DuplicatePasswords);

            // 过期密码标签页
            AddIssueTab("过期密码", report.ExpiredPasswords, Color.FromArgb(255, 200, 60));
        }

        private void AddIssueTab(string name, System.Collections.Generic.IList<PasswordIssue> issues, Color color)
        {
            var tab = new TabPage($"  {name} ({issues.Count})  ");
            tab.BackColor = Color.FromArgb(35, 35, 35);

            var listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White
            };

            listView.Columns.Add("标题", 180);
            listView.Columns.Add("用户名", 120);
            listView.Columns.Add("分组", 120);
            listView.Columns.Add("问题", 150);
            listView.Columns.Add("强度", 60);

            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    var item = new ListViewItem(issue.Title ?? "(无标题)");
                    item.SubItems.Add(issue.Username ?? "");
                    item.SubItems.Add(issue.GroupPath ?? "");
                    item.SubItems.Add(issue.Issue ?? "");
                    item.SubItems.Add(issue.StrengthScore.ToString());
                    item.ForeColor = color;
                    listView.Items.Add(item);
                }
            }

            tab.Controls.Add(listView);
            _tabControl.TabPages.Add(tab);
        }

        private void AddDuplicateTab(string name, System.Collections.Generic.IList<DuplicatePasswordGroup> groups)
        {
            var tab = new TabPage($"  {name} ({groups.Count} 组)  ");
            tab.BackColor = Color.FromArgb(35, 35, 35);

            var listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White
            };

            listView.Columns.Add("密码哈希", 120);
            listView.Columns.Add("条目标题", 180);
            listView.Columns.Add("用户名", 120);
            listView.Columns.Add("分组", 120);

            foreach (var group in groups)
            {
                bool first = true;
                foreach (var entry in group.Entries)
                {
                    var item = new ListViewItem(first ? group.PasswordHash : "");
                    item.SubItems.Add(entry.Title ?? "(无标题)");
                    item.SubItems.Add(entry.Username ?? "");
                    item.SubItems.Add(entry.GroupPath ?? "");
                    item.ForeColor = Color.FromArgb(255, 150, 50);
                    listView.Items.Add(item);
                    first = false;
                }
            }

            tab.Controls.Add(listView);
            _tabControl.TabPages.Add(tab);
        }
    }
}
