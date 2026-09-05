using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

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
                Width = 360,
                Visible = false,
                BackColor = GdtermColorTable.Background
            };
            var sideClose = new AntdUI.Button {
                Text = "✕ 关闭面板",
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = GdtermColorTable.Surface,
                ForeColor = Color.White
            };
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
                try { _active.Dispose(); } catch { }
            }
            _active = panel;
            panel.Dock = DockStyle.Fill;
            _host.Controls.Add(panel);
            panel.BringToFront();
            _host.Visible = true;
            _host.Width = Math.Max(320, _host.Width);
        }

        public void Hide()
        {
            if (_host == null) return;
            if (_active != null)
            {
                _host.Controls.Remove(_active);
                try { _active.Dispose(); } catch { }
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
