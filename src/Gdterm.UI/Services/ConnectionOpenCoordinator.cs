using System;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 连接打开/新建/SFTP 入口编排 + 最近连接记录（finding-10）。
    /// </summary>
    public sealed class ConnectionOpenCoordinator
    {
        private readonly TabContainerControl _tabs;
        private readonly IConnectionStore _store;
        private readonly IBookmarkStore _bookmarks;
        private readonly ConnectionTreeControl _tree;
        private readonly IWin32Window _owner;

        private readonly IKeePassService _keepass;

        public ConnectionOpenCoordinator(
            TabContainerControl tabs,
            IConnectionStore store,
            IBookmarkStore bookmarks,
            ConnectionTreeControl tree,
            IWin32Window owner,
            IKeePassService keepass = null)
        {
            _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
            _store = store;
            _bookmarks = bookmarks;
            _tree = tree;
            _owner = owner;
            _keepass = keepass;
        }

        public void OpenConnection(ConnectionConfig config)
        {
            if (config == null) return;
            try
            {
                DiagLog.Info("ConnOpenCoordinator",
                    "request id=" + (config.Id ?? "") + " host=" + (config.Host ?? "") +
                    " proto=" + config.Protocol);
            }
            catch { }
            try
            {
                _tabs.OpenConnection(config);
            }
            catch (Exception ex)
            {
                // 记录后重抛：保持原有传播行为（MainForm ThreadException 兼弹窗），但日志链不断
                DiagLog.Swallowed("ConnOpenCoordinator.Tabs", ex);
                throw;
            }
            try
            {
                _bookmarks?.AddRecentConnection(new RecentConnection
                {
                    ConnectionId = config.Id,
                    Host = config.Host,
                    Protocol = config.Protocol.ToString(),
                    ConnectedAt = DateTime.UtcNow,
                    Success = true
                });
            }
            catch (Exception ex) { DiagLog.Swallowed("ConnOpenCoordinator.Recent", ex); }
        }

        public void NewConnection()
        {
            using (var dlg = new ConnectionDialog(keepass: _keepass))
            {
                if (dlg.ShowDialog(_owner) == DialogResult.OK && dlg.Result != null)
                {
                    _store?.Add(dlg.Result);
                    try { _tree?.LoadConnections(); } catch { }
                }
            }
        }

        public void OpenSftpFromActive()
        {
            var tc = _tabs.GetActiveTerminalControl();
            if (tc?.Config != null)
            {
                _tabs.OpenSftpBrowser(tc.Config);
                return;
            }
            MessageBox.Show(
                "请先打开一个 SSH 连接，或从连接树双击后再打开 SFTP。",
                "SFTP",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
