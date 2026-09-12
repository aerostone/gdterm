using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 右侧工具宿主——Show/Hide 侧栏、替换活动面板（finding-10）。
    /// </summary>
    public sealed class SidePanelHost
    {
        private readonly Panel _host;
        private Control _active;

        public SidePanelHost(Panel host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public Panel Host { get { return _host; } }

        public Control ActivePanel { get { return _active; } }

        public bool IsVisible
        {
            get { return _host != null && _host.Visible; }
        }

        public static Panel CreateHost(EventHandler onCloseClick)
        {
            var host = new Panel
            {
                Dock = DockStyle.Right,
                // 侧板宿主宽 360 设计 px：内容面板多为 300+ 列，窄于 320 会挤；同样不叠手工 Scale。
                Width = 360,
                Visible = false,
                BackColor = GdtermColorTable.Background
            };
            var sideClose = new AntdUI.Button {
                Text = "✕ 关闭面板",
                Dock = DockStyle.Top,
                // 固定 28 在大字号下裁字 → 字体驱动（host 建成后才有字号，此处先给 28 地板，Show 时校准）
                Height = 28,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            sideClose.Name = "SidePanelCloseButton";
            if (onCloseClick != null)
                sideClose.Click += onCloseClick;
            host.Controls.Add(sideClose);
            return host;
        }

        public void Show(Control panel)
        {
            if (panel == null || _host == null) return;
            if (_active != null)
            {
                _host.Controls.Remove(_active);
                try { _active.Dispose(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelHost", exSwallowed); } catch { } }
            }
            _active = panel;
            panel.Dock = DockStyle.Fill;
            _host.Controls.Add(panel);
            panel.BringToFront();
            _host.Visible = true;
            _host.Width = Math.Max(320, _host.Width);
            // 关闭钮高随字号校准（CreateHost 静态时无字号上下文，此处 host 已有 Font）
            try
            {
                var close = _host.Controls["SidePanelCloseButton"];
                if (close != null)
                    close.Height = Math.Max(28, FormFontPolicy.RowStep(_host));
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelHost", exSwallowed); } catch { } }
        }

        public void Hide()
        {
            if (_host == null) return;
            if (_active != null)
            {
                _host.Controls.Remove(_active);
                try { _active.Dispose(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelHost", exSwallowed); } catch { } }
                _active = null;
            }
            _host.Visible = false;
        }

        public void ShowSnippetSearch(SidePanelFactory factory, TabContainerControl tabs)
        {
            if (factory == null) return;
            var panel = factory.CreateSnippetSearchPanel(cmd =>
            {
                var tc = tabs != null ? tabs.GetActiveTerminalControl() : null;
                if (tc == null) return;
                var line = cmd.EndsWith("\r") || cmd.EndsWith("\n") ? cmd : cmd + "\r";
                tc.SendInput(line);
            });
            Show(panel);
            var snip = panel as SnippetSearchPanel;
            snip?.ShowAndFocus();
        }
    }
}
