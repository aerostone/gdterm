using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Security;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 本地敏感信息扫描面板——显示扫描结果和安全评分
    /// </summary>
    public class SecretScanPanel : UserControl
    {
        private readonly SecretScanner _scanner;
        private readonly ISecurityManager _security;
        private readonly Label _lblScore;
        private readonly Label _lblStats;
        private readonly ListView _lvFindings;
        private readonly Button _btnScan;
        private readonly Button _btnStop;
        private readonly ProgressBar _progress;
        private readonly Label _lblStatus;

        public SecretScanPanel(SecretScanner scanner, ISecurityManager security = null)
        {
            _scanner = scanner;
            _security = security;
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(30, 30, 30);

            // ── 顶部：安全评分 + 控制按钮 ──
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Color.FromArgb(37, 37, 38), Padding = new Padding(15) };

            _lblScore = new Label
            {
                Text = "安全评分: --",
                Font = Services.FormFontPolicy.UiFont(+11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(78, 201, 176),
                AutoSize = true,
                Location = DpiScale.P(15, 15)
            };

            _lblStats = new Label
            {
                Text = "等待扫描...",
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(150, 150, 150),
                AutoSize = true,
                Location = DpiScale.P(15, 55)
            };

            _btnScan = new Button
            {
                Text = "开始扫描",
                Size = DpiScale.S(100, 35),
                Location = DpiScale.P(500, 15),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = Services.FormFontPolicy.UiFont(),
                Cursor = Cursors.Hand
            };
            _btnScan.FlatAppearance.BorderSize = 0;
            _btnScan.Click += (s, e) => StartScan();

            _btnStop = new Button
            {
                Text = "停止",
                Size = DpiScale.S(70, 35),
                Location = DpiScale.P(610, 15),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(200, 50, 50),
                ForeColor = Color.White,
                Font = Services.FormFontPolicy.UiFont(),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            _btnStop.FlatAppearance.BorderSize = 0;
            _btnStop.Click += (s, e) => StopScan();

            topPanel.Controls.AddRange(new Control[] { _lblScore, _lblStats, _btnScan, _btnStop });

            // ── 进度条 ──
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 3, Style = ProgressBarStyle.Continuous };
            WinFormsCompat.SetProgressState(_progress, WinFormsCompat.ProgressStateNormal); // 绿色

            // ── 发现列表 ──
            _lvFindings = new ListView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 9f),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                MultiSelect = false
            };
            _lvFindings.Columns.Add("严重程度", 80);
            _lvFindings.Columns.Add("类别", 100);
            _lvFindings.Columns.Add("规则", 120);
            _lvFindings.Columns.Add("文件路径", 300);
            _lvFindings.Columns.Add("行号", 50);
            _lvFindings.Columns.Add("内容（脱敏）", 200);
            _lvFindings.Columns.Add("描述", 200);
            _lvFindings.DoubleClick += OnFindingDoubleClick;

            // ── 底部状态栏 ──
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 25, BackColor = Color.FromArgb(37, 37, 38) };
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.UiFont(-1f),
                ForeColor = Color.FromArgb(130, 130, 130),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Text = "选择扫描路径后点击「开始扫描」"
            };
            bottomPanel.Controls.Add(_lblStatus);

            Controls.Add(_lvFindings);
            Controls.Add(_progress);
            Controls.Add(topPanel);
            Controls.Add(bottomPanel);

            // 绑定事件
            _scanner.FindingDetected += OnFindingDetected;
            _scanner.ProgressChanged += OnProgressChanged;
            _scanner.ScanCompleted += OnScanCompleted;
            _scanner.ErrorOccurred += OnError;
        }

        private void StartScan()
        {
            _lvFindings.Items.Clear();
            _btnScan.Enabled = false;
            _btnStop.Enabled = true;
            _progress.Style = ProgressBarStyle.Marquee;
            _lblStatus.Text = "扫描中...";
            _lblScore.Text = "安全评分: 扫描中...";
            _lblScore.ForeColor = Color.FromArgb(78, 201, 176);

            _scanner.StartScanAsync();
        }

        private void StopScan()
        {
            _scanner.StopScan();
            _btnScan.Enabled = true;
            _btnStop.Enabled = false;
            _progress.Style = ProgressBarStyle.Continuous;
            _lblStatus.Text = "扫描已停止";
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

            _lvFindings.Items.Add(item);
            _lblStats.Text = string.Format("已发现 {0} 个问题", _lvFindings.Items.Count);
        }

        private void OnProgressChanged(int current, int total)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnProgressChanged(current, total))); return; }
            _lblStatus.Text = string.Format("扫描进度: {0}/{1} 文件", current, total);
        }

        private void OnScanCompleted(SecretScanReport report)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnScanCompleted(report))); return; }

            _btnScan.Enabled = true;
            _btnStop.Enabled = false;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;

            var score = report.SecurityScore;
            _lblScore.Text = string.Format("安全评分: {0}", score);
            _lblScore.ForeColor = score >= 80 ? Color.FromArgb(78, 201, 176) :
                                  score >= 60 ? Color.FromArgb(220, 220, 170) :
                                  score >= 40 ? Color.FromArgb(255, 150, 50) :
                                  Color.FromArgb(255, 80, 80);

            _lblStats.Text = string.Format("扫描完成: {0} 文件 | {1} 个发现 (严重:{2} 高:{3} 中:{4} 低:{5})",
                report.FilesScanned, report.TotalFindings,
                report.CriticalCount, report.HighCount, report.MediumCount, report.LowCount);

            _lblStatus.Text = string.Format("扫描耗时: {0:F1}秒", report.Duration.TotalSeconds);
        }

        private void OnError(string error)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnError(error))); return; }
            _lblStatus.Text = "错误: " + error;
        }

        private void OnFindingDoubleClick(object sender, EventArgs e)
        {
            if (_lvFindings.SelectedItems.Count == 0) return;
            var finding = _lvFindings.SelectedItems[0].Tag as SecretFinding;
            if (finding == null) return;

            // 显示详情对话框
            var form = new Form
            {
                Text = "发现详情",
                Size = DpiScale.S(600, 400),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            // finding-10：默认脱敏；明文需主密码再验证
            var masked = finding.GetRedactedContent();
            var matchDisplay = masked;
            var revealed = false;

            var txt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 10f),
                ScrollBars = ScrollBars.Vertical
            };

            Action refreshText = () =>
            {
                txt.Text = string.Format(
                    "规则: {0}\n严重程度: {1}\n类别: {2}\n文件: {3}\n行号: {4}\n\n匹配内容{5}:\n{6}\n\n描述: {7}",
                    finding.RuleName,
                    GetSeverityText(finding.Severity),
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    revealed ? "（明文）" : "（已脱敏）",
                    matchDisplay,
                    finding.Description);
            };
            refreshText();

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.FromArgb(37, 37, 38) };

            var btnReveal = new Button
            {
                Text = "显示明文",
                Width = 100,
                Height = 30,
                Left = 8,
                Top = 5,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204),
                FlatStyle = FlatStyle.Flat
            };
            btnReveal.Click += (s2, e2) =>
            {
                if (revealed) return;
                if (_security != null)
                {
                    if (!Gdterm.UI.Services.MasterPasswordPrompt.Confirm(form, _security, "查看敏感匹配内容"))
                        return;
                }
                else
                {
                    var r = MessageBox.Show(form, "确认显示完整匹配内容？", "gdterm",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
                matchDisplay = finding.MatchedContent ?? "";
                revealed = true;
                btnReveal.Enabled = false;
                refreshText();
            };

            var btnWhitelist = new Button
            {
                Text = "加入白名单",
                Width = 100,
                Height = 30,
                Left = 120,
                Top = 5,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204),
                FlatStyle = FlatStyle.Flat
            };
            btnWhitelist.Click += (s2, e2) =>
            {
                _scanner.AddToWhitelist(finding.FilePath, finding.MatchedContent);
                MessageBox.Show("已加入白名单", "gdterm", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                case FindingSeverity.Critical: return "🔴 严重";
                case FindingSeverity.High: return "🟠 高";
                case FindingSeverity.Medium: return "🟡 中";
                case FindingSeverity.Low: return "🟢 低";
                default: return severity.ToString();
            }
        }

        private static Color GetSeverityColor(FindingSeverity severity)
        {
            switch (severity)
            {
                case FindingSeverity.Critical: return Color.FromArgb(255, 80, 80);
                case FindingSeverity.High: return Color.FromArgb(255, 150, 50);
                case FindingSeverity.Medium: return Color.FromArgb(220, 220, 100);
                case FindingSeverity.Low: return Color.FromArgb(100, 200, 100);
                default: return Color.FromArgb(204, 204, 204);
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
