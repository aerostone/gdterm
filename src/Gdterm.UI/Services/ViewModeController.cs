using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 视图模式控制器——Standard / Focus / Compact 与连接树显隐（finding-10）。
    /// Focus 下显示浮动「退出专注」按钮，避免菜单隐藏后无法回标准视图。
    /// </summary>
    public sealed class ViewModeController
    {
        private readonly ConnectionTreeControl _connectionTree;
        private readonly StatusBarControl _statusBar;
        private readonly MenuStrip _menuStrip;
        private readonly QuickBarPanel _quickBar;
        private readonly TmuxBarPanel _tmuxBar;
        private readonly Action _hideSidePanel;
        private readonly ToolStripMenuItem _viewStandardItem;
        private readonly ToolStripMenuItem _viewFocusItem;
        private readonly ToolStripMenuItem _viewCompactItem;
        private readonly Control _host;
        private Button _exitFocusButton;
        private bool _tmuxBarWasVisible;

        private ViewMode _current = ViewMode.Standard;

        public ViewModeController(
            ConnectionTreeControl connectionTree,
            StatusBarControl statusBar,
            MenuStrip menuStrip,
            QuickBarPanel quickBar,
            Action hideSidePanel,
            ToolStripMenuItem viewStandardItem,
            ToolStripMenuItem viewFocusItem,
            ToolStripMenuItem viewCompactItem,
            Control host = null,
            TmuxBarPanel tmuxBar = null)
        {
            _connectionTree = connectionTree;
            _statusBar = statusBar;
            _menuStrip = menuStrip;
            _quickBar = quickBar;
            _tmuxBar = tmuxBar;
            _hideSidePanel = hideSidePanel;
            _viewStandardItem = viewStandardItem;
            _viewFocusItem = viewFocusItem;
            _viewCompactItem = viewCompactItem;
            _host = host;
            EnsureExitButton();
        }

        private void EnsureExitButton()
        {
            if (_host == null || _exitFocusButton != null) return;
            _exitFocusButton = new Button
            {
                Text = "退出专注 (Esc/F11)",
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = GdtermColorTable.Accent,
                ForeColor = Color.White,
                Font = Services.FormFontPolicy.UiFont(0f, FontStyle.Bold),
                Padding = new Padding(10, 4, 10, 4),
                Visible = false,
                TabStop = false,
                Cursor = Cursors.Hand
            };
            _exitFocusButton.FlatAppearance.BorderSize = 0;
            _exitFocusButton.Click += (s, e) => SetViewMode(ViewMode.Standard);
            try
            {
                _host.Controls.Add(_exitFocusButton);
                _exitFocusButton.BringToFront();
                _host.Resize += (s, e) => PositionExitButton();
            }
            catch { }
        }

        private void PositionExitButton()
        {
            if (_exitFocusButton == null || _host == null || !_exitFocusButton.Visible) return;
            try
            {
                _exitFocusButton.Location = new Point(
                    Math.Max(DpiScale.V(_host, 8), _host.ClientSize.Width - _exitFocusButton.Width - DpiScale.V(_host, 12)),
                    DpiScale.V(_host, 8));
                _exitFocusButton.BringToFront();
            }
            catch { }
        }

        private void SetExitButtonVisible(bool visible)
        {
            if (_exitFocusButton == null) return;
            try
            {
                _exitFocusButton.Visible = visible;
                if (visible)
                {
                    PositionExitButton();
                    _exitFocusButton.BringToFront();
                }
            }
            catch { }
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
                    if (_tmuxBar != null) _tmuxBar.Visible = _tmuxBarWasVisible; // 恢复用户偏好（默认隐藏）
                    SetExitButtonVisible(false);
                    break;
                case ViewMode.Focus:
                    if (_connectionTree != null) _connectionTree.Visible = false;
                    if (_statusBar != null) _statusBar.Visible = false;
                    if (_menuStrip != null) _menuStrip.Visible = false;
                    if (_quickBar != null) _quickBar.Visible = false;
                    if (_tmuxBar != null)
                    {
                        _tmuxBarWasVisible = _tmuxBar.Visible; // 记住用户偏好，Standard 恢复
                        _tmuxBar.Visible = false;
                    }
                    try { _hideSidePanel?.Invoke(); } catch { }
                    SetExitButtonVisible(true);
                    break;
                case ViewMode.Compact:
                    if (_connectionTree != null)
                    {
                        _connectionTree.Visible = true;
                        _connectionTree.Width = 200;
                    }
                    if (_statusBar != null) _statusBar.Visible = false;
                    if (_menuStrip != null) _menuStrip.Visible = false;
                    SetExitButtonVisible(true);
                    break;
            }
        }

        public void ToggleConnectionTree()
        {
            if (_connectionTree != null)
                _connectionTree.Visible = !_connectionTree.Visible;
        }

        /// <summary>
        /// Focus/Compact 下 Esc 回到标准视图（菜单+树+状态栏），避免「出不去」。
        /// </summary>
        public bool TryHandleEscape()
        {
            if (_current == ViewMode.Standard) return false;
            SetViewMode(ViewMode.Standard);
            return true;
        }

        /// <summary>在 Standard ↔ Focus 之间切换（F11）。</summary>
        public void ToggleFocus()
        {
            if (_current == ViewMode.Focus)
                SetViewMode(ViewMode.Standard);
            else
                SetViewMode(ViewMode.Focus);
        }
    }
}
