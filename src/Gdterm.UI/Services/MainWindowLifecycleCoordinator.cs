using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>Owns tray behavior, exit confirmation, and persisted window geometry.</summary>
    public sealed class MainWindowLifecycleCoordinator : IDisposable
    {
        private readonly Form _form;
        private readonly Func<Panel> _getSidePanel;
        private bool _confirmExitPending;
        private NotifyIcon _trayIcon;

        public MainWindowLifecycleCoordinator(Form form, Func<Panel> getSidePanel)
        {
            _form = form;
            _getSidePanel = getSidePanel;
            _form.Disposed += (s, e) => Dispose();
        }

        public void RestoreWindowGeometry(AppearanceSettings settings)
        {
            if (settings == null || settings.WindowWidth < 640 || settings.WindowHeight < 480) return;
            try
            {
                if (settings.WindowX == int.MinValue || settings.WindowY == int.MinValue) return;
                var virtualScreen = SystemInformation.VirtualScreen;
                var bounds = new Rectangle(settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight);
                var visible = Rectangle.Intersect(virtualScreen, bounds);
                if (visible.Width < 200 || visible.Height < 40)
                {
                    DiagLog.Info("MainWindowLifecycle.Geometry", "saved bounds off-screen, fallback to centered: " + bounds);
                    return;
                }
                _form.StartPosition = FormStartPosition.Manual;
                _form.Bounds = bounds;
                if (settings.WindowMaximized) _form.WindowState = FormWindowState.Maximized;
            }
            catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.RestoreGeometry", ex); }
        }

        public void ApplySidePanelWidth(Panel sidePanel, AppearanceSettings settings)
        {
            try
            {
                if (settings == null || sidePanel == null) return;
                if (settings.SidePanelWidth < 320 || settings.SidePanelWidth > 1600) return;
                sidePanel.Width = settings.SidePanelWidth;
            }
            catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.ApplySidePanelWidth", ex); }
        }

        public void SetupTrayIcon(Action openLocalTerminal)
        {
            _trayIcon = new NotifyIcon { Text = "gdterm", Visible = true };
            try { _trayIcon.Icon = _form.Icon ?? SystemIcons.Application; }
            catch (Exception ex)
            {
                try { _trayIcon.Icon = SystemIcons.Application; }
                catch (Exception fallback) { DiagLog.Swallowed("MainWindowLifecycle.TrayIcon", fallback); }
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add(L10n.T("显示主窗口"), null, (s, e) => RestoreFromTray());
            menu.Items.Add(L10n.T("本地终端"), null, (s, e) =>
            {
                RestoreFromTray();
                try { openLocalTerminal?.Invoke(); }
                catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.OpenLocalTerminal", ex); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L10n.T("退出"), null, (s, e) => RequestExit());
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        public void RequestExit()
        {
            _confirmExitPending = true;
            HideTrayIcon();
            _form.Close();
        }

        public void RestoreFromTray()
        {
            try
            {
                _form.Show();
                _form.WindowState = FormWindowState.Normal;
                _form.Activate();
                _form.ShowInTaskbar = true;
            }
            catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.RestoreFromTray", ex); }
        }

        public void OnResize(EventArgs e)
        {
            try
            {
                if (_trayIcon == null || _form.WindowState != FormWindowState.Minimized) return;
                _form.Hide();
                _form.ShowInTaskbar = false;
                try
                {
                    _trayIcon.ShowBalloonTip(1200, "gdterm",
                        L10n.T("已最小化到托盘，双击图标恢复。"), ToolTipIcon.Info);
                }
                catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.BalloonTip", ex); }
            }
            catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.Resize", ex); }
        }

        public void ConfirmClose(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            if (_confirmExitPending)
            {
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            try
            {
                var result = MessageBox.Show(_form,
                    L10n.T("确定要退出 gdterm 吗？\n\n所有打开的连接会话将被关闭。"),
                    L10n.T("退出确认"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes) return;
            }
            catch { return; }

            _confirmExitPending = true;
            HideTrayIcon();
            _form.Close();
        }

        public void SaveWindowGeometry(object sender, FormClosingEventArgs e)
        {
            try
            {
                var settings = Gdterm.UI.Program.GlobalAppearance;
                if (settings == null) return;
                var bounds = _form.WindowState == FormWindowState.Normal ? _form.Bounds : _form.RestoreBounds;
                if (bounds.Width >= 640 && bounds.Height >= 480)
                {
                    settings.WindowX = bounds.X;
                    settings.WindowY = bounds.Y;
                    settings.WindowWidth = bounds.Width;
                    settings.WindowHeight = bounds.Height;
                }
                settings.WindowMaximized = _form.WindowState == FormWindowState.Maximized;
                var side = _getSidePanel != null ? _getSidePanel() : null;
                if (side != null && side.Width >= 320) settings.SidePanelWidth = side.Width;
                settings.Save(AppearanceSettings.DefaultPath);
            }
            catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.SaveGeometry", ex); }
        }

        public void Dispose()
        {
            try
            {
                if (_trayIcon == null) return;
                var menu = _trayIcon.ContextMenuStrip;
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
                menu?.Dispose();
            }
            catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.Dispose", ex); }
        }

        private void HideTrayIcon()
        {
            try { if (_trayIcon != null) _trayIcon.Visible = false; }
            catch (Exception ex) { DiagLog.Swallowed("MainWindowLifecycle.HideTrayIcon", ex); }
        }
    }
}
