using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 连接健康监控面板——实时显示延迟/连接状态/运行时间/流量
    /// </summary>
    public class HealthMonitorPanel : UserControl
    {
        private ConnectionHealthMonitor _monitor;
        private Label _lblStatus, _lblUptime, _lblLatency, _lblReconnects;
        private Panel _graphPanel;
        private readonly List<HealthSnapshot> _recent = new List<HealthSnapshot>();
        private System.Windows.Forms.Timer _refreshTimer;
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
            BackColor = Color.FromArgb(30, 30, 30);
            BuildUI();
        }

        public void SetMonitor(ConnectionHealthMonitor monitor)
        {
            _monitor = monitor;
            if (_monitor != null)
            {
                _monitor.SnapshotUpdated += OnSnapshot;
                _monitor.Start(3000);
            }
        }

        private void BuildUI()
        {
            // ── 状态卡片 ──
            var cards = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Color.FromArgb(37, 37, 38), Padding = new Padding(12, 10, 12, 10) };

            _lblStatus = CreateCard("● 状态", "未知", 12, Color.FromArgb(130, 130, 130));
            _lblUptime = CreateCard("⏱ 运行时间", "00:00:00", 160, Color.FromArgb(204, 204, 204));
            _lblLatency = CreateCard("⚡ 延迟", "— ms", 320, Color.FromArgb(78, 201, 176));
            _lblReconnects = CreateCard("↻ 重连", "0", 480, Color.FromArgb(255, 200, 87));

            cards.Controls.AddRange(new Control[] { _lblStatus, _lblUptime, _lblLatency, _lblReconnects });

            // ── 图表区 ──
            _graphPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(25, 25, 25),
                Padding = new Padding(12)
            };
            _graphPanel.Paint += OnPaintGraph;

            Controls.Add(_graphPanel);
            Controls.Add(cards);

            // 定时刷新图表
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _refreshTimer.Tick += (s, e) => _graphPanel.Invalidate();
            _refreshTimer.Start();
        }

        private Label CreateCard(string title, string value, int x, Color valueColor)
        {
            var lbl = new Label
            {
                Location = new Point(x, 6),
                AutoSize = false,
                Size = DpiScale.S(140, 65),
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
                    _lblStatus.Text = snapshot.IsConnected ? "● 已连接" : "○ 断开";
                    _lblStatus.ForeColor = snapshot.IsConnected ? Color.FromArgb(78, 201, 176) : Color.FromArgb(255, 80, 80);
                    _lblUptime.Text = FormatTimeSpan(snapshot.Uptime);
                    _lblLatency.Text = string.Format("{0:F0}ms", snapshot.LatencyMs);
                    _lblReconnects.Text = snapshot.ReconnectCount.ToString();
                }));
            }
            catch { }
        }

        private void OnPaintGraph(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = _graphPanel.ClientRectangle;
            var bgBrush = GetBrush(Color.FromArgb(25, 25, 25));
            g.FillRectangle(bgBrush, rect);

            // 边距
            int left = 50, right = 20, top = 30, bottom = 30;
            var chartRect = new Rectangle(rect.Left + left, rect.Top + top, rect.Width - left - right, rect.Height - top - bottom);

            // 标题
            g.DrawString("连接状态", _titleFont, Brushes.Gray, rect.Left + left, 8);

            // 网格线
            using (var pen = new Pen(Color.FromArgb(40, 40, 40), 1))
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
            if (snapshots.Count < 2) return;

            var points = new PointF[snapshots.Count];
            for (int i = 0; i < snapshots.Count; i++)
            {
                float x = chartRect.Left + chartRect.Width * i / (float)(snapshots.Count - 1);
                float y = snapshots[i].IsConnected ? chartRect.Top + chartRect.Height * 0.2f : chartRect.Top + chartRect.Height * 0.8f;
                points[i] = new PointF(x, y);
            }

            using (var pen = new Pen(Color.FromArgb(78, 201, 176), 2))
            {
                g.DrawLines(pen, points);
            }

            // 左侧标签
            g.DrawString("在线", _labelFont, Brushes.Gray, 8, chartRect.Top + chartRect.Height * 0.15f);
            g.DrawString("离线", _labelFont, Brushes.Gray, 8, chartRect.Top + chartRect.Height * 0.75f);
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
