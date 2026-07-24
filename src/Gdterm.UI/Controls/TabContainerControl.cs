using System;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tunnel;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 标签页容器（TabControl）
    /// </summary>
    public class TabContainerControl : UserControl
    {
        private readonly TunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IAiAssistantService _aiService;
        private readonly IAuditLogger _auditLogger;
        private TabControl _tabControl;

        public TabContainerControl(
            TunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            ISftpServiceFactory sftpFactory,
            IAiAssistantService aiService,
            IAuditLogger auditLogger)
        {
            _tunnelManager = tunnelManager;
            _terminalFactory = terminalFactory;
            _sftpFactory = sftpFactory;
            _aiService = aiService;
            _auditLogger = auditLogger;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill
            };
            Controls.Add(_tabControl);
        }

        /// <summary>
        /// 打开连接标签页
        /// </summary>
        public void OpenConnection(ConnectionConfig config)
        {
            if (config == null) return;

            TabPage tab;

            switch (config.Protocol)
            {
                case ProtocolType.SSH:
                    tab = CreateSshTerminalTab(config);
                    break;
                case ProtocolType.RDP:
                    tab = CreateRdpTab(config);
                    break;
                default:
                    MessageBox.Show($"不支持的协议: {config.Protocol}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
            }

            _tabControl.TabPages.Add(tab);
            _tabControl.SelectedTab = tab;

            // 记录日志
            _auditLogger.LogConnection(config.Id, config.Name, config.Host, true);
        }

        /// <summary>
        /// 关闭所有标签页
        /// </summary>
        public void CloseAllTabs()
        {
            foreach (TabPage tab in _tabControl.TabPages)
            {
                if (tab.Tag is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _tabControl.TabPages.Clear();
        }

        private TabPage CreateSshTerminalTab(ConnectionConfig config)
        {
            var tab = new TabPage(config.Name);
            var terminalControl = new TerminalControl(config, _terminalFactory, _tunnelManager, _auditLogger);
            terminalControl.Dock = DockStyle.Fill;
            tab.Controls.Add(terminalControl);
            tab.Tag = terminalControl;
            return tab;
        }

        private TabPage CreateRdpTab(ConnectionConfig config)
        {
            var tab = new TabPage(config.Name);
            // TODO: 实现 RDP 标签页
            var label = new Label
            {
                Text = $"RDP 连接: {config.Host}",
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter
            };
            tab.Controls.Add(label);
            return tab;
        }
    }
}
