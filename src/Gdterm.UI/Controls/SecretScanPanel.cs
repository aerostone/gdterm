using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Security;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 本地敏感信息扫描面板——显示扫描结果和安全评分
    /// </summary>
    public class SecretScanPanel : UserControl
    {
        private readonly SecretScanner _scanner;
        private readonly ISecurityManager _security;
        private readonly AntdUI.Label _lblScore;
        private readonly AntdUI.Label _lblStats;
        private readonly ListView _lvFindings;
        private EmptyStateView _empty;
        /// <summary>过滤/排序/导出壳（扫描结果动辄上百条，按文件/规则找是刚需）</summary>
        private ListShell _shell;
        private readonly AntdUI.Button _btnScan;
        private readonly AntdUI.Button _btnStop;
        private readonly AntdUI.Button _btnCopySel;
        private readonly ProgressBar _progress;
        private readonly AntdUI.Label _lblStatus;

        public SecretScanPanel(SecretScanner scanner, ISecurityManager security = null)
        {
            _scanner = scanner;
            _security = security;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;

            // ── 顶部：安全评分 + 控制按钮 ──
            var topPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 15)),
                ColumnCount = 4,
                RowCount = 2
            };
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _lblScore = new AntdUI.Label {
                Text = L10n.T("安全评分: --"),
                Font = Services.FormFontPolicy.UiFont(+11f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Success,
                AutoSize = true,
                Margin = new Padding(0, 0, DpiScale.V(this, 12), DpiScale.V(this, 4))
            };

            _lblStats = new AntdUI.Label {
                Text = L10n.T("等待扫描..."),
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = GdtermColorTable.Muted,
                AutoSize = true,
                Margin = new Padding(0, 0, DpiScale.V(this, 12), 0)
            };

            _btnScan = new AntdUI.Button {
                Text = L10n.T("开始扫描"),
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 5), DpiScale.V(this, 10), DpiScale.V(this, 5)),
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0),
                Type = AntdUI.TTypeMini.Primary,
                Font = Services.FormFontPolicy.UiFont(),
                Cursor = Cursors.Hand
            };
            _btnScan.Click += (s, e) => StartScan();

            _btnStop = new AntdUI.Button {
                Text = L10n.T("停止"),
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 5), DpiScale.V(this, 10), DpiScale.V(this, 5)),
                Type = AntdUI.TTypeMini.Error,
                Font = Services.FormFontPolicy.UiFont(),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            _btnStop.Click += (s, e) => StopScan();

            // 批量：ListView 原生多选（Ctrl/Shift+点击），复制选中结果供贴进整改清单
            _btnCopySel = new AntdUI.Button {
                Text = L10n.T("复制选中"),
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 5), DpiScale.V(this, 10), DpiScale.V(this, 5)),
                Margin = new Padding(DpiScale.V(this, 8), 0, 0, 0),
                Type = AntdUI.TTypeMini.Default,
                Font = Services.FormFontPolicy.UiFont(),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            _btnCopySel.Name = "SecretScanCopySelButton";
            _btnCopySel.Click += (s, e) => OnCopySelectedClick();

            topPanel.Controls.Add(_lblScore, 0, 0);
            topPanel.Controls.Add(_lblStats, 0, 1);
            topPanel.Controls.Add(_btnScan, 1, 0);
            topPanel.SetRowSpan(_btnScan, 2);
            topPanel.Controls.Add(_btnStop, 2, 0);
            topPanel.SetRowSpan(_btnStop, 2);
            topPanel.Controls.Add(_btnCopySel, 3, 0);
            topPanel.SetRowSpan(_btnCopySel, 2);

            // ── 进度条 ──
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 3, Style = ProgressBarStyle.Continuous };
            WinFormsCompat.SetProgressState(_progress, WinFormsCompat.ProgressStateNormal); // 绿色

            // ── 发现列表 ──
            _lvFindings = new ListView
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                MultiSelect = true
            };
            _lvFindings.SelectedIndexChanged += (s, e) => { _btnCopySel.Enabled = _lvFindings.SelectedItems.Count > 0; };
            _lvFindings.Columns.Add(L10n.T("严重程度"), 80);
            _lvFindings.Columns.Add(L10n.T("类别"), 100);
            _lvFindings.Columns.Add(L10n.T("规则"), 120);
            _lvFindings.Columns.Add(L10n.T("文件路径"), 300);
            _lvFindings.Columns.Add(L10n.T("行号"), 50);
            _lvFindings.Columns.Add(L10n.T("内容（脱敏）"), 200);
            _lvFindings.Columns.Add(L10n.T("描述"), 200);
            _lvFindings.DoubleClick += OnFindingDoubleClick;

            // ── 底部状态栏 ──
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 3), DpiScale.V(this, 10), DpiScale.V(this, 3))
            };
            _lblStatus = new AntdUI.Label {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.UiFont(-1f),
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0),
                Text = L10n.T("选择扫描路径后点击「开始扫描」")
            };
            bottomPanel.Controls.Add(_lblStatus);

            _shell = new ListShell(_lvFindings, L10n.T("过滤规则/文件/内容…"));
            var btnExport = _shell.AddAction(L10n.T("导出"), (s2, e2) => _shell.ExportCsv(FindForm(), "secret-scan"));
            btnExport.Name = "SecretScanExportButton";

            Controls.Add(_shell);
            Controls.Add(_progress);
            // 审计 C1：空态引导 —— 「扫完 0 个发现」与「还没扫」此前都只是一片空列表
            _empty = EmptyStateView.Attach(this, L10n.T("还没有扫描结果"),
                L10n.T("选一个目录开始扫描，命中密钥/口令/私钥等高危模式的文件会列在这里"),
                L10n.T("开始扫描"), (s2, e2) => StartScan());
            _empty.SetEmpty(true);
            Controls.Add(topPanel);
            Controls.Add(bottomPanel);

            // 绑定事件
            _scanner.FindingDetected += OnFindingDetected;
            _scanner.ProgressChanged += OnProgressChanged;
            _scanner.ScanCompleted += OnScanCompleted;
            _scanner.ErrorOccurred += OnError;
            _lvFindings.AccessibleName = L10n.T("敏感信息扫描结果");
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件，
            // 列表名单独设（读屏软件念"宏列表"而不是"列表"）
            try { UiA11y.AutoName(this); } catch (Exception) { }

        }

        private void StartScan()
        {
            _shell.SetItems(new System.Collections.Generic.List<ListViewItem>());
            if (_empty != null) _empty.SetEmpty(false);
            _btnScan.Enabled = false;
            _btnStop.Enabled = true;
            _progress.Style = ProgressBarStyle.Marquee;
            _lblStatus.Text = L10n.T("扫描中...");
            _lblScore.Text = L10n.T("安全评分: 扫描中...");
            _lblScore.ForeColor = GdtermColorTable.Success;

            _scanner.StartScanAsync();
        }

        private void StopScan()
        {
            _scanner.StopScan();
            _btnScan.Enabled = true;
            _btnStop.Enabled = false;
            _progress.Style = ProgressBarStyle.Continuous;
            _lblStatus.Text = L10n.T("扫描已停止");
        }

        private void OnFindingDetected(SecretFinding finding)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnFindingDetected(finding))); return; }

            var item = new ListViewItem(GetSeverityText(finding.Severity));
            item.ForeColor = GetSeverityColor(finding.Severity);
            item.SubItems.Add(finding.Category.ToString());
            item.SubItems.Add(finding.RuleName);
            item.SubItems.Add(finding.FilePath);
            item.SubItems.Add(finding.LineNumber > 0 ? finding.LineNumber.ToString() : "-");
            item.SubItems.Add(finding.GetRedactedContent());
            item.SubItems.Add(finding.Description);
            item.Tag = finding;

            _shell.AddItem(item);
            _lblStats.Text = string.Format(L10n.T("已发现 {0} 个问题"), _shell.AllItems.Count);
        }

        private void OnProgressChanged(int current, int total)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnProgressChanged(current, total))); return; }
            _lblStatus.Text = string.Format(L10n.T("扫描进度: {0}/{1} 文件"), current, total);
        }

        private void OnScanCompleted(SecretScanReport report)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnScanCompleted(report))); return; }

            _btnScan.Enabled = true;
            _btnStop.Enabled = false;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;

            var score = report.SecurityScore;
            _lblScore.Text = string.Format(L10n.T("安全评分: {0}"), score);
            _lblScore.ForeColor = score >= 80 ? GdtermColorTable.Success :
                                  score >= 60 ? GdtermColorTable.Warning :
                                  score >= 40 ? GdtermColorTable.Warning :
                                  GdtermColorTable.Danger;

            _lblStats.Text = string.Format(L10n.T("扫描完成: {0} 文件 | {1} 个发现 (严重:{2} 高:{3} 中:{4} 低:{5})"),
                report.FilesScanned, report.TotalFindings,
                report.CriticalCount, report.HighCount, report.MediumCount, report.LowCount);

            _lblStatus.Text = string.Format(L10n.T("扫描耗时: {0:F1}秒"), report.Duration.TotalSeconds);

            if (_empty != null)
            {
                // 「零发现」是好消息，不该显示成空态；文案要区分「没扫」与「扫完很干净」
                if (_lvFindings.Items.Count > 0) _empty.SetEmpty(false);
                else if (report.FilesScanned > 0)
                {
                    _empty.SetEmpty(true, L10n.T("没有发现敏感信息"),
                        string.Format(L10n.T("已扫描 {0} 个文件，未命中已知密钥/口令模式"), report.FilesScanned));
                    if (_empty.ActionButton != null) _empty.ActionButton.Visible = false;
                }
                else
                {
                    _empty.SetEmpty(true);
                    if (_empty.ActionButton != null) _empty.ActionButton.Visible = true;
                }
                if (_lvFindings.Items.Count > 0 && _empty.ActionButton != null)
                    _empty.ActionButton.Visible = true;
            }
        }

        private void OnError(string error)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnError(error))); return; }
            _lblStatus.Text = L10n.T("错误: ") + error;
            _btnScan.Enabled = true;
            _btnStop.Enabled = false;
            _progress.Style = ProgressBarStyle.Continuous;
            // 失败必须能被人看见：状态栏在面板底部，扫描出错时用户往往已经切走
            var owner = FindForm();
            if (owner != null) AntdUI.Message.error(owner, L10n.T("扫描失败：") + error);
            else ToastNotifier.Error(L10n.T("扫描失败：") + error);
        }

        /// <summary>批量复制选中结果（各列制表符分隔，一行一条），供贴进整改清单。</summary>
        private void OnCopySelectedClick()
        {
            if (_lvFindings.SelectedItems.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (ListViewItem it in _lvFindings.SelectedItems)
            {
                for (int i = 0; i < it.SubItems.Count; i++)
                {
                    if (i > 0) sb.Append('\t');
                    sb.Append(it.SubItems[i].Text);
                }
                sb.AppendLine();
            }
            try
            {
                Clipboard.SetText(sb.ToString().TrimEnd());
                _lblStatus.Text = L10n.Tf("已复制 {0} 条结果", _lvFindings.SelectedItems.Count);
            }
            catch (Exception ex)
            {
                _lblStatus.Text = L10n.Tf("复制失败：{0}", ex.Message);
            }
        }

        private void OnFindingDoubleClick(object sender, EventArgs e)
        {
            if (_lvFindings.SelectedItems.Count == 0) return;
            var finding = _lvFindings.SelectedItems[0].Tag as SecretFinding;
            if (finding == null) return;

            // 显示详情对话框
            var form = new Form
            {
                Text = L10n.T("发现详情"),
                Size = DpiScale.S(this, 600, 400),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            // finding-10：默认脱敏；明文需主密码再验证
            var masked = finding.GetRedactedContent();
            var matchDisplay = masked;
            var revealed = false;

            var txt = new AntdUI.Input {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
            };

            Action refreshText = () =>
            {
                txt.Text = string.Format(
                    L10n.T("规则: {0}\n严重程度: {1}\n类别: {2}\n文件: {3}\n行号: {4}\n\n匹配内容{5}:\n{6}\n\n描述: {7}"),
                    finding.RuleName,
                    GetSeverityText(finding.Severity),
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    revealed ? L10n.T("（明文）") : L10n.T("（已脱敏）"),
                    matchDisplay,
                    finding.Description);
            };
            refreshText();

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(form, 8), DpiScale.V(form, 5), DpiScale.V(form, 8), DpiScale.V(form, 5))
            };

            var btnReveal = new AntdUI.Button {
                Text = L10n.T("显示明文"),
                AutoSize = true,
                Padding = new Padding(DpiScale.V(form, 10), DpiScale.V(form, 4), DpiScale.V(form, 10), DpiScale.V(form, 4)),
                Margin = new Padding(0, 0, DpiScale.V(form, 8), 0),
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
            };
            btnReveal.Click += (s2, e2) =>
            {
                if (revealed) return;
                if (_security != null)
                {
                    if (!Gdterm.UI.Services.MasterPasswordPrompt.Confirm(form, _security, L10n.T("查看敏感匹配内容")))
                        return;
                }
                else
                {
                    var r = MessageBox.Show(form, L10n.T("确认显示完整匹配内容？"), "gdterm",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
                matchDisplay = finding.MatchedContent ?? "";
                revealed = true;
                btnReveal.Enabled = false;
                refreshText();
            };

            var btnWhitelist = new AntdUI.Button {
                Text = L10n.T("加入白名单"),
                AutoSize = true,
                Padding = new Padding(DpiScale.V(form, 10), DpiScale.V(form, 4), DpiScale.V(form, 10), DpiScale.V(form, 4)),
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
            };
            btnWhitelist.Click += (s2, e2) =>
            {
                _scanner.AddToWhitelist(finding.FilePath, finding.MatchedContent);
                MessageBox.Show(L10n.T("已加入白名单"), "gdterm", MessageBoxButtons.OK, MessageBoxIcon.Information);
                form.Close();
            };

            bottom.Controls.Add(btnReveal);
            bottom.Controls.Add(btnWhitelist);
            form.Controls.Add(txt);
            form.Controls.Add(bottom);
            form.Show(this);
        }

        private static string GetSeverityText(FindingSeverity severity)
        {
            switch (severity)
            {
                case FindingSeverity.Critical: return L10n.T("🔴 严重");
                case FindingSeverity.High: return L10n.T("🟠 高");
                case FindingSeverity.Medium: return L10n.T("🟡 中");
                case FindingSeverity.Low: return L10n.T("🟢 低");
                default: return severity.ToString();
            }
        }

        private static Color GetSeverityColor(FindingSeverity severity)
        {
            switch (severity)
            {
                case FindingSeverity.Critical: return GdtermColorTable.Danger;
                case FindingSeverity.High: return GdtermColorTable.Warning;
                case FindingSeverity.Medium: return GdtermColorTable.Warning;
                case FindingSeverity.Low: return GdtermColorTable.Success;
                default: return GdtermColorTable.Foreground;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _scanner.FindingDetected -= OnFindingDetected;
                _scanner.ProgressChanged -= OnProgressChanged;
                _scanner.ScanCompleted -= OnScanCompleted;
                _scanner.ErrorOccurred -= OnError;
                _lvFindings?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
