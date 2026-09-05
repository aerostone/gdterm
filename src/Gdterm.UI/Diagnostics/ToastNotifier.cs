using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Services;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// 非模态 Toast 通知。替代非关键 MessageBox，3–5 秒自动消失。
    /// 线程安全：可从任意线程调用。
    /// </summary>
    public static class ToastNotifier
    {
        public enum Level
        {
            Info,
            Success,
            Warning,
            Error
        }

        private static readonly object Sync = new object();
        private static readonly List<ToastForm> Active = new List<ToastForm>();
        private static Form _owner;

        /// <summary>绑定主窗体，决定 Toast 停靠位置。</summary>
        public static void Bind(Form owner)
        {
            _owner = owner;
        }

        public static void Info(string message, int durationMs = 3500)
        {
            Show(message, Level.Info, durationMs);
        }

        public static void Success(string message, int durationMs = 3000)
        {
            Show(message, Level.Success, durationMs);
        }

        public static void Warning(string message, int durationMs = 4500)
        {
            Show(message, Level.Warning, durationMs);
        }

        public static void Error(string message, int durationMs = 5000)
        {
            Show(message, Level.Error, durationMs);
        }

        public static void Show(string message, Level level = Level.Info, int durationMs = 3500)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                var owner = _owner;
                if (owner != null && owner.IsHandleCreated && owner.InvokeRequired)
                {
                    owner.BeginInvoke(new Action(() => ShowCore(message, level, durationMs)));
                    return;
                }
                ShowCore(message, level, durationMs);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("ToastNotifier.Show", ex);
            }
        }

        private static void ShowCore(string message, Level level, int durationMs)
        {
            var form = new ToastForm(message, level, Math.Max(1500, durationMs));
            lock (Sync)
            {
                Active.Add(form);
                RepositionLocked();
            }
            form.FormClosed += (s, e) =>
            {
                lock (Sync)
                {
                    Active.Remove(form);
                    RepositionLocked();
                }
            };
            form.Show(_owner);
        }

        private static void RepositionLocked()
        {
            var owner = _owner;
            Rectangle work;
            if (owner != null && owner.IsHandleCreated && !owner.IsDisposed)
            {
                work = owner.RectangleToScreen(owner.ClientRectangle);
            }
            else
            {
                work = Screen.PrimaryScreen.WorkingArea;
            }

            int bottom = work.Bottom - 16;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var t = Active[i];
                if (t == null || t.IsDisposed) continue;
                t.Location = new Point(work.Right - t.Width - 16, bottom - t.Height);
                bottom -= t.Height + 8;
            }
        }

        private sealed class ToastForm : Form
        {
            private readonly Timer _timer;
            private int _ticks;
            private readonly int _maxTicks;

            public ToastForm(string message, Level level, int durationMs)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                Size = DpiScale.S(this, 320, 64);
                DoubleBuffered = true;
                BackColor = ColorFor(level);
                Opacity = 0.96;

                var accent = AccentFor(level);
                var bar = new Panel
                {
                    Dock = DockStyle.Left,
                    Width = 4,
                    BackColor = accent
                };
                var label = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = message,
                    ForeColor = GdtermColorTable.Foreground,
                    Font = Services.FormFontPolicy.UiFont(),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(12, 8, 12, 8)
                };
                Controls.Add(label);
                Controls.Add(bar);

                _maxTicks = Math.Max(1, durationMs / 50);
                _timer = new Timer { Interval = 50 };
                _timer.Tick += (s, e) =>
                {
                    _ticks++;
                    if (_ticks >= _maxTicks)
                    {
                        try { _timer.Stop(); } catch { }
                        try { Close(); } catch { }
                    }
                    else if (_ticks > _maxTicks - 10)
                    {
                        // 淡出
                        try { Opacity = Math.Max(0.15, 0.96 * (1.0 - (_ticks - (_maxTicks - 10)) / 10.0)); }
                        catch { }
                    }
                };
                _timer.Start();
                Click += (s, e) => { try { Close(); } catch { } };
                label.Click += (s, e) => { try { Close(); } catch { } };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return cp;
                }
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(GdtermColorTable.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _timer.Stop(); } catch { }
                    try { _timer.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }

            private static Color ColorFor(Level level)
            {
                switch (level)
                {
                    case Level.Success: return GdtermColorTable.Success;
                    case Level.Warning: return GdtermColorTable.Warning;
                    case Level.Error: return GdtermColorTable.Danger;
                    default: return GdtermColorTable.Background;
                }
            }

            private static Color AccentFor(Level level)
            {
                switch (level)
                {
                    case Level.Success: return GdtermColorTable.Accent;
                    case Level.Warning: return GdtermColorTable.Warning;
                    case Level.Error: return GdtermColorTable.Danger;
                    default: return GdtermColorTable.Info;
                }
            }
        }
    }
}
