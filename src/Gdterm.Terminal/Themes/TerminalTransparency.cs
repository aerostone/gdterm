using System;

namespace Gdterm.Terminal.Themes
{
    /// <summary>
    /// 终端窗口增强——透明度控制
    /// </summary>
    public class TerminalTransparency
    {
        private double _opacity = 1.0;

        /// <summary>透明度 (0.3 - 1.0)</summary>
        public double Opacity
        {
            get { return _opacity; }
            set { _opacity = Math.Max(0.3, Math.Min(1.0, value)); }
        }

        /// <summary>是否启用透明</summary>
        public bool IsTransparent { get { return _opacity < 1.0; } }

        /// <summary>应用透明度到 WinForms 窗体</summary>
        public void ApplyToForm(System.Windows.Forms.Form form)
        {
            if (form == null) return;
            form.Opacity = _opacity;
        }

        /// <summary>应用透明度到 WinForms 控件（通过 Region 实现）</summary>
        public void ApplyToControl(System.Windows.Forms.Control control)
        {
            if (control == null) return;
            // WinForms 控件透明度需要通过父容器 BackColor = Transparent
            // 或使用分层窗口（WS_EX_LAYERED）实现
            // 这里仅记录设置，实际在 LightweightRenderer 中使用
        }

        /// <summary>增加透明度（更透明）</summary>
        public double DecreaseOpacity(double step = 0.05)
        {
            Opacity -= step;
            return Opacity;
        }

        /// <summary>减少透明度（更不透明）</summary>
        public double IncreaseOpacity(double step = 0.05)
        {
            Opacity += step;
            return Opacity;
        }

        /// <summary>切换透明/不透明</summary>
        public double ToggleTransparency()
        {
            if (IsTransparent)
                _opacity = 1.0;
            else
                _opacity = 0.7;
            return _opacity;
        }

        /// <summary>重置为完全不透明</summary>
        public void Reset()
        {
            _opacity = 1.0;
        }
    }
}
