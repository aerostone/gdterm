using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 密码健康报告对话框
    /// 显示：弱密码/重复密码/空密码/过期密码/健康评分
    /// 需要主密码二次验证才能打开
    /// </summary>
    public class PasswordHealthForm : AntdUI.Window
    {
        private readonly IKeePassService _keepassService;
        private AntdUI.Label _scoreLabel;
        private AntdUI.Label _summaryLabel;
        private AntdUI.Tabs _tabControl;

        public PasswordHealthForm(IKeePassService keepassService)
        {
            _keepassService = keepassService;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadReport();
        }

        private void InitializeComponent()
        {
            Text = "密码健康报告";
            Size = DpiScale.S(this, 700, 550);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = GdtermColorTable.Surface;

            // 顶部评分区
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(15, 10, 15, 5)
            };

            _scoreLabel = new AntdUI.Label
            {
                Text = "健康评分：—",
                Font = Services.FormFontPolicy.UiFont(+9f, FontStyle.Bold),
                Location = DpiScale.P(this, 15, 10),
                Size = DpiScale.S(this, 250, 40)
            };

            _summaryLabel = new AntdUI.Label
            {
                Text = "正在分析...",
                Font = Services.FormFontPolicy.UiFont(+1f),
                Location = DpiScale.P(this, 15, 48),
                Size = DpiScale.S(this, 650, 28)
            };

            headerPanel.Controls.AddRange(new Control[] { _scoreLabel, _summaryLabel });

            // 标签页容器
            _tabControl = new AntdUI.Tabs
            {
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.UiFont(+0.5f)
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
                _scoreLabel.ForeColor = GdtermColorTable.Danger;
                _summaryLabel.Text = ex.Message;
            }
        }

        private void DisplayReport(PasswordHealthReport report)
        {
            // 评分颜色
            Color scoreColor;
            if (report.HealthScore >= 90) scoreColor = GdtermColorTable.Success;
            else if (report.HealthScore >= 70) scoreColor = GdtermColorTable.Warning;
            else if (report.HealthScore >= 50) scoreColor = GdtermColorTable.Warning;
            else scoreColor = GdtermColorTable.Danger;

            _scoreLabel.Text = $"健康评分：{report.HealthScore}/100";
            _scoreLabel.ForeColor = scoreColor;
            _summaryLabel.Text = $"{report.Summary}（共 {report.TotalEntries} 个条目）";

            // 清空标签页
            _tabControl.Pages.Clear();

            // 空密码标签页
            AddIssueTab("空密码", report.EmptyPasswords, GdtermColorTable.Danger);

            // 弱密码标签页
            AddIssueTab("弱密码", report.WeakPasswords, GdtermColorTable.Warning);

            // 重复密码标签页
            AddDuplicateTab("重复密码", report.DuplicatePasswords);

            // 过期密码标签页
            AddIssueTab("过期密码", report.ExpiredPasswords, GdtermColorTable.Warning);
        }

        private sealed class IssueRow
        {
            public string Title { get; set; }
            public string Username { get; set; }
            public string GroupPath { get; set; }
            public string Issue { get; set; }
            public string Strength { get; set; }
        }

        private void AddIssueTab(string name, System.Collections.Generic.IList<PasswordIssue> issues, Color color)
        {
            var tab = new AntdUI.TabPage { Text = $"  {name} ({(issues != null ? issues.Count : 0)})  " };

            var table = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BorderWidth = 0,

                RowHeight = 28
            };
            table.Columns.Add(new AntdUI.Column("Title", "标题", AntdUI.ColumnAlign.Left));
            table.Columns.Add(new AntdUI.Column("Username", "用户名", AntdUI.ColumnAlign.Left));
            table.Columns.Add(new AntdUI.Column("GroupPath", "分组", AntdUI.ColumnAlign.Left));
            table.Columns.Add(new AntdUI.Column("Issue", "问题", AntdUI.ColumnAlign.Left));
            table.Columns.Add(new AntdUI.Column("Strength", "强度", AntdUI.ColumnAlign.Left));

            var rows = new AntdUI.AntList<IssueRow>();
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    rows.Add(new IssueRow
                    {
                        Title = issue.Title ?? "(无标题)",
                        Username = issue.Username ?? "",
                        GroupPath = issue.GroupPath ?? "",
                        Issue = issue.Issue ?? "",
                        Strength = issue.StrengthScore.ToString()
                    });
                }
            }
            table.DataSource = rows;

            tab.Controls.Add(table);
            _tabControl.Pages.Add(tab);
        }

        private sealed class DupRow
        {
            public string Hash { get; set; }
            public string Title { get; set; }
            public string Username { get; set; }
            public string GroupPath { get; set; }
        }

        private void AddDuplicateTab(string name, System.Collections.Generic.IList<DuplicatePasswordGroup> groups)
        {
            var tab = new AntdUI.TabPage { Text = $"  {name} ({groups.Count} 组)  " };

            var listView = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BorderWidth = 0,

                RowHeight = 28
            };
            listView.Columns.Add(new AntdUI.Column("Hash", "密码哈希", AntdUI.ColumnAlign.Left));
            listView.Columns.Add(new AntdUI.Column("Title", "条目标题", AntdUI.ColumnAlign.Left));
            listView.Columns.Add(new AntdUI.Column("Username", "用户名", AntdUI.ColumnAlign.Left));
            listView.Columns.Add(new AntdUI.Column("GroupPath", "分组", AntdUI.ColumnAlign.Left));

            var rows = new AntdUI.AntList<DupRow>();
            foreach (var group in groups)
            {
                bool first = true;
                foreach (var entry in group.Entries)
                {
                    rows.Add(new DupRow
                    {
                        Hash = first ? group.PasswordHash : "",
                        Title = entry.Title ?? "(无标题)",
                        Username = entry.Username ?? "",
                        GroupPath = entry.GroupPath ?? ""
                    });
                    first = false;
                }
            }
            listView.DataSource = rows;
            _tabControl.Pages.Add(tab);
        }
    }
}
