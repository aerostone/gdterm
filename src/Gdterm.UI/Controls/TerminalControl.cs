using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.Terminal.Models;
using Gdterm.Terminal.Rendering;
using Gdterm.Terminal.Themes;
using Gdterm.Tunnel;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// SSH/串口/本地终端标签页控件——懒连接、暂停渲染、危险命令拦截、TerminalProfile
    /// </summary>
    public class TerminalControl : UserControl, IDisposable
    {
        private readonly ConnectionConfig _config;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly TunnelManager _tunnelManager;
        private readonly IAuditLogger _auditLogger;
        private readonly DangerousCommandDetector _dangerousDetector;
        private readonly TerminalKeyBindingResolver _keyResolver = new TerminalKeyBindingResolver();
        private readonly StringBuilder _commandLine = new StringBuilder();

        private ITerminalSession _session;
        private LightweightRenderer _renderer;
        private TerminalProfile _profile;
        private TerminalAutoLogger _autoLogger;
        private bool _isPaused = true; // 默认暂停，等 Resume 再连
        private bool _disposed;
        private bool _connecting;

        /// <summary>KeePass 注入的凭据</summary>
        public CredentialPayload Credentials { get; set; }

        /// <summary>底层会话（多通道/快捷栏使用）</summary>
        public ITerminalSession Session => _session;

        /// <summary>连接配置</summary>
        public ConnectionConfig Config => _config;

        /// <summary>是否已连接</summary>
        public bool IsConnected => _session != null && _session.IsConnected;

        /// <summary>快捷键解析器</summary>
        public TerminalKeyBindingResolver KeyResolver => _keyResolver;

        /// <summary>快捷键动作（copy/paste/clear/find）</summary>
        public event EventHandler<KeyBindingActionEventArgs> ActionRequested;

        /// <summary>会话建立后</summary>
        public event EventHandler SessionConnected;

        /// <summary>会话断开</summary>
        public event EventHandler SessionDisconnected;

        public TerminalControl(
            ConnectionConfig config,
            ITerminalSessionFactory terminalFactory,
            TunnelManager tunnelManager,
            IAuditLogger auditLogger,
            DangerousCommandDetector dangerousDetector = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _terminalFactory = terminalFactory ?? throw new ArgumentNullException(nameof(terminalFactory));
            _tunnelManager = tunnelManager;
            _auditLogger = auditLogger;
            _dangerousDetector = dangerousDetector;

            _profile = TerminalProfile.FromMetadata(config.Metadata);
            if (_profile.ScrollbackLines > 1000) _profile.ScrollbackLines = 1000;
            if (_profile.ScrollbackLines < 100) _profile.ScrollbackLines = 100;

            InitializeComponent();
        }

        /// <summary>本地终端专用构造</summary>
        public TerminalControl(ITerminalSession localSession, IAuditLogger auditLogger = null)
        {
            _session = localSession ?? throw new ArgumentNullException(nameof(localSession));
            _auditLogger = auditLogger;
            _config = new ConnectionConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "本地终端",
                Host = "localhost",
                Protocol = ProtocolType.SSH
            };
            _profile = new TerminalProfile();
            InitializeComponent();
            AttachExistingSession(localSession);
            _isPaused = false;
        }

        private void InitializeComponent()
        {
            var scheme = ColorSchemes.GetByName(_profile?.ColorScheme) ?? ColorSchemes.Classic;
            _renderer = new LightweightRenderer(24, 80, scheme);
            var canvas = _renderer.GetControl();
            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);

            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;

            // 键盘：渲染器画布获得焦点时转发
            canvas.KeyPress += OnKeyPress;
            canvas.KeyDown += OnKeyDown;
            canvas.PreviewKeyDown += (s, e) =>
            {
                // 让方向键等进入 KeyDown
                e.IsInputKey = true;
            };
            canvas.GotFocus += (s, e) => Focus();
            KeyPress += OnKeyPress;
            KeyDown += OnKeyDown;
            PreviewKeyDown += (s, e) => { e.IsInputKey = true; };
        }

        private void AttachExistingSession(ITerminalSession session)
        {
            _session = session;
            _session.OutputReceived += OnTerminalOutput;
            try
            {
                if (session is LocalTerminalSession local && !local.IsConnected)
                    local.ConnectLocal();
            }
            catch (Exception ex)
            {
                _renderer?.Write("\r\n\x1b[31m本地终端启动失败: " + ex.Message + "\x1b[0m\r\n");
            }
        }

        /// <summary>建立连接（懒加载：ResumeRendering 触发）</summary>
        public async void Connect()
        {
            if (_session != null || _connecting || _disposed) return;
            _connecting = true;

            try
            {
                var credential = Credentials ?? new CredentialPayload { Username = _config.Username };
                ITerminalSession session;

                if (_config.Protocol == ProtocolType.Serial)
                {
                    session = new SerialSession();
                    await Task.Run(() => session.Connect(_config, credential));
                }
                else
                {
                    session = _terminalFactory != null
                        ? _terminalFactory.Create(new TerminalEndpoint { Host = _config.Host, Port = _config.Port })
                        : new TerminalSession();

                    if (_config.Tunnel != null && _tunnelManager != null)
                    {
                        var tunnelEndpoint = await _tunnelManager.EstablishAsync(
                            _config, credential, System.Threading.CancellationToken.None);
                        await Task.Run(() => session.ConnectViaTunnel(_config, credential, tunnelEndpoint));
                    }
                    else
                    {
                        await Task.Run(() => session.Connect(_config, credential));
                    }
                }

                _session = session;
                _session.OutputReceived += OnTerminalOutput;

                // 自动运行命令（profile）
                if (_profile.AutoRunCommands != null)
                {
                    foreach (var cmd in _profile.AutoRunCommands)
                    {
                        if (!string.IsNullOrWhiteSpace(cmd))
                            SafeSend(cmd + (_profile.NewLineSequence ?? "\n"));
                    }
                }

                _auditLogger?.LogConnection(_config.Id, _config.Name, _config.Host, true);
                SessionConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _renderer?.Write("\r\n\x1b[31m连接失败: " + ex.Message + "\x1b[0m\r\n");
                _auditLogger?.LogConnection(_config.Id, _config.Name, _config.Host, false);
            }
            finally
            {
                _connecting = false;
            }
        }

        /// <summary>启用会话自动日志（默认关闭，外部显式打开）</summary>
        public void EnableAutoLog(string logDirectory)
        {
            if (string.IsNullOrEmpty(logDirectory) || _autoLogger != null) return;
            _autoLogger = new TerminalAutoLogger(logDirectory)
            {
                MaxFileSizeBytes = 10 * 1024 * 1024, // 10MB
                MaxFileCount = 3
            };
            try { _autoLogger.StartRecording(_config?.Host ?? "session", _config?.Name); } catch { }
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
            }

            if (_session == null && !_connecting)
                Connect();
        }

        /// <summary>向终端发送文本（含危险命令检测）</summary>
        public bool TrySendInput(string text, bool isCommandLine = false)
        {
            if (string.IsNullOrEmpty(text) || _session == null || !_session.IsConnected)
                return false;

            if (isCommandLine && _dangerousDetector != null)
            {
                var check = _dangerousDetector.Check(text.TrimEnd('\r', '\n'));
                if (check != null && check.IsDangerous)
                {
                    using (var dlg = new DangerousCommandDialog(text.Trim(), check))
                    {
                        dlg.ShowDialog(FindForm());
                        if (!dlg.IsConfirmed)
                            return false;
                        if (dlg.RememberChoice)
                        {
                            try { _dangerousDetector.AddToWhitelist(text.Trim()); } catch { }
                        }
                    }
                }
            }

            return SafeSend(text);
        }

        public void SendInput(string text)
        {
            TrySendInput(text, isCommandLine: true);
        }

        private bool SafeSend(string text)
        {
            try
            {
                _session.SendInput(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void ClearTerminal()
        {
            _renderer?.Clear();
        }

        public string GetSelection()
        {
            return _renderer?.GetSelection() ?? string.Empty;
        }

        public string[] GetRecentLines(int count)
        {
            return _renderer?.GetRecentLines(count) ?? new string[0];
        }

        private void OnTerminalOutput(object sender, TerminalOutputEventArgs e)
        {
            if (_disposed || e == null || string.IsNullOrEmpty(e.Text)) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnTerminalOutput(sender, e))); }
                catch { }
                return;
            }

            if (!_isPaused)
                _renderer?.Write(e.Text);

            try { _autoLogger?.LogOutput(e.Text); } catch { }
        }

        private void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            if (_session?.IsConnected != true) return;

            // 可打印字符进入命令行缓冲，回车时做危险检测
            if (!char.IsControl(e.KeyChar))
            {
                _commandLine.Append(e.KeyChar);
                SafeSend(e.KeyChar.ToString());
                e.Handled = true;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_session?.IsConnected != true) return;

            var result = _keyResolver.Resolve(e);
            if (result != null)
            {
                e.Handled = true;
                switch (result.Type)
                {
                    case SendType.Sequence:
                    case SendType.Text:
                        SafeSend(result.Value);
                        break;
                    case SendType.Action:
                        ActionRequested?.Invoke(this, new KeyBindingActionEventArgs(result.Value, result.Binding));
                        break;
                }
                return;
            }

            try
            {
                switch (e.KeyCode)
                {
                    case Keys.Enter:
                        // 命令行确认：危险检测
                        var cmd = _commandLine.ToString();
                        _commandLine.Clear();
                        if (!string.IsNullOrWhiteSpace(cmd) && _dangerousDetector != null)
                        {
                            var check = _dangerousDetector.Check(cmd);
                            if (check != null && check.IsDangerous)
                            {
                                // 已逐字发送过命令体，这里只拦截回车
                                using (var dlg = new DangerousCommandDialog(cmd, check))
                                {
                                    dlg.ShowDialog(FindForm());
                                    if (!dlg.IsConfirmed)
                                    {
                                        // 用 Ctrl+C 取消当前行
                                        SafeSend("\x03");
                                        e.Handled = true;
                                        return;
                                    }
                                    if (dlg.RememberChoice)
                                    {
                                        try { _dangerousDetector.AddToWhitelist(cmd); } catch { }
                                    }
                                }
                            }
                        }
                        SafeSend("\r");
                        e.Handled = true;
                        break;
                    case Keys.Back:
                        if (_commandLine.Length > 0)
                            _commandLine.Length--;
                        SafeSend("\b");
                        e.Handled = true;
                        break;
                    case Keys.Tab:
                        SafeSend("\t");
                        e.Handled = true;
                        break;
                    case Keys.Escape:
                        _commandLine.Clear();
                        SafeSend("\x1b");
                        e.Handled = true;
                        break;
                    case Keys.Up:
                        _commandLine.Clear();
                        SafeSend("\x1b[A");
                        e.Handled = true;
                        break;
                    case Keys.Down:
                        _commandLine.Clear();
                        SafeSend("\x1b[B");
                        e.Handled = true;
                        break;
                    case Keys.Right:
                        SafeSend("\x1b[C");
                        e.Handled = true;
                        break;
                    case Keys.Left:
                        SafeSend("\x1b[D");
                        e.Handled = true;
                        break;
                    case Keys.Home:
                        SafeSend("\x1b[H");
                        e.Handled = true;
                        break;
                    case Keys.End:
                        SafeSend("\x1b[F");
                        e.Handled = true;
                        break;
                    case Keys.Delete:
                        SafeSend("\x1b[3~");
                        e.Handled = true;
                        break;
                    case Keys.PageUp:
                        SafeSend("\x1b[5~");
                        e.Handled = true;
                        break;
                    case Keys.PageDown:
                        SafeSend("\x1b[6~");
                        e.Handled = true;
                        break;
                }

                if (e.Control && e.KeyCode == Keys.C && !_keyResolver.HasBinding(e))
                {
                    _commandLine.Clear();
                    SafeSend("\x03");
                    e.Handled = true;
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    if (_session != null)
                    {
                        try { _session.OutputReceived -= OnTerminalOutput; } catch { }
                        try { _session.Dispose(); } catch { }
                        _session = null;
                        SessionDisconnected?.Invoke(this, EventArgs.Empty);
                    }
                    try { _autoLogger?.Dispose(); } catch { }
                    try
                    {
                        if (_renderer != null)
                        {
                            var canvas = _renderer.GetControl();
                            Controls.Remove(canvas);
                            _renderer.Dispose();
                        }
                    }
                    catch { }
                }
            }
            base.Dispose(disposing);
        }
    }
}
