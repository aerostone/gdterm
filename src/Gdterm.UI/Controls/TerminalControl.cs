using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.Terminal.Models;
using Gdterm.Terminal.Rendering;
using Gdterm.Terminal.Themes;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// SSH/串口/本地终端标签页控件——懒连接、暂停渲染、危险命令拦截、TerminalProfile
    /// </summary>
    public class TerminalControl : UserControl, IDisposable
    {
        private readonly ConnectionConfig _config;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly ITunnelManager _tunnelManager;
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
            ITunnelManager tunnelManager,
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

            // 默认关闭；仅当 Metadata/terminalProfile 显式 autoLog=true 时启用
            if (_profile != null && _profile.AutoLog)
            {
                try
                {
                    var logDir = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "data", "logs", "terminal");
                    EnableAutoLog(logDir);
                }
                catch { }
            }
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
                    if (_terminalFactory == null)
                        throw new InvalidOperationException("ITerminalSessionFactory 未注入，无法创建串口会话");
                    session = _terminalFactory.CreateSerial();
                    await Task.Run(() => session.Connect(_config, credential));
                }
                else
                {
                    if (_terminalFactory == null)
                        throw new InvalidOperationException("ITerminalSessionFactory 未注入，无法创建 SSH 会话");
                    session = _terminalFactory.Create(new TerminalEndpoint { Host = _config.Host, Port = _config.Port });

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

                // finding-01：关签与 Connect 完成竞态——控件已 dispose 则立刻丢弃会话
                if (_disposed)
                {
                    try { session.Dispose(); } catch { }
                    return;
                }

                _session = session;
                _session.OutputReceived += OnTerminalOutput;

                // 自动运行命令（profile）——走危险命令闸门
                if (_profile.AutoRunCommands != null)
                {
                    foreach (var cmd in _profile.AutoRunCommands)
                    {
                        if (!string.IsNullOrWhiteSpace(cmd))
                            TrySendInput(cmd + (_profile.NewLineSequence ?? "\n"), isCommandLine: true);
                    }
                }

                _auditLogger?.LogConnection(
                    _config.Id,
                    _config.Host ?? _config.Name,
                    (_config.Protocol).ToString(),
                    ConnectionAction.Open);
                SessionConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                _renderer?.Write("\r\n\x1b[31m连接失败: " + ex.Message + "\x1b[0m\r\n");
                _auditLogger?.LogConnection(
                    _config.Id,
                    _config.Host ?? _config.Name,
                    (_config.Protocol).ToString(),
                    ConnectionAction.Error);
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

        /// <summary>
        /// 向终端发送文本。isCommandLine=true 时走危险命令闸门（整行确认后再下发）。
        /// </summary>
        public bool TrySendInput(string text, bool isCommandLine = false)
        {
            if (string.IsNullOrEmpty(text) || _session == null || !_session.IsConnected)
                return false;

            var trimmed = isCommandLine ? text.TrimEnd('\r', '\n') : text;
            if (isCommandLine && !ConfirmIfDangerous(trimmed))
                return false;

            // 外部整行发送会打乱本地行缓冲
            if (isCommandLine)
                ClearLocalLine(eraseDisplay: true);

            if (!SafeSend(text))
                return false;

            if (isCommandLine && !string.IsNullOrWhiteSpace(trimmed))
            {
                try
                {
                    _auditLogger?.LogCommand(_config?.Id ?? "", trimmed);
                }
                catch { }
            }

            return true;
        }

        public void SendInput(string text)
        {
            TrySendInput(text, isCommandLine: true);
        }

        /// <summary>是否启用本地行缓冲（有检测器时：确认前不向远端逐字发送）</summary>
        private bool UseLocalLineBuffer
        {
            get { return _dangerousDetector != null; }
        }

        /// <summary>供 AI/外部调用的危险命令确认入口</summary>
        public bool ConfirmDangerousCommand(string command)
        {
            return ConfirmIfDangerous(command);
        }

        /// <summary>危险命令确认；安全或用户确认返回 true</summary>
        private bool ConfirmIfDangerous(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || _dangerousDetector == null)
                return true;

            CommandCheckResult check;
            try
            {
                check = _dangerousDetector.Check(command);
            }
            catch (Exception ex)
            {
                // finding-02：fail-closed——检测异常视为拦截
                try
                {
                    _auditLogger?.LogSecurityEvent(
                        SecurityEvent.DangerousCommandBlocked,
                        "detector error on " + (_config?.Host ?? "?") + ": " + ex.Message);
                }
                catch { }
                try
                {
                    MessageBox.Show(
                        FindForm(),
                        "危险命令检测失败，已阻止发送该命令。\n" + ex.Message,
                        "安全拦截",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                catch { }
                return false;
            }

            if (check == null || !check.IsDangerous)
                return true;

            using (var dlg = new DangerousCommandDialog(command, check))
            {
                dlg.ShowDialog(FindForm());
                if (!dlg.IsConfirmed)
                {
                    try
                    {
                        _auditLogger?.LogSecurityEvent(
                            SecurityEvent.DangerousCommandBlocked,
                            "blocked command on " + (_config?.Host ?? "?") + ": " + command);
                    }
                    catch { }
                    return false;
                }
                if (dlg.RememberChoice)
                {
                    try { _dangerousDetector.AddToWhitelist(command); } catch { }
                }
            }
            return true;
        }

        private void ClearLocalLine(bool eraseDisplay)
        {
            if (eraseDisplay && _commandLine.Length > 0 && UseLocalLineBuffer)
            {
                // 用退格擦除本地回显（远端尚未收到这些字符）
                var erase = new StringBuilder();
                for (int i = 0; i < _commandLine.Length; i++)
                    erase.Append("\b \b");
                try { _renderer?.Write(erase.ToString()); } catch { }
            }
            _commandLine.Clear();
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

            // finding-08：暂停标签不向 UI 线程泵输出；仅后台写 auto-log（若启用）
            if (_isPaused)
            {
                try { _autoLogger?.LogOutput(e.Text); } catch { }
                return;
            }

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnTerminalOutput(sender, e))); }
                catch { }
                return;
            }

            _renderer?.Write(e.Text);
            try { _autoLogger?.LogOutput(e.Text); } catch { }
        }

        private void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            if (_session?.IsConnected != true) return;

            // 可打印字符：有检测器时只进本地缓冲+本地回显，确认前不发远端
            if (!char.IsControl(e.KeyChar))
            {
                _commandLine.Append(e.KeyChar);
                if (UseLocalLineBuffer)
                {
                    try { _renderer?.Write(e.KeyChar.ToString()); } catch { }
                }
                else
                {
                    SafeSend(e.KeyChar.ToString());
                }
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
                    {
                        var cmd = _commandLine.ToString();
                        if (UseLocalLineBuffer)
                        {
                            // 确认前命令体从未离开本机
                            if (!ConfirmIfDangerous(cmd))
                            {
                                ClearLocalLine(eraseDisplay: true);
                                e.Handled = true;
                                return;
                            }
                            // 整行下发（远端此前未见字符）
                            _commandLine.Clear();
                            if (cmd.Length > 0)
                                SafeSend(cmd);
                            SafeSend("\r");
                        }
                        else
                        {
                            // 无检测器：字符已逐字下发，只补回车（仍尝试闸门，失败则 Ctrl+C）
                            _commandLine.Clear();
                            if (!ConfirmIfDangerous(cmd))
                            {
                                SafeSend("\x03");
                                e.Handled = true;
                                return;
                            }
                            SafeSend("\r");
                        }
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            try { _auditLogger?.LogCommand(_config?.Id ?? "", cmd); }
                            catch { }
                        }
                        e.Handled = true;
                        break;
                    }
                    case Keys.Back:
                        if (_commandLine.Length > 0)
                        {
                            _commandLine.Length--;
                            if (UseLocalLineBuffer)
                            {
                                try { _renderer?.Write("\b \b"); } catch { }
                            }
                            else
                            {
                                SafeSend("\b");
                            }
                        }
                        else if (!UseLocalLineBuffer)
                        {
                            SafeSend("\b");
                        }
                        e.Handled = true;
                        break;
                    case Keys.Tab:
                        // Tab 补全需要远端：丢弃本地缓冲后直通
                        if (UseLocalLineBuffer && _commandLine.Length > 0)
                        {
                            var partial = _commandLine.ToString();
                            _commandLine.Clear();
                            SafeSend(partial);
                        }
                        SafeSend("\t");
                        e.Handled = true;
                        break;
                    case Keys.Escape:
                        ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
                        SafeSend("\x1b");
                        e.Handled = true;
                        break;
                    case Keys.Up:
                        ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
                        SafeSend("\x1b[A");
                        e.Handled = true;
                        break;
                    case Keys.Down:
                        ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
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
                    ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
                    SafeSend("\x03");
                    e.Handled = true;
                }
            }
            catch { }
        }

        /// <summary>锁屏时擦除内存中的明文凭据（finding-04）</summary>
        public void ClearCachedCredentials()
        {
            Credentials = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    Credentials = null;
                    if (_session != null)
                    {
                        DiagLog.Try("TerminalControl.Dispose.Unsub", () => _session.OutputReceived -= OnTerminalOutput);
                        DiagLog.Try("TerminalControl.Dispose.Session", () => _session.Dispose());
                        _session = null;
                        try { SessionDisconnected?.Invoke(this, EventArgs.Empty); } catch { }
                    }
                    DiagLog.Try("TerminalControl.Dispose.AutoLog", () => _autoLogger?.Dispose());
                    DiagLog.Try("TerminalControl.Dispose.Renderer", () =>
                    {
                        if (_renderer != null)
                        {
                            var canvas = _renderer.GetControl();
                            Controls.Remove(canvas);
                            _renderer.Dispose();
                        }
                    });
                }
            }
            base.Dispose(disposing);
        }
    }
}
