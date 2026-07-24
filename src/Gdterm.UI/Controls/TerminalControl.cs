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
    /// SSH 终端标签页控件——支持暂停/恢复渲染
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
            // 创建轻量级渲染器
            _renderer = new LightweightRenderer();
            Controls.Add(_renderer.GetControl());

            // 延迟连接：标签页创建后不立即连接，等标签页被选中时再连接
            // 这样可以避免一次性打开15个连接
        }

        /// <summary>
        /// 建立连接（延迟调用）
        /// </summary>
        public async void Connect()
        {
            if (_session != null) return; // 已连接

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
                _renderer.Write($"\r\n\x1b[31m连接失败: {ex.Message}\x1b[0m\r\n");
                _auditLogger.LogConnection(_config.Id, _config.Name, _config.Host, false);
            }
        }

        /// <summary>
        /// 暂停渲染（非活动标签调用）
        /// </summary>
        public void PauseRendering()
        {
            if (!_isPaused)
            {
                _isPaused = true;
                _renderer?.Pause();
            }
        }

        /// <summary>
        /// 恢复渲染（活动标签调用）
        /// </summary>
        public void ResumeRendering()
        {
            if (_isPaused)
            {
                _isPaused = false;
                _renderer?.Resume();

                // 如果还没连接，现在连接
                if (_session == null)
                {
                    Connect();
                }
            }
        }

        private void OnTerminalOutput(object sender, TerminalOutputEventArgs e)
        {
            if (_isPaused) return; // 暂停时不处理输出

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => OnTerminalOutput(sender, e)));
                }
                catch { /* 控件已销毁 */ }
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
