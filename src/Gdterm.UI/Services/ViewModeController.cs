using System;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 视图模式控制器——Standard / Focus / Compact 与连接树显隐（finding-10）。
    /// MainForm 只持有控件引用，模式切换逻辑集中在此。
    /// </summary>
    public sealed class ViewModeController
    {
        private readonly ConnectionTreeControl _connectionTree;
        private readonly StatusBarControl _statusBar;
        private readonly MenuStrip _menuStrip;
        private readonly QuickBarPanel _quickBar;
        private readonly Action _hideSidePanel;
        private readonly ToolStripMenuItem _viewStandardItem;
        private readonly ToolStripMenuItem _viewFocusItem;
        private readonly ToolStripMenuItem _viewCompactItem;

        private ViewMode _current = ViewMode.Standard;

        public ViewModeController(
            ConnectionTreeControl connectionTree,
            StatusBarControl statusBar,
            MenuStrip menuStrip,
            QuickBarPanel quickBar,
            Action hideSidePanel,
            ToolStripMenuItem viewStandardItem,
            ToolStripMenuItem viewFocusItem,
            ToolStripMenuItem viewCompactItem)
        {
            _connectionTree = connectionTree;
            _statusBar = statusBar;
            _menuStrip = menuStrip;
            _quickBar = quickBar;
            _hideSidePanel = hideSidePanel;
            _viewStandardItem = viewStandardItem;
            _viewFocusItem = viewFocusItem;
            _viewCompactItem = viewCompactItem;
        }

        public ViewMode Current
        {
            get { return _current; }
        }

        public void SetViewMode(ViewMode mode)
        {
            _current = mode;
            if (_viewStandardItem != null) _viewStandardItem.Checked = mode == ViewMode.Standard;
            if (_viewFocusItem != null) _viewFocusItem.Checked = mode == ViewMode.Focus;
            if (_viewCompactItem != null) _viewCompactItem.Checked = mode == ViewMode.Compact;

            switch (mode)
            {
                case ViewMode.Standard:
                    if (_connectionTree != null)
                    {
                        _connectionTree.Visible = true;
                        _connectionTree.Width = 250;
                    }
                    if (_statusBar != null) _statusBar.Visible = true;
                    if (_menuStrip != null) _menuStrip.Visible = true;
                    if (_quickBar != null) _quickBar.Visible = true;
                    break;
                case ViewMode.Focus:
                    if (_connectionTree != null) _connectionTree.Visible = false;
                    if (_statusBar != null) _statusBar.Visible = false;
                    if (_menuStrip != null) _menuStrip.Visible = false;
                    if (_quickBar != null) _quickBar.Visible = false;
                    try { _hideSidePanel?.Invoke(); } catch { }
                    break;
                case ViewMode.Compact:
                    if (_connectionTree != null)
                    {
                        _connectionTree.Visible = true;
                        _connectionTree.Width = 200;
                    }
                    if (_statusBar != null) _statusBar.Visible = false;
                    if (_menuStrip != null) _menuStrip.Visible = false;
                    break;
            }
        }

        public void ToggleConnectionTree()
        {
            if (_connectionTree != null)
                _connectionTree.Visible = !_connectionTree.Visible;
        }

        /// <summary>
        /// Focus 模式下 Esc 临时恢复菜单；返回是否已处理。
        /// </summary>
        public bool TryHandleEscape()
        {
            if (_current != ViewMode.Focus) return false;
            if (_menuStrip != null) _menuStrip.Visible = true;
            return true;
        }
    }
}
