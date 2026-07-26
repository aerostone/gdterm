using System;
using System.Windows.Forms;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// MainForm 快捷键路由（finding-10）。ProcessCmdKey 只做转发。
    /// </summary>
    public sealed class MainFormCommandRouter
    {
        private readonly TabContainerControl _tabs;
        private readonly SidePanelFactory _sidePanels;
        private readonly SidePanelHost _sideHost;
        private readonly ViewModeController _viewMode;

        public MainFormCommandRouter(
            TabContainerControl tabs,
            SidePanelFactory sidePanels,
            SidePanelHost sideHost,
            ViewModeController viewMode)
        {
            _tabs = tabs;
            _sidePanels = sidePanels;
            _sideHost = sideHost;
            _viewMode = viewMode;
        }

        /// <summary>处理快捷键；返回 true 表示已消费。</summary>
        public bool TryHandle(Keys keyData)
        {
            if (_viewMode != null && keyData == Keys.Escape && _viewMode.TryHandleEscape())
                return true;

            if (keyData == (Keys.Control | Keys.R))
            {
                _tabs?.ReconnectActiveTab();
                return true;
            }

            if (keyData == (Keys.Control | Keys.W))
            {
                _tabs?.CloseActiveTab();
                return true;
            }

            if (keyData == (Keys.Control | Keys.F))
            {
                _sidePanels?.AttachSearchBar(_tabs);
                return true;
            }

            if (keyData == (Keys.Control | Keys.P))
            {
                _sideHost?.ShowSnippetSearch(_sidePanels, _tabs);
                return true;
            }

            return false;
        }
    }
}
