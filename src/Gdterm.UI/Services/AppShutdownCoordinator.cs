using System;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.Security;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 窗体关闭时的有序释放：会话状态 → 热键 → 标签 → 隧道/KeePass/安全（finding-10）。
    /// 各步独立 try/DiagLog，一步失败不阻断后续清理。
    /// </summary>
    public sealed class AppShutdownCoordinator
    {
        private readonly SessionStateCoordinator _sessionState;
        private readonly GlobalHotkeyController _hotkeys;
        private readonly TabContainerControl _tabs;
        private readonly ITunnelManager _tunnels;
        private readonly IKeePassService _keepass;
        private readonly ISecurityManager _security;

        public AppShutdownCoordinator(
            SessionStateCoordinator sessionState,
            GlobalHotkeyController hotkeys,
            TabContainerControl tabs,
            ITunnelManager tunnels,
            IKeePassService keepass,
            ISecurityManager security)
        {
            _sessionState = sessionState;
            _hotkeys = hotkeys;
            _tabs = tabs;
            _tunnels = tunnels;
            _keepass = keepass;
            _security = security;
        }

        public void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            DiagLog.Try("AppShutdown.SaveSession", () => _sessionState?.Save());
            DiagLog.Try("AppShutdown.Hotkeys", () => _hotkeys?.Dispose());
            DiagLog.Try("AppShutdown.CloseTabs", () => _tabs?.CloseAllTabs());
            DiagLog.Try("AppShutdown.Tunnels", () => _tunnels?.Dispose());
            DiagLog.Try("AppShutdown.KeePass", () => _keepass?.Dispose());
            DiagLog.Try("AppShutdown.Security", () => _security?.Dispose());
        }
    }
}
