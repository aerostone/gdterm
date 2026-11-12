using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 主题化进度条。
    ///
    /// 为什么不用原生 <see cref="ProgressBar"/>：开了视觉样式后它的轨道是系统浅灰色，
    /// 而 WinForms 不提供任何受支持的换色方式（PBM_SETSTATE 只能改 Vista+ 的条状态色）。
    /// 结果是暗色外壳里横着一条白块——2026-11-07 R8「截图白块」上线当天就在
    /// transfer-progress 上抓到：404×38 的区域近白像素 100%，而所有属性检查全绿。
    ///
    /// API 面刻意贴着原生 ProgressBar 的常用子集（Minimum / Maximum / Value / Style /
    /// MarqueeAnimationSpeed），调用方只需换类型名，语义不变；状态色改用
    /// <see cref="State"/> 表达（原生靠 PBM_SETSTATE 传魔数）。
    /// </summary>
    public sealed class ThemedProgress : Control
    {
        /// <summary>进度条状态（对应原生 PBM_SETSTATE 的 Normal / Error / Paused）。</summary>
        public enum ProgressState
        {
            Normal = 1,
            Error = 2,
            Paused = 3
        }

        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private ProgressBarStyle _style = ProgressBarStyle.Continuous;
        private ProgressState _state = ProgressState.Normal;
        private readonly Timer _marqueeTimer;
        private int _marqueePos;      // 0.._marqueeSpan 的滚动相位
        private int _marqueeSpan = 100;

        public ThemedProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = GdtermColorTable.Surface2;
            ForeColor = GdtermColorTable.Accent;
            _marqueeTimer = new Timer();
            _marqueeTimer.Interval = 40;
            _marqueeTimer.Tick += (s, e) =>
            {
                _marqueePos += 4;
                if (_marqueePos > _marqueeSpan) _marqueePos = 0;
                Invalidate();
            };
        }

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; if (_value < _minimum) _value = _minimum; Invalidate(); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = value < _minimum ? _minimum : value; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < _minimum) v = _minimum;
                if (v > _maximum) v = _maximum;
                if (v == _value) return;
                _value = v;
                Invalidate();
            }
        }

        /// <summary>Continuous = 按 Value 填充；Marquee = 滚动段（首帧/未知总量）。</summary>
        public ProgressBarStyle Style
        {
            get { return _style; }
            set
            {
                if (_style == value) return;
                _style = value;
                if (_style == ProgressBarStyle.Marquee) _marqueeTimer.Start();
                else _marqueeTimer.Stop();
                Invalidate();
            }
        }

        /// <summary>原生 API 兼容位：滚动由内部定时器驱动，节奏固定。</summary>
        public int MarqueeAnimationSpeed { get; set; }

        /// <summary>状态色：正常=强调色，出错=危险色，暂停=告警色。</summary>
        public ProgressState State
        {
            get { return _state; }
            set { _state = value; Invalidate(); }
        }

        private Color BarColor
        {
            get
            {
                switch (_state)
                {
                    case ProgressState.Error: return GdtermColorTable.Danger;
                    case ProgressState.Paused: return GdtermColorTable.Warning;
                    default: return GdtermColorTable.Accent;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            using (var track = new SolidBrush(BackColor))
                g.FillRectangle(track, 0, 0, w, h);

            var fill = BarColor;
            if (_style == ProgressBarStyle.Marquee)
            {
                // 滚动段：窗宽的 30%，匀速从左到右循环
                int seg = Math.Max(24, w * 3 / 10);
                _marqueeSpan = w + seg;
                if (_marqueePos > _marqueeSpan) _marqueePos = 0;
                int x = _marqueePos - seg;
                using (var b = new SolidBrush(fill))
                    g.FillRectangle(b, x, 0, seg, h);
            }
            else
            {
                int span = _maximum - _minimum;
                double ratio = span > 0 ? (_value - _minimum) / (double)span : 0;
                int fw = (int)Math.Round(w * ratio);
                if (fw > 0)
                {
                    using (var b = new SolidBrush(fill))
                        g.FillRectangle(b, 0, 0, fw, h);
                }
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) _marqueeTimer.Stop();
            else if (_style == ProgressBarStyle.Marquee) _marqueeTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _marqueeTimer.Stop(); _marqueeTimer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
