using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.Tunnel.Models;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// SSH 终端标签页控件
    /// </summary>
    public class TerminalControl : UserControl, IDisposable
    {
        private readonly ConnectionConfig _config;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly TunnelManager _tunnelManager;
        private readonly IAuditLogger _auditLogger;
        private ITerminalSession _session;
        private TerminalEndpoint _endpoint;

        public TerminalControl(
            ConnectionConfig config,
            ITerminalSessionFactory terminalFactory,
            TunnelManager tunnelManager,
            IAuditLogger auditLogger)
        {
            _config = config;
            _terminalFactory = terminalFactory;
            _tunnelManager = tunnelManager;
            _auditLogger = auditLogger;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // 终端显示区域
            var terminalPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.Black
            };
            Controls.Add(terminalPanel);

            // 连接
            Connect();
        }

        private async void Connect()
        {
            try
            {
                // 如果需要隧道
                if (_config.Tunnel != null)
                {
                    var tunnelEndpoint = await _tunnelManager.EstablishAsync(
                        _config,
                        new CredentialPayload { Username = _config.Username },
                        System.Threading.CancellationToken.None);

                    _endpoint = new TerminalEndpoint
                    {
                        Host = tunnelEndpoint.LocalHost,
                        Port = tunnelEndpoint.LocalPort
                    };
                }
                else
                {
                    _endpoint = new TerminalEndpoint
                    {
                        Host = _config.Host,
                        Port = _config.Port
                    };
                }

                // 创建终端会话
                _session = _terminalFactory.Create(_endpoint);
                _session.OutputReceived += OnTerminalOutput;

                // 连接
                await _session.ConnectAsync(
                    new CredentialPayload { Username = _config.Username },
                    System.Threading.CancellationToken.None);

                _auditLogger.LogConnection(_config.Id, _config.Name, _config.Host, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _auditLogger.LogConnection(_config.Id, _config.Name, _config.Host, false);
            }
        }

        private void OnTerminalOutput(object sender, TerminalOutputEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnTerminalOutput(sender, e)));
                return;
            }

            // TODO: 将输出渲染到终端控件
        }

        public new void Dispose()
        {
            _session?.Dispose();
            base.Dispose();
        }
    }
}
