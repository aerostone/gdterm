using System;
using System.Drawing;
using Gdterm.Core.Models;
using Gdterm.Terminal.Rendering;
using Gdterm.UI.Diagnostics;

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
            // 收敛到 TerminalLimits 一处口径（P1-5）：之前这里硬顶 2000，跟引擎层 100k 上限打架。
            // 设置界面给人看的下限仍是 MinScrollback，否则 0/负数会被引擎当成「不留历史」。
            if (profile.ScrollbackLines < Gdterm.Core.Constants.TerminalLimits.MinScrollback)
                profile.ScrollbackLines = Gdterm.Core.Constants.TerminalLimits.MinScrollback;
            profile.ScrollbackLines = Gdterm.Core.Constants.TerminalLimits.ClampScrollback(profile.ScrollbackLines, 0);
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
                try { _cellRenderer.ApplyFont(fontName, fontSize, cjkFontName); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Profile", exSwallowed); } catch { } }
                try { if (_profile != null) _cellRenderer.ApplyTerminalType(_profile.TerminalType); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Profile", exSwallowed); } catch { } }
                LogFontMetrics("cell");
            }
            else if (_renderer is LightweightRenderer light)
            {
                try { light.ApplyFont(fontName, fontSize); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Profile", exSwallowed); } catch { } }
                LogFontMetrics("light");
            }
            try { _renderer.GetControl()?.Invalidate(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Profile", exSwallowed); } catch { } }
        }
    }
}