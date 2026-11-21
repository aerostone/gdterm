using System;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>Creates the empty-session welcome page and routes its actions.</summary>
    public sealed class WelcomePanelCoordinator
    {
        private readonly Form _owner;
        private readonly WelcomePanel _panel;
        private readonly Func<int> _getTabCount;

        public WelcomePanelCoordinator(
            Form owner,
            IConnectionStore connections,
            IBookmarkStore bookmarks,
            Action newConnection,
            Action openLocalTerminal,
            Action openBookmarks,
            Action openKeePass,
            Action<ConnectionConfig> openConnection,
            Func<int> getTabCount)
        {
            _owner = owner;
            _getTabCount = getTabCount;
            _panel = new WelcomePanel(connections, bookmarks)
            {
                Dock = DockStyle.Fill,
                Visible = false
            };

            _panel.NewConnectionRequested += () => Run("NewConnection", newConnection);
            _panel.OpenLocalTerminalRequested += () => RunAndUpdate("OpenLocalTerminal", openLocalTerminal);
            _panel.OpenBookmarksRequested += () => Run("OpenBookmarks", openBookmarks);
            _panel.OpenKeePassRequested += () => Run("OpenKeePass", openKeePass);
            _panel.OpenConnectionRequested += config =>
            {
                try
                {
                    if (openConnection != null) openConnection(config);
                    UpdateVisibility();
                }
                catch (Exception ex) { DiagLog.Swallowed("WelcomePanelCoordinator.OpenConnection", ex); }
            };

            _owner.Controls.Add(_panel);
            _panel.BringToFront();
            UpdateVisibility();
        }

        public void UpdateVisibility(int? tabCount = null)
        {
            try
            {
                var tabs = tabCount ?? (_getTabCount != null ? _getTabCount() : 0);
                bool show = tabs <= 0;
                if (_panel.IsDisposed) return;
                if (show) _panel.Reload();
                _panel.Visible = show;
                if (show) _panel.BringToFront();
            }
            catch (Exception ex) { DiagLog.Swallowed("WelcomePanelCoordinator.UpdateVisibility", ex); }
        }

        private void RunAndUpdate(string operation, Action action)
        {
            Run(operation, action);
            UpdateVisibility();
        }

        private static void Run(string operation, Action action)
        {
            try { action?.Invoke(); }
            catch (Exception ex) { DiagLog.Swallowed("WelcomePanelCoordinator." + operation, ex); }
        }
    }
}
