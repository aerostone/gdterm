using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.Terminal;
using Gdterm.Terminal.Rendering;
using Gdterm.Tunnel;
using Gdterm.Tunnel.Models;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// SSH 终端标签页控件——支持暂停/恢复渲染、KeePass 凭据自动填充
    /// </summary>
    public class TerminalControl : UserControl, IDisposable
    {
        private readonly ConnectionConfig _config;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly TunnelManager _tunnelManager;
        private readonly IAuditLogger _auditLogger;
        private ITerminalSession _session;
        private TerminalEndpoint _endpoint;
        private LightweightRenderer _renderer;
        private bool _isPaused;
        private bool _disposed;

        /// <summary>
        /// 连接凭据（由 TabContainerControl 从 KeePass 获取后注入）
        /// </summary>
        public CredentialPayload Credentials { get; set; }

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
            _renderer = new LightweightRenderer();
            Controls.Add(_renderer.GetControl());
        }

        /// <summary>
        /// 建立连接（延迟调用，由 ResumeRendering 触发）
        /// </summary>
        public async void Connect()
        {
            if (_session != null) return;

            try
            {
                // 使用注入的凭据，或回退到配置中的用户名
                var credential = Credentials ?? new CredentialPayload
                {
                    Username = _config.Username
                };

                // 如果需要隧道
                if (_config.Tunnel != null)
                {
                    var tunnelEndpoint = await _tunnelManager.EstablishAsync(
                        _config, credential, System.Threading.CancellationToken.None);

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

                _session = _terminalFactory.Create(_endpoint);
                _session.OutputReceived += OnTerminalOutput;

                // 用凭据连接（密码 + SSH 密钥）
                await _session.ConnectAsync(credential, System.Threading.CancellationToken.None);

                _auditLogger.LogConnection(_config.Id, _config.Name, _config.Host, true);
            }
            catch (Exception ex)
            {
                _renderer.Write($"\r\n\x1b[31m连接失败: {ex.Message}\x1b[0m\r\n");
                _auditLogger.LogConnection(_config.Id, _config.Name, _config.Host, false);
            }
        }

        public void PauseRendering()
        {
            if (!_isPaused)
            {
                _isPaused = true;
                _renderer?.Pause();
            }
        }

        public void ResumeRendering()
        {
            if (_isPaused)
            {
                _isPaused = false;
                _renderer?.Resume();

                if (_session == null)
                {
                    Connect();
                }
            }
        }

        private void OnTerminalOutput(object sender, TerminalOutputEventArgs e)
        {
            if (_isPaused) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnTerminalOutput(sender, e))); }
                catch { }
                return;
            }

            _renderer?.Write(e.Text);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _session?.Dispose();
                    _session = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
