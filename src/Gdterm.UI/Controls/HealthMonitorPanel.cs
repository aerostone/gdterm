using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 连接健康监控面板——实时显示延迟/连接状态/运行时间/流量
    /// </summary>
    public class HealthMonitorPanel : UserControl
    {
        private ConnectionHealthMonitor _monitor;
        private AntdUI.Label _lblStatus, _lblUptime, _lblLatency, _lblReconnects;
        private AntdUI.Button _btnExport;
        private Panel _graphPanel;
        private readonly List<HealthSnapshot> _recent = new List<HealthSnapshot>();
        private System.Windows.Forms.Timer _refreshTimer;
        private AntdUI.Label _lblHint;
        private bool _loading = true;       // 首帧快照未到 = 还在加载（此前图区空白，分不清「等」与「坏」）
        private bool _lastConnected = true;
        private static readonly Font _titleFont = Services.FormFontPolicy.UiFont(+1f);
        private static readonly Font _labelFont = Services.FormFontPolicy.UiFont(-1f);
        private readonly Dictionary<Color, SolidBrush> _brushCache = new Dictionary<Color, SolidBrush>();

        private SolidBrush GetBrush(Color color)
        {
            if (!_brushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidBrush(color);
                _brushCache[color] = brush;
            }
            return brush;
        }

        public HealthMonitorPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            BuildUI();
        }

        public void SetMonitor(ConnectionHealthMonitor monitor)
        {
            if (_monitor != null)
            {
                try { _monitor.SnapshotUpdated -= OnSnapshot; } catch (Exception) { }
            }
            _monitor = monitor;
            _loading = true;
            if (_lblHint != null) _lblHint.Text = L10n.T("正在等待首个采样…");
            if (_monitor != null)
            {
                _monitor.SnapshotUpdated += OnSnapshot;
                try { _monitor.Start(3000); }
                catch (Exception ex)
                {
                    SetHint(L10n.T("采样启动失败：") + ex.Message, true);
                    try { DiagLog.Error("HealthMonitorPanel.Start", ex); } catch (Exception) { }
                }
            }
        }

        /// <summary>状态条文案 + 失败时留痕（失败只在文字里变红太容易被忽略）。</summary>
        private void SetHint(string text, bool failed)
        {
            if (_lblHint == null) return;
            _lblHint.Text = text;
            _lblHint.ForeColor = failed ? GdtermColorTable.Danger : GdtermColorTable.Muted;
            if (failed)
            {
                try { DiagLog.Error("HealthMonitorPanel", text); } catch (Exception) { }
                ToastNotifier.Warning(text);
            }
        }

        private void BuildUI()
        {
            // ── 状态卡片 ──
            var cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                // 5 张卡片在侧栏宽（~360）里横排必然越界（CI 0.1.381 实测第 5 张 x=310 + 115px）。
                // 允许换行：宽宿主仍是一行，窄侧栏自动折成 2~3 行，卡不裁、字不糊。
                WrapContents = true,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 10), DpiScale.V(this, 12), DpiScale.V(this, 10))
            };

            _lblStatus = CreateCard(L10n.T("● 状态"), L10n.T("未知"), GdtermColorTable.Muted);
            _lblUptime = CreateCard(L10n.T("⏱ 运行时间"), "00:00:00", GdtermColorTable.Foreground);
            _lblLatency = CreateCard(L10n.T("⚡ 延迟"), "— ms", GdtermColorTable.Success);
            _lblReconnects = CreateCard(L10n.T("↻ 重连"), "0", GdtermColorTable.Warning);
            _lblHint = CreateCard(L10n.T("采样"), L10n.T("正在等待首个采样…"), GdtermColorTable.Muted);
            _lblHint.Font = _labelFont;

            cards.Controls.AddRange(new Control[] { _lblStatus, _lblUptime, _lblLatency, _lblReconnects, _lblHint });

            // F9：采样历史导出 CSV——监控面板的数据带出口（与 ListShell 导出同惯例：BOM/转义/Toast）
            _btnExport = new AntdUI.Button
            {
                Text = L10n.T("导出 CSV"),
                AutoSize = true,
                Enabled = false,   // 有采样点后才可用
                Height = Math.Max(DpiScale.V(this, 28), Services.FormFontPolicy.LineBox(Services.FormFontPolicy.UiFont(), this)),
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 5), DpiScale.V(this, 10), DpiScale.V(this, 5)), // AutoSize 钮零 padding = 文字贴边（ui-tree-check 门）
                Margin = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 6), 0, 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _btnExport.Click += (s, e) => ExportCsv();
            cards.Controls.Add(_btnExport);

            // ── 图表区 ──
            _graphPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(12)
            };
            _graphPanel.Paint += OnPaintGraph;

            Controls.Add(_graphPanel);
            Controls.Add(cards);

            // 定时刷新图表
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _refreshTimer.Tick += (s, e) => _graphPanel.Invalidate();
            _refreshTimer.Start();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private AntdUI.Label CreateCard(string title, string value, Color valueColor)
        {
            var lbl = new AntdUI.Label {
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 8), DpiScale.V(this, 10), DpiScale.V(this, 8)),
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0),
                Font = new Font("Consolas", 14f, FontStyle.Bold),
                ForeColor = valueColor,
                Text = value,
                TextAlign = ContentAlignment.MiddleCenter
            };
            var tip = new ToolTip();
            tip.SetToolTip(lbl, title);
            return lbl;
        }

        private void OnSnapshot(HealthSnapshot snapshot)
        {
            lock (_recent)
            {
                _recent.Add(snapshot);
                if (_recent.Count > 120) _recent.RemoveRange(0, _recent.Count - 60);
            }

            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _loading = false;
                    _lblStatus.Text = snapshot.IsConnected ? L10n.T("● 已连接") : L10n.T("○ 断开");
                    _lblStatus.ForeColor = snapshot.IsConnected ? GdtermColorTable.Success : GdtermColorTable.Danger;
                    _lblUptime.Text = FormatTimeSpan(snapshot.Uptime);
                    _lblLatency.Text = string.Format("{0:F0}ms", snapshot.LatencyMs);
                    _lblReconnects.Text = snapshot.ReconnectCount.ToString();
                    // 状态跃迁才提示：每 3 秒弹一次「断开」会把面板变成噪音源
                    if (snapshot.IsConnected != _lastConnected)
                    {
                        _lastConnected = snapshot.IsConnected;
                        if (!snapshot.IsConnected)
                            SetHint(string.IsNullOrEmpty(snapshot.StatusText) ? L10n.T("连接已断开，正在等待重连…") : snapshot.StatusText, true);
                        else
                            SetHint(L10n.T("已恢复连接"), false);
                    }
                    else if (snapshot.IsConnected)
                        SetHint(L10n.T("采样于 ") + snapshot.Timestamp.ToString("HH:mm:ss"), false);
                    if (!_btnExport.Enabled) _btnExport.Enabled = true;   // F9：首个采样点后开放导出
                }));
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("HealthMonitorPanel", exSwallowed); } catch { } }
        }

        private void OnPaintGraph(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = _graphPanel.ClientRectangle;
            var bgBrush = GetBrush(GdtermColorTable.Background);
            g.FillRectangle(bgBrush, rect);

            // 边距
            int left = 50, right = 20, top = 30, bottom = 30;
            var chartRect = new Rectangle(rect.Left + left, rect.Top + top, rect.Width - left - right, rect.Height - top - bottom);

            // 标题
            g.DrawString(L10n.T("连接状态"), _titleFont, Brushes.Gray, rect.Left + left, 8);

            // 网格线
            using (var pen = new Pen(GdtermColorTable.Surface, 1))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = chartRect.Top + chartRect.Height * i / 4;
                    g.DrawLine(pen, chartRect.Left, y, chartRect.Right, y);
                }
            }

            // 连接状态线（1=已连接, 0=断开）
            List<HealthSnapshot> snapshots;
            lock (_recent) { snapshots = new List<HealthSnapshot>(_recent); }
            if (snapshots.Count < 2)
            {
                // 此前这里直接 return：首帧前图区纯背景色，用户以为图表坏了
                var msg = _loading ? L10n.T("正在等待首个采样…") : L10n.T("采样点不足，稍等几秒即出曲线");
                g.DrawString(msg, _labelFont, GetBrush(GdtermColorTable.Muted),
                    chartRect.Left + 8, chartRect.Top + chartRect.Height / 2f - 8);
                return;
            }

            var points = new PointF[snapshots.Count];
            for (int i = 0; i < snapshots.Count; i++)
            {
                float x = chartRect.Left + chartRect.Width * i / (float)(snapshots.Count - 1);
                float y = snapshots[i].IsConnected ? chartRect.Top + chartRect.Height * 0.2f : chartRect.Top + chartRect.Height * 0.8f;
                points[i] = new PointF(x, y);
            }

            using (var pen = new Pen(GdtermColorTable.Success, 2))
            {
                g.DrawLines(pen, points);
            }

            // 左侧标签
            g.DrawString(L10n.T("在线"), _labelFont, Brushes.Gray, 8, chartRect.Top + chartRect.Height * 0.15f);
            g.DrawString(L10n.T("离线"), _labelFont, Brushes.Gray, 8, chartRect.Top + chartRect.Height * 0.75f);
        }

        /// <summary>F9：采样历史导出 CSV。列：时间/连接/延迟/运行时间/重连/状态。
        /// BOM 不能省（Excel 读中文）；转义与 Toast 文案与 ListShell 导出同口径。</summary>
        private void ExportCsv()
        {
            List<HealthSnapshot> rows;
            lock (_recent) { rows = new List<HealthSnapshot>(_recent); }
            if (rows.Count == 0)
            {
                ToastNotifier.Warning(L10n.T("没有可导出的条目"));
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = L10n.T("导出为 CSV"),
                Filter = "CSV (*.csv)|*.csv",
                FileName = "gdterm-health-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                DefaultExt = "csv"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("Timestamp,Connected,LatencyMs,Uptime,ReconnectCount,Status\r\n");
                    foreach (var s in rows)
                    {
                        sb.Append(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.IsConnected ? "true" : "false").Append(',')
                          .Append(s.LatencyMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(FormatTimeSpan(s.Uptime)).Append(',')
                          .Append(s.ReconnectCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(EscapeCsv(s.StatusText))
                          .Append("\r\n");
                    }
                    using (var sw = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
                        sw.Write(sb.ToString());
                    ToastNotifier.Success(L10n.T("已导出 ") + rows.Count + L10n.T(" 条到 ") + Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("HealthMonitorPanel.ExportCsv", ex); } catch (Exception) { }
                    ToastNotifier.Error(L10n.T("导出失败：") + ex.Message);
                }
            }
        }

        private static string EscapeCsv(string s)
        {
            s = s ?? string.Empty;
            bool need = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!need) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1) return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer?.Stop();
                _refreshTimer?.Dispose();
                if (_monitor != null) _monitor.SnapshotUpdated -= OnSnapshot;
                foreach (var brush in _brushCache.Values) brush.Dispose();
                _brushCache.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
