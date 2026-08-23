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

            // F11：专注模式 ↔ 标准视图
            if (_viewMode != null && keyData == Keys.F11)
            {
                _viewMode.ToggleFocus();
                return true;
            }

            // 标签导航（Windows Terminal 惯例）：Ctrl+Tab 循环、Ctrl+Alt+数字 直达；
            // 不占用普通 Ctrl 组合，shell readline 不受影响
            if (keyData == (Keys.Control | Keys.Tab))
            {
                _tabs?.CycleTab(1);
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                _tabs?.CycleTab(-1);
                return true;
            }
            if ((keyData & (Keys.Control | Keys.Alt)) == (Keys.Control | Keys.Alt))
            {
                var digit = keyData & Keys.KeyCode;
                if (digit >= Keys.D1 && digit <= Keys.D9)
                {
                    _tabs?.ActivateTabIndex((int)digit - (int)Keys.D1);
                    return true;
                }
            }

            // UI 快捷键一律 Ctrl+Shift+字母：普通 Ctrl 组合留给 shell readline
            // （Ctrl+R 反向搜索 / Ctrl+W 删词 / Ctrl+F 前进字符 / Ctrl+P 上一条历史）
            if (keyData == (Keys.Control | Keys.Shift | Keys.R))
            {
                _tabs?.ReconnectActiveTab();
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.W))
            {
                _tabs?.CloseActiveTab();
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.F))
            {
                _sidePanels?.AttachSearchBar(_tabs);
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.P))
            {
                _sideHost?.ShowSnippetSearch(_sidePanels, _tabs);
                return true;
            }

            return false;
        }
    }
}
