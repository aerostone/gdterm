using System;
using System.Drawing;
using Gdterm.Core.Models;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// TerminalControl 外观/Profile 处理——partial class。
    /// </summary>
    public partial class TerminalControl
    {
        private static void NormalizeProfile(TerminalProfile profile)
        {
            if (profile == null) return;
            // 低配默认：scrollback 300；硬顶 2000。不因内存自动切 Lightweight。
            if (profile.ScrollbackLines > 2000) profile.ScrollbackLines = 2000;
            if (profile.ScrollbackLines < 100) profile.ScrollbackLines = 100;
            if (string.IsNullOrWhiteSpace(profile.TerminalType))
                profile.TerminalType = "xterm-256color";
            if (string.IsNullOrWhiteSpace(profile.Renderer))
                profile.Renderer = "VtCell";
            // 本地会话强制 line renderer（Normalize 在构造后调用时保留 Lightweight）
        }

        /// <summary>
        /// 按当前 GlobalAppearance 与 _profile 重新应用字体 / CJK 字体 / 配色到现有渲染器。
        /// 仅当 renderer 已初始化时生效（InitializeComponent 走原始初始化路径，不走本方法）。
        /// 优先级链与 InitializeComponent 保持一致：Profile 显式>0 → 覆盖；否则 GlobalAppearance；否则兑底默认。
        /// </summary>
        public void ApplyCurrentAppearance()
        {
            if (_renderer == null) return;
            var ga = Gdterm.UI.Program.GlobalAppearance;

            string fontName = _profile != null && !string.IsNullOrWhiteSpace(_profile.FontName)
                && !string.Equals(_profile.FontName, "Consolas", StringComparison.OrdinalIgnoreCase)
                    ? _profile.FontName
                    : (ga != null && !string.IsNullOrWhiteSpace(ga.FontName) ? ga.FontName : "Consolas");
            // 与 InitializeComponent 同样的优先级规则——GlobalAppearance.FontSize 优先于 Profile。
            float fontSize = (ga != null && ga.FontSize > 0)
                ? (float)ga.FontSize
                : (_profile != null && _profile.FontSize > 0
                    ? (float)_profile.FontSize
                    : 14f);
            string cjkFontName = ga != null && !string.IsNullOrWhiteSpace(ga.CjkFontName) ? ga.CjkFontName : null;

            if (_cellRenderer != null)
            {
                try { _cellRenderer.ApplyFont(fontName, fontSize, cjkFontName); } catch { }
            }
            else if (_renderer is LightweightRenderer light)
            {
                try { light.ApplyFont(fontName, fontSize); } catch { }
            }
            try { _renderer.GetControl()?.Invalidate(); } catch { }
        }
    }
}