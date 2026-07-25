using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 活动会话桥——把 TabContainer 的 SSH 适配细节收敛到一处，
    /// 减轻 MainForm 对 Renci/适配器的直接了解（finding-10 轻量拆分）。
    /// </summary>
    public sealed class ActiveSessionBridge
    {
        private readonly TabContainerControl _tabs;

        public ActiveSessionBridge(TabContainerControl tabs)
        {
            _tabs = tabs;
        }

        public ISshRemoteSession GetRemoteSession()
        {
            return _tabs != null ? _tabs.GetActiveRemoteSession() : null;
        }

        public ISshPortForwardHost GetPortForwardHost()
        {
            return _tabs != null ? _tabs.GetActivePortForwardHost() : null;
        }

        public ITerminalSession GetTerminalSession()
        {
            return _tabs != null ? _tabs.GetActiveSession() : null;
        }

        public TerminalControl GetTerminalControl()
        {
            return _tabs != null ? _tabs.GetActiveTerminalControl() : null;
        }

        public ConnectionHealthMonitor GetHealthMonitor()
        {
            return _tabs != null ? _tabs.GetActiveHealthMonitor() : null;
        }

        public void BindToolbox(ToolboxPanel panel)
        {
            if (panel == null) return;
            try { panel.SetRemoteSession(GetRemoteSession()); } catch { }
        }

        public void BindPortForward(PortForwardPanel panel)
        {
            if (panel == null) return;
            var host = GetPortForwardHost();
            if (host != null)
            {
                try { panel.SetPortForwardHost(host); } catch { }
            }
        }
    }
}
