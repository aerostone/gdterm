using System;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 会话窗口状态保存/恢复——从 MainForm 抽出（finding-10）。
    /// </summary>
    public sealed class SessionStateCoordinator
    {
        private readonly SessionStateStore _store;
        private readonly IConnectionStore _connectionStore;
        private readonly TabContainerControl _tabs;
        private readonly Form _form;
        private readonly Func<ViewMode> _getViewMode;
        private readonly Action<ViewMode> _setViewMode;
        private readonly Func<int> _getTreeWidth;
        private readonly Action<int> _setTreeWidth;

        public SessionStateCoordinator(
            SessionStateStore store,
            IConnectionStore connectionStore,
            TabContainerControl tabs,
            Form form,
            Func<ViewMode> getViewMode,
            Action<ViewMode> setViewMode,
            Func<int> getTreeWidth,
            Action<int> setTreeWidth)
        {
            _store = store;
            _connectionStore = connectionStore;
            _tabs = tabs;
            _form = form;
            _getViewMode = getViewMode;
            _setViewMode = setViewMode;
            _getTreeWidth = getTreeWidth;
            _setTreeWidth = setTreeWidth;
        }

        public void Save()
        {
            if (_store == null || _form == null) return;
            var state = new SessionState
            {
                WindowX = _form.Left,
                WindowY = _form.Top,
                WindowWidth = _form.Width,
                WindowHeight = _form.Height,
                WindowState = _form.WindowState.ToString(),
                ViewMode = _getViewMode != null ? _getViewMode().ToString() : ViewMode.Standard.ToString(),
                ConnectionPanelWidth = _getTreeWidth != null ? _getTreeWidth() : 250,
                ActiveTabIndex = _tabs != null ? _tabs.ActiveTabIndex : -1,
                OpenTabs = _tabs != null ? _tabs.GetOpenTabStates() : null
            };
            _store.Save(state);
        }

        public void Restore()
        {
            if (_store == null || _form == null) return;
            var state = _store.Load();
            if (state == null) return;
            try
            {
                if (state.WindowWidth > 200 && state.WindowHeight > 200)
                {
                    _form.Width = state.WindowWidth;
                    _form.Height = state.WindowHeight;
                    _form.StartPosition = FormStartPosition.Manual;
                    _form.Left = state.WindowX;
                    _form.Top = state.WindowY;
                }
                if (state.WindowState == "Maximized")
                    _form.WindowState = FormWindowState.Maximized;
                if (_setViewMode != null && Enum.TryParse(state.ViewMode, out ViewMode vm))
                    _setViewMode(vm);
                if (state.ConnectionPanelWidth > 50 && _setTreeWidth != null)
                    _setTreeWidth(state.ConnectionPanelWidth);
                if (state.OpenTabs != null && _tabs != null && _connectionStore != null)
                {
                    var all = _connectionStore.LoadAll();
                    foreach (var tab in state.OpenTabs)
                    {
                        var config = all.FirstOrDefault(c => c.Id == tab.ConnectionId);
                        if (config != null) _tabs.OpenConnection(config);
                    }
                    if (state.ActiveTabIndex >= 0)
                        _tabs.SetActiveTabIndex(state.ActiveTabIndex);
                }
            }
            catch { }
        }
    }
}
