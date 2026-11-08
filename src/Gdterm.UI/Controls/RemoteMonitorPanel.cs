using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.Terminal.Monitoring;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 远端主机资源面板（P1-4）：CPU / 内存 / Swap / 磁盘 / 负载 / 网络速率。
    /// 采样走独立 SSH 连接（RemoteStatsSampler），不占用交互 shell，也不往终端里塞命令。
    /// 采样失败只把状态行染红，不弹窗——掉线时这个面板应该安静地告诉人事实。
    /// </summary>
    public sealed class RemoteMonitorPanel : UserControl
    {
        private readonly Func<TerminalControl> _getTerminal;
        private readonly AntdUI.Label _statusLabel;
        private readonly AntdUI.Label _infoLabel;
        private readonly MetricBar _cpuBar;
        private readonly MetricBar _memBar;
        private readonly MetricBar _swapBar;
        private readonly MetricBar _diskBar;
        private readonly System.Windows.Forms.Timer _timer;

        private RemoteStatsSampler _sampler;
        private string _samplerKey;
        private bool _sampling;
        private bool _disposed2;
        private bool _loading = true;          // 首帧未到 → 顶部显示跑马灯 + “正在连接采样”
        private bool _failing;                 // 连续失败中（只在跃迁那一次提示，避免每 5 秒刷屏）
        private ProgressBar _firstLoadBar;
        private AntdUI.Button _btnExport;
        // F9：采样历史（与 HealthMonitorPanel 同容量策略：120 截到 60），供导出 CSV
        private readonly List<RemoteStatsSnapshot> _history = new List<RemoteStatsSnapshot>();

        /// <summary>采样间隔（毫秒）。远端 5 秒一轮，成本可忽略。</summary>
        public int IntervalMs { get; set; } = 5000;

        public RemoteMonitorPanel() : this(null) { }

        public RemoteMonitorPanel(Func<TerminalControl> getTerminal)
        {
            _getTerminal = getTerminal;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            Padding = new Padding(DpiScale.V(this, 12));

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 10,   // 0..8 内容行（AutoSize）+ 9 弹性尾行（Percent 吸收剩余高度）
                BackColor = GdtermColorTable.Background
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 9; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = new AntdUI.Label
            {
                Text = L10n.T("主机资源"),
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.UiFont(+1f),
                Margin = new Padding(0, 0, 0, DpiScale.V(this, 10))
            };
            root.Controls.Add(title, 0, 0);

            _cpuBar = new MetricBar { Caption = "CPU" };
            _memBar = new MetricBar { Caption = L10n.T("内存") };
            _swapBar = new MetricBar { Caption = "Swap" };
            _diskBar = new MetricBar { Caption = L10n.T("磁盘 /") };
            root.Controls.Add(_cpuBar, 0, 1);
            root.Controls.Add(_memBar, 0, 2);
            root.Controls.Add(_swapBar, 0, 3);
            root.Controls.Add(_diskBar, 0, 4);

            _infoLabel = new AntdUI.Label
            {
                Text = "—",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = DpiScale.V(this, 48),
                Margin = new Padding(0, DpiScale.V(this, 10), 0, 0),
                Font = Services.FormFontPolicy.MonoFont()
            };
            root.Controls.Add(_infoLabel, 0, 5);

            _statusLabel = new AntdUI.Label
            {
                Text = L10n.T("等待会话…"),
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = DpiScale.V(this, 22),
                ForeColor = GdtermColorTable.Muted,
                Margin = new Padding(0, DpiScale.V(this, 6), 0, 0)
            };
            root.Controls.Add(_statusLabel, 0, 6);

            // 首帧加载态：本地 5s 采样 + 远端 SSH 建链，第一轮多半要等 1-3 秒。
            // 此前这段时间面板是一片“0.0%”，看起来像采集全挂了。
            _firstLoadBar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Height = DpiScale.V(this, 3),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Margin = new Padding(0, DpiScale.V(this, 6), 0, 0)
            };
            root.Controls.Add(_firstLoadBar, 0, 7);

            var hint = new AntdUI.Label
            {
                Text = L10n.T("每 5 秒在独立 SSH 连接上只读采集 /proc，不干扰终端。"),
                AutoSize = true,
                MaximumSize = new Size(0, DpiScale.V(this, 34)),   // R03：裸像素高度要过 DpiScale
                ForeColor = GdtermColorTable.Muted,
                Margin = DpiScale.Pad(this, 0, 6, 0, 2)
            };
            // F9：采样历史导出 CSV（与 ListShell 导出同惯例：BOM/转义/Toast）
            _btnExport = new AntdUI.Button
            {
                Text = L10n.T("导出 CSV"),
                AutoSize = true,
                Enabled = false,   // 有采样点后才可用
                Height = Math.Max(DpiScale.V(this, 26), Services.FormFontPolicy.LineBox(Services.FormFontPolicy.UiFont(), this)),
                Margin = DpiScale.Pad(this, 8, 4, 0, 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _btnExport.Click += (s, e) => ExportCsv();
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Color.Transparent
            };
            bottom.Controls.Add(hint);
            bottom.Controls.Add(_btnExport);
            root.Controls.Add(bottom, 0, 8);

            Controls.Add(root);

            SetLoading(true);
            _timer = new System.Windows.Forms.Timer { Interval = IntervalMs };
            _timer.Tick += (s, e) => Tick();
            HandleCreated += (s, e) => { _timer.Start(); Tick(); };
            HandleDestroyed += (s, e) => _timer.Stop();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        /// <summary>首帧加载态开关（跑马灯 + 状态行）。</summary>
        private void SetLoading(bool loading)
        {
            _loading = loading;
            if (_firstLoadBar != null && _firstLoadBar.Visible != loading) _firstLoadBar.Visible = loading;
            if (loading) SetStatus(L10n.T("正在连接采样（首个快照约 1-3 秒）…"), true);
        }

        private void Tick()
        {
            if (_sampling || IsDisposed) return;
            var cfg = CurrentConfig();
            if (cfg == null)
            {
                SetLoading(false);
                SetStatus(L10n.T("无活动 SSH 会话（连上后自动开始）"), false);
                ResetBars("—");
                return;
            }

            var cred = CurrentCredentials();
            if (cred == null)
            {
                SetLoading(false);
                SetStatus(L10n.T("缺少凭据，无法采样（重连后恢复）"), false);
                ResetBars("—");
                return;
            }

            string key = Key(cfg, cred);
            if (_sampler == null || _samplerKey != key)
            {
                try { if (_sampler != null) _sampler.Dispose(); } catch (Exception) { }
                try
                {
                    _sampler = new RemoteStatsSampler(cfg, cred);
                    _samplerKey = key;
                }
                catch (Exception ex)
                {
                    _sampler = null;
                    _samplerKey = null;
                    SetLoading(false);
                    SetStatus(L10n.T("采样器初始化失败: ") + ex.Message, false);
                    try { DiagLog.Error("RemoteMonitorPanel.Init", ex); } catch (Exception) { }
                    return;
                }
            }

            var sampler = _sampler;
            _sampling = true;
            Task.Run(() =>
            {
                var snap = sampler.Sample();
                return snap;
            }).ContinueWith(t =>
            {
                _sampling = false;
                if (IsDisposed || Disposing) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (t.IsFaulted || t.Result == null)
                                ReportSampleFailure(t.Exception != null ? t.Exception.GetBaseException().Message : L10n.T("空结果"));
                            else
                                Render(t.Result);
                        }
                        catch (Exception ex) { SetStatus(L10n.T("渲染失败: ") + ex.Message, false); }
                    }));
                }
                catch (Exception) { /* 控件已销毁 */ }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// 采样失败：状态行染红 + 首轮失败时留痕并提示一次。
        /// 面板挂靠终端侧边，失败时用户多半在看终端——只在“进入失败态”的瞬间提示，
        /// 恢复后自动重新武装，不会每 5 秒弹一次。
        /// </summary>
        private void ReportSampleFailure(string message)
        {
            SetLoading(false);
            SetStatus(L10n.T("采样失败: ") + message, false);
            if (_failing) return;
            _failing = true;
            try { DiagLog.Error("RemoteMonitorPanel.Sample", message); } catch (Exception) { }
            ToastNotifier.Warning(L10n.T("主机资源采样失败：") + message);
        }

        private void Render(RemoteStatsSnapshot s)
        {
            if (s.Error != null)
            {
                ReportSampleFailure(s.Error);
                return;
            }
            _failing = false;
            SetLoading(false);
            // F9：记录采样历史（Render 在 UI 线程 BeginInvoke 里跑，无需锁）
            _history.Add(s);
            if (_history.Count > 120) _history.RemoveRange(0, _history.Count - 60);
            if (!_btnExport.Enabled) _btnExport.Enabled = true;
            _cpuBar.Value = s.CpuPercent;
            _memBar.Value = s.MemPercent;
            _swapBar.Value = s.SwapPercent;
            _diskBar.Value = s.DiskPercent;

            _memBar.Detail = s.MemTotalKb > 0
                ? string.Format(CultureInfo.InvariantCulture,"{0:0.0}% / {1}", s.MemPercent, FormatKb(s.MemTotalKb))
                : null;
            _diskBar.Detail = s.DiskTotalKb > 0
                ? string.Format(CultureInfo.InvariantCulture,"{0:0.0}% / {1}", s.DiskPercent, FormatKb(s.DiskTotalKb))
                : null;
            _swapBar.Detail = s.SwapPercent <= 0 ? L10n.T("未启用") : null;

            _infoLabel.Text = string.Format(CultureInfo.InvariantCulture,
                L10n.T("负载(1m) {0:0.00}    网络 ↓ {1:0.0} KiB/s  ↑ {2:0.0} KiB/s"),
                s.Load1, s.NetRxKbps, s.NetTxKbps);
            SetStatus(L10n.T("采样于 ") + s.SampledAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture), true);
        }

        private void ResetBars(string detail)
        {
            _cpuBar.Value = 0; _memBar.Value = 0; _swapBar.Value = 0; _diskBar.Value = 0;
            _cpuBar.Detail = detail; _memBar.Detail = detail; _swapBar.Detail = detail; _diskBar.Detail = detail;
            _infoLabel.Text = "—";
        }

        private void SetStatus(string text, bool ok)
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = ok ? GdtermColorTable.Muted : GdtermColorTable.Danger;
        }

        private ConnectionConfig CurrentConfig()
        {
            try
            {
                var t = _getTerminal != null ? _getTerminal() : null;
                if (t == null || t.Config == null) return null;
                if (!t.IsConnected) return null;
                var cfg = t.Config;
                if (cfg.Protocol != ProtocolType.SSH) return null;   // 串口/本地/RDP 无 /proc
                if (string.IsNullOrWhiteSpace(cfg.Host)) return null;
                return cfg;
            }
            catch (Exception) { return null; }
        }

        private CredentialPayload CurrentCredentials()
        {
            try
            {
                var t = _getTerminal != null ? _getTerminal() : null;
                return t != null ? t.Credentials : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>F9：采样历史导出 CSV。列：时间/CPU/内存/Swap/磁盘/负载1/收/发/内存总量/磁盘总量（KiB）。</summary>
        private void ExportCsv()
        {
            var rows = new List<RemoteStatsSnapshot>(_history);   // Render 只在 UI 线程跑，无需锁
            if (rows.Count == 0)
            {
                ToastNotifier.Warning(L10n.T("没有可导出的条目"));
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = L10n.T("导出为 CSV"),
                Filter = "CSV (*.csv)|*.csv",
                FileName = "gdterm-remote-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                DefaultExt = "csv"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("Timestamp,CpuPercent,MemPercent,SwapPercent,DiskPercent,Load1,NetDownKbps,NetUpKbps,MemTotalKb,DiskTotalKb\r\n");
                    foreach (var s in rows)
                    {
                        sb.Append(s.SampledAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.CpuPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.MemPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.SwapPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.DiskPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.Load1.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.NetRxKbps.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.NetTxKbps.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.MemTotalKb.ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.DiskTotalKb.ToString(CultureInfo.InvariantCulture))
                          .Append("\r\n");
                    }
                    using (var sw = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
                        sw.Write(sb.ToString());
                    ToastNotifier.Success(L10n.T("已导出 ") + rows.Count + L10n.T(" 条到 ") + Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("RemoteMonitorPanel.ExportCsv", ex); } catch (Exception) { }
                    ToastNotifier.Error(L10n.T("导出失败：") + ex.Message);
                }
            }
        }

        private static string Key(ConnectionConfig cfg, CredentialPayload cred)
        {
            return (cfg.Host ?? "") + "|" + cfg.Port + "|" + (cred.Username ?? cfg.Username ?? "");
        }

        private static string FormatKb(long kb)
        {
            if (kb <= 0) return "0";
            double gb = kb / 1024.0 / 1024.0;
            if (gb >= 1.0) return string.Format(CultureInfo.InvariantCulture,"{0:0.0}G", gb);
            return string.Format(CultureInfo.InvariantCulture,"{0:0}M", kb / 1024.0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed2)
            {
                _disposed2 = true;
                try { _timer.Stop(); _timer.Dispose(); } catch (Exception) { }
                try { if (_sampler != null) _sampler.Dispose(); } catch (Exception) { }
                _sampler = null;
            }
            base.Dispose(disposing);
        }

        /// <summary>一条指标：标题 + 进度条 + 右侧数值。纯 GDI 画，避免 AntdUI 版本差异。</summary>
        private sealed class MetricBar : Control
        {
            private double _value;
            public string Caption { get; set; }
            /// <summary>右侧附加说明（总量等），为空则只显示百分比。</summary>
            public string Detail { get; set; }

            public double Value
            {
                get { return _value; }
                set
                {
                    _value = value < 0 ? 0 : (value > 100 ? 100 : value);
                    Invalidate();
                }
            }

            public MetricBar()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Height = DpiScale.V(this, 44);
                Dock = DockStyle.Fill;
                Margin = new Padding(0, 0, 0, DpiScale.V(this, 6));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                int pad = DpiScale.V(this, 4);
                int labelH = DpiScale.V(this, 18);
                var capFont = Services.FormFontPolicy.UiFont(-1f);
                var valFont = Services.FormFontPolicy.MonoFont();

                using (var fg = new SolidBrush(GdtermColorTable.Foreground))
                {
                    g.DrawString(Caption ?? "", capFont, fg, pad, 0);
                    string right = string.Format(CultureInfo.InvariantCulture,"{0:0.0}%", _value);
                    if (!string.IsNullOrEmpty(Detail)) right += "  (" + Detail + ")";
                    var sz = g.MeasureString(right, valFont);
                    g.DrawString(right, valFont, fg, Math.Max(pad, Width - pad - sz.Width), 0);
                }

                var track = new Rectangle(pad, labelH, Math.Max(1, Width - pad * 2), DpiScale.V(this, 8));
                using (var back = new SolidBrush(GdtermColorTable.Surface2))
                {
                    g.FillRectangle(back, track);
                    int fillW = (int)Math.Round(track.Width * _value / 100.0);
                    if (fillW > 0)
                    {
                        // 70% 以上转警示色，90% 以上转危险色：和竞品一致的红黄绿语义
                        var color = _value >= 90 ? GdtermColorTable.Danger
                                  : _value >= 70 ? GdtermColorTable.Warning
                                  : GdtermColorTable.Accent;
                        using (var fill = new SolidBrush(color))
                            g.FillRectangle(fill, new Rectangle(track.X, track.Y, fillW, track.Height));
                    }
                }
            }
        }
    }
}
