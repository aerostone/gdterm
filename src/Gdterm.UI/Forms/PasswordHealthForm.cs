using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.Security;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.Core;

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
        /// <summary>“上次扫描”状态行（UX change 2026-09-25）：区分未扫描与扫描完 0 问题。</summary>
        private AntdUI.Label _scanStateLabel;
        private AntdUI.Tabs _tabControl;

        public PasswordHealthForm(IKeePassService keepassService)
        {
            _keepassService = keepassService;
            Font = Gdterm.UI.Services.FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            // 审计 C2：KDBX 全量分析是几百毫秒到数秒的同步 I/O，原先在构造里跑 →
            // 窗体现在 Shown 之后才分析，期间光标 Wait + 状态行给出「正在分析」。
            Shown += (s, e) => LoadReportAsync();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private void InitializeComponent()
        {
            Text = L10n.T("密码健康报告");
            Size = DpiScale.S(this, 700, 550);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            ShowInTaskbar = false;

            // 字体驱动 + DPI 缩放：所有尺寸从 fieldH/rowH 派生
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = Math.Max(DpiScale.V(this, 28), fieldH);

            // 顶部评分区
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 80),
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 15), DpiScale.V(this, 10), DpiScale.V(this, 15), DpiScale.V(this, 5))
            };

            _scoreLabel = new AntdUI.Label {
                Text = L10n.T("健康评分：—"),
                Font = Services.FormFontPolicy.UiFont(+9f, FontStyle.Bold),
                Location = DpiScale.P(this, 15, 8),
                Size = DpiScale.S(this, 250, 32)
            };

            _summaryLabel = new AntdUI.Label {
                Text = L10n.T("正在分析..."),
                Font = Services.FormFontPolicy.UiFont(+1f),
                Location = DpiScale.P(this, 15, 40),
                Size = DpiScale.S(this, 650, rowH),
                ForeColor = GdtermColorTable.Foreground
            };

            // 上次扫描状态行（UX change 2026-09-25）：区分“从未扫描”与“扫描完 0 个问题”，
            // 否则 0 行 + 规则文案让人误以为功能未跑。
            _scanStateLabel = new AntdUI.Label {
                Name = "HealthScanStateLabel",
                Text = L10n.T("上次扫描：—"),
                // 与评分同行右侧（显式 Location 不吃 Panel.Padding，abs 左缘=面板左+275）
                Location = DpiScale.P(this, 275, 10),
                Size = DpiScale.S(this, 395, 24),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = GdtermColorTable.Muted
            };

            headerPanel.Controls.AddRange(new Control[] { _scoreLabel, _scanStateLabel, _summaryLabel });

            // 标签页容器
            _tabControl = new AntdUI.Tabs
            {
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.UiFont(+0.5f)
            };

            // 底部关闭条（RightToLeft：关闭在右，ESC 快捷关闭）
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, DpiScale.V(this, 4), DpiScale.V(this, 12), DpiScale.V(this, 4))
            };
            var closeButton = new AntdUI.Button {
                Name = "HealthCloseButton",
                Text = L10n.T("关闭"),
                Type = AntdUI.TTypeMini.Default,
                Margin = new Padding(0)
            };
            Services.FormFontPolicy.SizeActionButton(this, closeButton, 10, 4);
            closeButton.Click += (s, e) => Close();
            bottomPanel.Controls.Add(closeButton);

            Controls.Add(_tabControl);
            Controls.Add(bottomPanel);
            Controls.Add(headerPanel);

            CancelButton = closeButton;
        }

        private async void LoadReportAsync()
        {
            using (var busy = BusyScope.Begin(this, L10n.T("正在分析密码健康…")))
            {
                if (_scanStateLabel != null) _scanStateLabel.Text = L10n.T("正在分析…");
                _scoreLabel.Text = L10n.T("健康评分：分析中…");
                _summaryLabel.Text = L10n.T("正在读取密码库条目（重复/弱/过期检查）");

                PasswordHealthReport report = null;
                Exception failure = null;
                var cts = busy.Token;
                try
                {
                    report = await System.Threading.Tasks.Task.Run(() =>
                    {
                        cts.ThrowIfCancellationRequested();
                        return _keepassService.AnalyzeHealth();
                    }, cts);
                }
                catch (OperationCanceledException) { /* 窗口关闭/取消：直接收尾 */ }
                catch (Exception ex) { failure = ex; }

                if (IsDisposed || Disposing) return;

                if (failure != null)
                {
                    _scoreLabel.Text = L10n.T("分析失败");
                    _scoreLabel.ForeColor = GdtermColorTable.Danger;
                    _summaryLabel.Text = failure.Message + L10n.T("（可直接重试，无需重开窗口）");
                    if (_scanStateLabel != null) _scanStateLabel.Text = L10n.T("上次扫描：失败");
                    // 原来只有一行红字：既不留痕也不能重试
                    try { DiagLog.Error("PasswordHealth.Analyze", failure); } catch (Exception) { }
                    try { ToastNotifier.Error(L10n.T("密码健康分析失败：") + failure.Message); } catch (Exception) { }
                    ShowRetryTab(failure.Message);
                    return;
                }
                if (report != null) DisplayReport(report);
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

            _scoreLabel.Text = L10n.Tf("健康评分：{0}/100", report.HealthScore);
            _scoreLabel.ForeColor = scoreColor;
            _summaryLabel.Text = L10n.Tf("{0}（共 {1} 个条目）", report.Summary, report.TotalEntries);
            if (_scanStateLabel != null)
                _scanStateLabel.Text = L10n.Tf("上次扫描：{0} · {1} 个条目", DateTime.Now.ToString("HH:mm:ss"), report.TotalEntries);

            // 清空标签页
            _tabControl.Pages.Clear();

            // 空密码标签页
            AddIssueTab(L10n.T("空密码"), report.EmptyPasswords, GdtermColorTable.Danger);

            // 弱密码标签页
            AddIssueTab(L10n.T("弱密码"), report.WeakPasswords, GdtermColorTable.Warning);

            // 重复密码标签页
            AddDuplicateTab(L10n.T("重复密码"), report.DuplicatePasswords);

            // 过期密码标签页
            AddIssueTab(L10n.T("过期密码"), report.ExpiredPasswords, GdtermColorTable.Warning);
        }

        /// <summary>分析失败页：说明 + 一键重试（失败后窗口不再只是"死"在那里）。</summary>
        private void ShowRetryTab(string message)
        {
            _tabControl.Pages.Clear();
            var tab = new AntdUI.TabPage { Text = L10n.T("  分析失败  ") };
            var host = new Panel { Dock = DockStyle.Fill, BackColor = GdtermColorTable.Background };
            tab.Controls.Add(host);
            _tabControl.Pages.Add(tab);
            EmptyStateView.Attach(host, L10n.T("读取密码库失败"),
                message + L10n.T("；解锁密码库或检查 KDBX 文件后点下方按钮重试"),
                L10n.T("重试分析"), (s2, e2) => LoadReportAsync());
        }

        /// <summary>行激活：双击把这一条复制到剪贴板（拿去改密码/贴给同事时不用手抄）。</summary>
        private void WireRowActivation(AntdUI.Table table, Func<object, string> describe)
        {
            if (table == null || describe == null) return;
            EventHandler copy = (s2, e2) =>
            {
                try
                {
                    var row = CurrentRow(table);
                    var text = row == null ? null : describe(row);
                    if (string.IsNullOrEmpty(text)) return;
                    Clipboard.SetText(text);
                    ToastNotifier.Success(L10n.T("已复制：") + text, 2000);
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("PasswordHealth.Copy", ex); } catch (Exception) { }
                    ToastNotifier.Error(L10n.T("复制失败：") + ex.Message);
                }
            };
            table.DoubleClick += copy;
            table.KeyDown += (s2, e2) =>
            {
                if (e2.KeyCode == Keys.Enter) { copy(s2, e2); e2.Handled = true; }
            };
        }

        /// <summary>AntdUI.Table 的 SelectedIndex 含表头行（0 为表头），故减 1 取数据行。</summary>
        private static object CurrentRow(AntdUI.Table table)
        {
            var rows = table.DataSource as System.Collections.IList;
            if (rows == null) return null;
            int i = table.SelectedIndex - 1;
            if (i < 0 || i >= rows.Count) return null;
            return rows[i];
        }

        /// <summary>批量复制选中行（仅元数据，不带密码本体）。</summary>
        private static void CopySelected<T>(TableShell<T> shell, Func<T, string> describe) where T : class
        {
            var rows = shell.GetSelectedRows();
            if (rows.Count == 0) return;
            var lines = new List<string>();
            foreach (var r in rows)
            {
                string t = describe(r);
                if (!string.IsNullOrEmpty(t)) lines.Add(t);
            }
            if (lines.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                ToastNotifier.Success(L10n.Tf("已复制 {0} 行", lines.Count), 2000);
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("PasswordHealth.CopySelected", ex); } catch (Exception) { }
                ToastNotifier.Error(L10n.T("复制失败：") + ex.Message);
            }
        }

        private static string DescribeIssue(object row)
        {
            var r = row as IssueRow;
            return r == null ? null : (r.Title + " / " + r.Username + " / " + r.GroupPath + " / " + r.Issue);
        }

        private static string DescribeDup(object row)
        {
            var r = row as DupRow;
            return r == null ? null : (r.Title + " / " + r.Username + " / " + r.GroupPath);
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
            int rowH = Math.Max(DpiScale.V(this, 24), FormFontPolicy.RowStep(this)); // 表格阅读行：24 地板
            var tab = new AntdUI.TabPage { Text = $"  {name} ({(issues != null ? issues.Count : 0)})  " };

            var table = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.MonoFont(),
                BorderWidth = 0,
                RowHeight = rowH
            };
            table.Columns.Add(new AntdUI.Column("Title", L10n.T("标题"), AntdUI.ColumnAlign.Left) { Width = "22%" });
            table.Columns.Add(new AntdUI.Column("Username", L10n.T("用户名"), AntdUI.ColumnAlign.Left) { Width = "18%" });
            table.Columns.Add(new AntdUI.Column("GroupPath", L10n.T("分组"), AntdUI.ColumnAlign.Left) { Width = "20%" });
            table.Columns.Add(new AntdUI.Column("Issue", L10n.T("问题"), AntdUI.ColumnAlign.Left) { Width = "25%" });
            table.Columns.Add(new AntdUI.Column("Strength", L10n.T("强度"), AntdUI.ColumnAlign.Left) { Width = "15%" });

            var rows = new List<IssueRow>();
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    rows.Add(new IssueRow
                    {
                        Title = issue.Title ?? L10n.T("(无标题)"),
                        Username = issue.Username ?? "",
                        GroupPath = issue.GroupPath ?? "",
                        Issue = issue.Issue ?? "",
                        Strength = issue.StrengthScore.ToString()
                    });
                }
            }
            // 表壳：问题过滤 + 排序 + 导出（只读表、无选中耦合；行激活靠 table.DataSource 反查，
            // 即壳的当前可见行，CurrentRow 仍正确）
            var shell = new TableShell<IssueRow>(table,
                new[] { L10n.T("标题"), L10n.T("用户名"), L10n.T("分组"), L10n.T("问题"), L10n.T("强度") },
                r => string.Join(" ", new[] { r.Title, r.Username, r.GroupPath, r.Issue, r.Strength }),
                (r, i) => { switch (i) { case 0: return r.Title; case 1: return r.Username; case 2: return r.GroupPath; case 3: return r.Issue; default: return r.Strength; } });
            shell.AddAction(L10n.T("导出"), (s, e) => shell.ExportCsv(this, "password-" + name)).Name = "PwdHealthIssueExportButton";
            // 多选 + 批量复制：只复制元数据（标题/用户/分组/问题），不带密码本体
            var multi = shell.EnableMultiSelect();
            var copySel = shell.AddAction(L10n.T("复制选中"), (s, e) => CopySelected(shell, r => DescribeIssue(r)));
            copySel.Enabled = false;
            copySel.Name = "PwdHealthIssueCopySelButton";
            multi.Changed += () => { copySel.Enabled = multi.Count > 0; };
            shell.SetItems(rows);
            WireRowActivation(table, DescribeIssue);

            var host = new Panel { Dock = DockStyle.Fill, BackColor = GdtermColorTable.Background };
            host.Controls.Add(shell);
            EmptyStateView.Attach(host, L10n.T("这一类没有发现问题"),
                L10n.Tf("「{0}」检查通过；规则变更或条目录入变化后重开本窗口即可复检", name));
            var empty = host.Controls.OfType<EmptyStateView>().FirstOrDefault();
            if (empty != null) empty.SetEmpty(rows.Count == 0);

            tab.Controls.Add(host);
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
            int rowH = Math.Max(DpiScale.V(this, 24), FormFontPolicy.RowStep(this)); // 表格阅读行：24 地板
            var tab = new AntdUI.TabPage { Text = L10n.Tf("  {0} ({1} 组)  ", name, groups.Count) };

            var listView = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.MonoFont(),
                BorderWidth = 0,
                RowHeight = rowH
            };
            listView.Columns.Add(new AntdUI.Column("Hash", L10n.T("密码哈希"), AntdUI.ColumnAlign.Left) { Width = "30%" });
            listView.Columns.Add(new AntdUI.Column("Title", L10n.T("条目标题"), AntdUI.ColumnAlign.Left) { Width = "25%" });
            listView.Columns.Add(new AntdUI.Column("Username", L10n.T("用户名"), AntdUI.ColumnAlign.Left) { Width = "20%" });
            listView.Columns.Add(new AntdUI.Column("GroupPath", L10n.T("分组"), AntdUI.ColumnAlign.Left) { Width = "25%" });

            var rows = new List<DupRow>();
            foreach (var group in groups)
            {
                bool first = true;
                foreach (var entry in group.Entries)
                {
                    rows.Add(new DupRow
                    {
                        Hash = first ? group.PasswordHash : "",
                        Title = entry.Title ?? L10n.T("(无标题)"),
                        Username = entry.Username ?? "",
                        GroupPath = entry.GroupPath ?? ""
                    });
                    first = false;
                }
            }
            // 表壳：重复密码过滤 + 排序 + 导出（只读表，按组平铺展示）
            var shell = new TableShell<DupRow>(listView,
                new[] { L10n.T("密码哈希"), L10n.T("条目标题"), L10n.T("用户名"), L10n.T("分组") },
                r => string.Join(" ", new[] { r.Hash, r.Title, r.Username, r.GroupPath }),
                (r, i) => { switch (i) { case 0: return r.Hash; case 1: return r.Title; case 2: return r.Username; default: return r.GroupPath; } });
            shell.AddAction(L10n.T("导出"), (s, e) => shell.ExportCsv(this, "password-" + name)).Name = "PwdHealthDupExportButton";
            // 多选 + 批量复制（仅标题/用户/分组，哈希也不入剪贴板——只给改密码任务清单用）
            var multi = shell.EnableMultiSelect();
            var copySel = shell.AddAction(L10n.T("复制选中"), (s, e) => CopySelected(shell, r => DescribeDup(r)));
            copySel.Enabled = false;
            copySel.Name = "PwdHealthDupCopySelButton";
            multi.Changed += () => { copySel.Enabled = multi.Count > 0; };
            shell.SetItems(rows);
            WireRowActivation(listView, DescribeDup);

            var dupHost = new Panel { Dock = DockStyle.Fill, BackColor = GdtermColorTable.Background };
            dupHost.Controls.Add(shell);
            EmptyStateView.Attach(dupHost, L10n.T("没有重复密码"),
                L10n.T("每个条目都用了唯一密码；同一密码被复用时会在这里按组列出"));
            var dupEmpty = dupHost.Controls.OfType<EmptyStateView>().FirstOrDefault();
            if (dupEmpty != null) dupEmpty.SetEmpty(rows.Count == 0);

            tab.Controls.Add(dupHost);
            _tabControl.Pages.Add(tab);
        }
    }
}
