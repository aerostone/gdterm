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
    /// SSH/串口/本地终端标签页——懒连接、暂停渲染、危险命令拦截、TerminalProfile 双轨渲染。
    /// 默认 VtCell（真彩/TUI）；Metadata renderer=lightweight 回退 16 色行缓冲。
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
        private IRenderer _renderer;
        private CellGdiRenderer _cellRenderer;
        private TerminalProfile _profile;
        private TerminalAutoLogger _autoLogger;
        private bool _isPaused = true;
        private bool _disposed;
        private bool _connecting;
        private Task _connectTask;
        private bool _mouseDown;
        private int _mouseButton;

        public CredentialPayload Credentials { get; set; }
        public ITerminalSession Session => _session;
        public ConnectionConfig Config => _config;
        public bool IsConnected => _session != null && _session.IsConnected;
        public TerminalKeyBindingResolver KeyResolver => _keyResolver;
        public bool IsVtCell => _cellRenderer != null;
        public TerminalProfile Profile => _profile;

        public event EventHandler<KeyBindingActionEventArgs> ActionRequested;
        public event EventHandler SessionConnected;
        public event EventHandler SessionDisconnected;
        /// <summary>终端尺寸/编码变化（Right=cols,Down=rows,Tag=encoding）。供状态栏显示。</summary>
        public event EventHandler<Size> TerminalInfoChanged;
        /// <summary>用户在右键菜单点了「查找」。</summary>
        public event EventHandler SearchRequested;
        /// <summary>用户在右键菜单点了「重连」。</summary>
        public event EventHandler ReconnectRequested;
        /// <summary>用户在右键菜单点了「导出缓冲」。</summary>
        public event EventHandler ExportRequested;
        /// <summary>用户在右键菜单点了「外观设置」。</summary>
        public event EventHandler AppearanceSettingsRequested;

        private ContextMenuStrip _termMenu;

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
            NormalizeProfile(_profile);
            InitializeComponent();

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
            // 本地会话：Win10 1809+ 用 ConPTY；Win7/Server2008 用 winpty；都没才 Lightweight。
            bool useVt = Gdterm.Terminal.LocalTerminalSession.IsAnyPtyAvailableOnThisOS();
            _profile = new TerminalProfile { Renderer = useVt ? "VtCell" : "Lightweight", TerminalType = "xterm-256color" };
            NormalizeProfile(_profile);
            InitializeComponent();
            AttachExistingSession(localSession);
            _isPaused = false;
            try
            {
                var c = _renderer != null ? _renderer.GetControl() : null;
                if (c != null)
                {
                    c.TabStop = true;
                    BeginInvoke(new Action(() => { try { c.Focus(); } catch { } }));
                }
            }
            catch { }
        }

        private static void NormalizeProfile(TerminalProfile profile)
        {
            if (profile == null) return;
            // 低配默认：scrollback 300；硬顶 2000。不因内存自动切 Lightweight。
            if (profile.ScrollbackLines > 2000) profile.ScrollbackLines = 2000;
            if (profile.ScrollbackLines < 100) profile.ScrollbackLines = 100;
            if (string.IsNullOrWhiteSpace(profile.TerminalType))
                profile.TerminalType = "xterm-256color";
            if (string.IsNullOrWhiteSpace(profile.Renderer))
                profile.Renderer = "VtCell";
            // 本地会话强制 line renderer（Normalize 在构造后调用时保留 Lightweight）
        }

        private void InitializeComponent()
        {
            // 全局外观优先于默认 Classic/Consolas/12；连接级 profile 非默认时覆盖
            var ga = Gdterm.UI.Program.GlobalAppearance;
            string schemeName = _profile != null && !string.IsNullOrWhiteSpace(_profile.ColorScheme)
                && !string.Equals(_profile.ColorScheme, "Classic", StringComparison.OrdinalIgnoreCase)
                    ? _profile.ColorScheme
                    : (ga != null && !string.IsNullOrWhiteSpace(ga.ColorScheme) ? ga.ColorScheme : "Classic");
            var scheme = ColorSchemes.GetByName(schemeName) ?? ColorSchemes.Classic;

            string fontName = _profile != null && !string.IsNullOrWhiteSpace(_profile.FontName)
                && !string.Equals(_profile.FontName, "Consolas", StringComparison.OrdinalIgnoreCase)
                    ? _profile.FontName
                    : (ga != null && !string.IsNullOrWhiteSpace(ga.FontName) ? ga.FontName : "Consolas");
            float fontSize = _profile != null && _profile.FontSize > 0 && _profile.FontSize != 12
                ? _profile.FontSize
                : (ga != null && ga.FontSize > 0 ? ga.FontSize : 12f);
            // CJK 补充字体（可空）—— Xshell 风格非 ASCII 字体。
            string cjkFontName = ga != null && !string.IsNullOrWhiteSpace(ga.CjkFontName) ? ga.CjkFontName : null;

            int rows = 24;
            int cols = 80;
            // 与 TerminalProfile 默认 300 对齐，低配多标签更省
            int history = _profile?.ScrollbackLines ?? 300;

            if (_profile != null && _profile.UseVtCell)
            {
                _cellRenderer = new CellGdiRenderer(rows, cols, scheme, history);
                _cellRenderer.SendToHost += OnCellSendToHost;
                _cellRenderer.TerminalResized += OnCellTerminalResized;
                _renderer = _cellRenderer;
                try { _cellRenderer.ApplyFont(fontName, fontSize, cjkFontName); } catch { }
            }
            else
            {
                _cellRenderer = null;
                _renderer = new LightweightRenderer(rows, cols, scheme);
                try
                {
                    var light = _renderer as LightweightRenderer;
                    if (light != null) light.ApplyFont(fontName, fontSize);
                }
                catch { }
            }

            var canvas = _renderer.GetControl();
            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);

            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;

            canvas.KeyPress += OnKeyPress;
            canvas.KeyDown += OnKeyDown;
            canvas.PreviewKeyDown += (s, e) => { e.IsInputKey = true; };
            canvas.GotFocus += (s, e) => Focus();

            if (_cellRenderer != null)
            {
                canvas.MouseDown += OnCellMouseDown;
                canvas.MouseUp += OnCellMouseUp;
                canvas.MouseMove += OnCellMouseMove;
            }

            KeyPress += OnKeyPress;
            KeyDown += OnKeyDown;
            PreviewKeyDown += (s, e) => { e.IsInputKey = true; };

            BuildContextMenu();
        }

        // ===== 终端右键菜单：复制 / 粘贴 / 清屏 / 查找 / 重连 / 导出 / 外观 =====
        // 行为定调：
        //   - 右键直接弹菜单（SecureCRT / Windows Terminal 习惯）。
        //   - Shift+右键 继续作为 VT 鼠标按钮 2 透传给 vim/less，保留 TUI 应用内右键。
        //   - 复制/粘贴用剪贴板；没有选中文本时「复制」按钮的 Enabled=false。
        private void BuildContextMenu()
        {
            _termMenu = new ContextMenuStrip();
            _termMenu.Opening += (s, e) =>
            {
                // 动态启用/禁用：没选中禁用复制；没连接禁用粘贴/重连/导出
                bool hasSel = false;
                try { hasSel = !string.IsNullOrWhiteSpace(GetSelection()); } catch { }
                SetMenuItemEnabled("_copyItem", hasSel);
                SetMenuItemEnabled("_pasteItem", IsConnected && ClipboardContainsText());
                SetMenuItemEnabled("_clearItem", IsConnected);
                SetMenuItemEnabled("_reconnectItem", !IsConnected || _config != null);
                SetMenuItemEnabled("_exportItem", IsConnected);
            };

            var copyItem = new ToolStripMenuItem("复制(&C)");
            copyItem.Name = "_copyItem";
            copyItem.Click += (s, e) =>
            {
                try
                {
                    var text = GetSelection();
                    if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
                }
                catch (Exception ex) { DiagLog.Swallowed("TerminalControl.Copy", ex); }
            };

            var pasteItem = new ToolStripMenuItem("粘贴(&V)");
            pasteItem.Name = "_pasteItem";
            pasteItem.Click += (s, e) =>
            {
                try
                {
                    if (!IsConnected) return;
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text)) TrySendInput(text);
                }
                catch (Exception ex) { DiagLog.Swallowed("TerminalControl.Paste", ex); }
            };

            _termMenu.Items.Add(copyItem);
            _termMenu.Items.Add(pasteItem);
            _termMenu.Items.Add(new ToolStripSeparator());

            var clearItem = new ToolStripMenuItem("清屏(&L)");
            clearItem.Name = "_clearItem";
            clearItem.Click += (s, e) =>
            {
                try { ClearTerminal(); } catch (Exception ex) { DiagLog.Swallowed("TerminalControl.Clear", ex); }
            };
            _termMenu.Items.Add(clearItem);

            var searchItem = new ToolStripMenuItem("查找(&F)...");
            searchItem.Name = "_searchItem";
            searchItem.Click += (s, e) =>
            {
                try { SearchRequested?.Invoke(this, EventArgs.Empty); } catch { }
            };
            _termMenu.Items.Add(searchItem);
            _termMenu.Items.Add(new ToolStripSeparator());

            var reconnectItem = new ToolStripMenuItem("重连(&R)");
            reconnectItem.Name = "_reconnectItem";
            reconnectItem.Click += (s, e) =>
            {
                try { ReconnectRequested?.Invoke(this, EventArgs.Empty); } catch { }
            };
            _termMenu.Items.Add(reconnectItem);

            var exportItem = new ToolStripMenuItem("导出缓冲(&E)...");
            exportItem.Name = "_exportItem";
            exportItem.Click += (s, e) =>
            {
                try { ExportRequested?.Invoke(this, EventArgs.Empty); } catch { }
            };
            _termMenu.Items.Add(exportItem);
            _termMenu.Items.Add(new ToolStripSeparator());

            var settingsItem = new ToolStripMenuItem("外观设置(&A)...");
            settingsItem.Name = "_settingsItem";
            settingsItem.Click += (s, e) =>
            {
                try { AppearanceSettingsRequested?.Invoke(this, EventArgs.Empty); } catch { }
            };
            _termMenu.Items.Add(settingsItem);

            // 绑到 TerminalControl 本身；canvas 会在 OnCellMouseDown 里拦右键后手动 Show。
            ContextMenuStrip = _termMenu;
        }

        private void SetMenuItemEnabled(string name, bool enabled)
        {
            foreach (ToolStripItem item in _termMenu.Items)
            {
                if (item.Name == name) { item.Enabled = enabled; break; }
            }
        }

        private static bool ClipboardContainsText()
        {
            try { return Clipboard.ContainsText(); } catch { return false; }
        }

        private void OnCellSendToHost(object sender, byte[] data)
        {
            if (_disposed || data == null || data.Length == 0) return;
            if (_session == null || !_session.IsConnected) return;
            try { _session.SendBytes(data); }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.CellSendToHost", ex); }
        }

        /// <summary>当前终端尺寸与编码，供状态栏使用。</summary>
        public Size GetCurrentTerminalInfo()
        {
            try
            {
                int cols = _cellRenderer != null ? _cellRenderer.Columns
                           : _renderer != null ? _renderer.Columns : 80;
                int rows = _cellRenderer != null ? _cellRenderer.Rows
                           : _renderer != null ? _renderer.Rows : 24;
                return new Size(cols, rows);
            }
            catch { return new Size(80, 24); }
        }

        /// <summary>当前编码（从 TerminalProfile 取，默认 UTF-8）。</summary>
        public string CurrentEncoding
        {
            get
            {
                try { return _profile != null && !string.IsNullOrEmpty(_profile.Encoding) ? _profile.Encoding : "UTF-8"; }
                catch { return "UTF-8"; }
            }
        }

        private void OnCellTerminalResized(object sender, EventArgs e)
        {
            if (_disposed || _cellRenderer == null) return;
            try
            {
                var info = new Size(_cellRenderer.Columns, _cellRenderer.Rows);
                TerminalInfoChanged?.Invoke(this, info);
            }
            catch { }
            if (_session == null || !_session.IsConnected) return;
            try
            {
                _session.Resize(_cellRenderer.Columns, _cellRenderer.Rows);
            }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.CellResize", ex); }
        }

        private void OnCellMouseDown(object sender, MouseEventArgs e)
        {
            // 右键：Shift+右键 → VT 鼠标按钮 2（vim/less 等应用内）；裸右键 → 弹菜单。
            if (e.Button == MouseButtons.Right && (ModifierKeys & Keys.Shift) == 0)
            {
                try
                {
                    var canvas = sender as Control;
                    if (canvas != null && _termMenu != null)
                        _termMenu.Show(canvas, e.X, e.Y);
                }
                catch (Exception ex) { DiagLog.Swallowed("TerminalControl.ContextMenuShow", ex); }
                return;
            }

            if (_cellRenderer == null || _session?.IsConnected != true) return;
            int col, row;
            if (!_cellRenderer.TryHitTest(e.X, e.Y, out col, out row)) return;
            _mouseDown = true;
            _mouseButton = MapMouseButton(e.Button);
            try
            {
                _cellRenderer.MousePress(col, row, _mouseButton,
                    (ModifierKeys & Keys.Control) != 0,
                    (ModifierKeys & Keys.Shift) != 0);
            }
            catch { }
        }

        private void OnCellMouseUp(object sender, MouseEventArgs e)
        {
            if (_cellRenderer == null || !_mouseDown) return;
            _mouseDown = false;
            int col, row;
            if (!_cellRenderer.TryHitTest(e.X, e.Y, out col, out row)) return;
            try
            {
                _cellRenderer.MouseRelease(col, row,
                    (ModifierKeys & Keys.Control) != 0,
                    (ModifierKeys & Keys.Shift) != 0);
            }
            catch { }
        }

        private void OnCellMouseMove(object sender, MouseEventArgs e)
        {
            if (_cellRenderer == null || !_mouseDown) return;
            int col, row;
            if (!_cellRenderer.TryHitTest(e.X, e.Y, out col, out row)) return;
            try
            {
                _cellRenderer.MouseMove(col, row, _mouseButton,
                    (ModifierKeys & Keys.Control) != 0,
                    (ModifierKeys & Keys.Shift) != 0);
            }
            catch { }
        }

        private static int MapMouseButton(MouseButtons b)
        {
            if (b == MouseButtons.Left) return 0;
            if (b == MouseButtons.Middle) return 1;
            if (b == MouseButtons.Right) return 2;
            return 0;
        }

                        private void AttachExistingSession(ITerminalSession session)
        {
            _session = session;
            _session.OutputReceived += OnTerminalOutput;
            try
            {
                if (session is LocalTerminalSession local && !local.IsConnected)
                    local.ConnectLocal();

                // 立刻把启动横幅画到画布，避免事件时序导致空白
                try
                {
                    var recent = session.GetRecentOutput(50);
                    if (recent != null)
                    {
                        foreach (var line in recent)
                        {
                            if (!string.IsNullOrEmpty(line))
                                _renderer?.Write(line);
                        }
                    }
                }
                catch { }

                if (session is LocalTerminalSession)
                {
                    try
                    {
                        _renderer?.Write("\r\n\x1b[32m[本地终端] 可输入命令；输入 exit 退出\x1b[0m\r\n");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                try { _renderer?.Write("\r\n\x1b[31m本地终端启动失败: " + ex.Message + "\x1b[0m\r\n"); } catch { }
                DiagLog.Swallowed("TerminalControl.AttachLocal", ex);
            }
        }

public async void Connect()
        {
            try { await ConnectAsyncCore().ConfigureAwait(true); }
            catch { }
        }

        public Task ConnectAsyncIfNeeded()
        {
            if (_disposed) return Task.FromResult(false).ContinueWith(_ => { });
            if (_session != null && _session.IsConnected) return Task.CompletedTask;
            if (_connectTask != null && !_connectTask.IsCompleted) return _connectTask;
            _connectTask = ConnectAsyncCore();
            return _connectTask;
        }

        private async Task ConnectAsyncCore()
        {
            if (_session != null || _connecting || _disposed) return;
            _connecting = true;
            try
            {
                DiagLog.Info("TerminalControl.ConnectAsyncCore",
                    "begin id=" + (_config != null ? _config.Id : "") +
                    " host=" + (_config != null ? _config.Host : "") +
                    " proto=" + (_config != null ? _config.Protocol.ToString() : ""));
            }
            catch { }

            try
            {
                var credential = Credentials ?? new CredentialPayload { Username = _config.Username };
                try
                {
                    var hasPwd = !string.IsNullOrEmpty(credential.Password);
                    var hasKey = credential.SshPrivateKey != null && credential.SshPrivateKey.Length > 0;
                    DiagLog.Info("TerminalControl.ConnectAsyncCore",
                        "auth user=" + (credential.Username ?? "") +
                        " hasPassword=" + hasPwd +
                        " hasPrivateKey=" + hasKey +
                        " keepassCredInjected=" + (Credentials != null));
                    if (!hasPwd && !hasKey && _config.Protocol == ProtocolType.SSH)
                    {
                        void WarnNoCred()
                        {
                            _renderer?.Write(
                                "\r\n\x1b[33m[认证] 未注入密码/私钥。请先解锁 KeePass，并在连接上关联凭据，" +
                                "或在密码库中为该主机建立条目。\x1b[0m\r\n");
                        }
                        if (InvokeRequired) BeginInvoke(new Action(WarnNoCred));
                        else WarnNoCred();
                    }
                }
                catch { }
                ITerminalSession session;

                int rows = _renderer != null ? Math.Max(1, _renderer.Rows) : 24;
                int cols = _renderer != null ? Math.Max(2, _renderer.Columns) : 80;

                if (_config.Protocol == ProtocolType.Serial)
                {
                    if (_terminalFactory == null)
                        throw new InvalidOperationException("ITerminalSessionFactory 未注入，无法创建串口会话");
                    session = _terminalFactory.CreateSerial();
                    await Task.Run(() => session.Connect(_config, credential, rows, cols)).ConfigureAwait(false);
                }
                else
                {
                    if (_terminalFactory == null)
                        throw new InvalidOperationException("ITerminalSessionFactory 未注入，无法创建 SSH 会话");
                    session = _terminalFactory.Create(new TerminalEndpoint { Host = _config.Host, Port = _config.Port });

                    // 跳板在 JumpChain；Tunnel 仅端口转发参数。任一存在且有隧道管理器则走隧道。
                    bool needTunnel = _tunnelManager != null && (
                        (_config.JumpChain != null && _config.JumpChain.Hops != null && _config.JumpChain.Hops.Count > 0)
                        || _config.Tunnel != null);
                    if (needTunnel)
                    {
                        DiagLog.Info("TerminalControl.ConnectAsyncCore", "establish tunnel hops=" +
                            (_config.JumpChain != null && _config.JumpChain.Hops != null
                                ? _config.JumpChain.Hops.Count.ToString() : "0"));
                        var tunnelEndpoint = await _tunnelManager.EstablishAsync(
                            _config, credential, System.Threading.CancellationToken.None).ConfigureAwait(false);
                        await Task.Run(() => session.ConnectViaTunnel(_config, credential, tunnelEndpoint, rows, cols))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Run(() => session.Connect(_config, credential, rows, cols)).ConfigureAwait(false);
                    }
                }

                if (_disposed)
                {
                    try { session.Dispose(); } catch { }
                    return;
                }

                _session = session;
                _session.OutputReceived += OnTerminalOutput;
                _session.Disconnected += OnSessionDisconnected;

                // 连接后同步一次尺寸（cell 路径）
                if (_cellRenderer != null)
                {
                    try { _session.Resize(_cellRenderer.Columns, _cellRenderer.Rows); }
                    catch { }
                }

                if (_profile.AutoRunCommands != null)
                {
                    void RunAuto()
                    {
                        foreach (var cmd in _profile.AutoRunCommands)
                        {
                            if (!string.IsNullOrWhiteSpace(cmd))
                                TrySendInput(cmd + (_profile.NewLineSequence ?? "\n"), isCommandLine: true);
                        }
                    }
                    if (InvokeRequired) BeginInvoke(new Action(RunAuto));
                    else RunAuto();
                }

                try
                {
                    _auditLogger?.LogConnection(
                        _config.Id,
                        _config.Host ?? _config.Name,
                        (_config.Protocol).ToString(),
                        ConnectionAction.Open);
                    if (!string.IsNullOrEmpty(credential?.Username))
                        _auditLogger?.LogCredentialUse(_config.Id, credential.Username, CredentialAction.AutoFill);
                }
                catch (Exception ex) { DiagLog.Swallowed("TerminalControl.AuditOpen", ex); }

                void RaiseConnected() { SessionConnected?.Invoke(this, EventArgs.Empty); }
                if (InvokeRequired) BeginInvoke(new Action(RaiseConnected));
                else RaiseConnected();
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TerminalControl.ConnectAsyncCore", ex);
                if (_disposed) return;
                void WriteFail()
                {
                    _renderer?.Write("\r\n\x1b[31m连接失败: " + ex.Message + "\x1b[0m\r\n");
                }
                if (InvokeRequired) BeginInvoke(new Action(WriteFail));
                else WriteFail();
                try
                {
                    _auditLogger?.LogConnection(
                        _config.Id,
                        _config.Host ?? _config.Name,
                        (_config.Protocol).ToString(),
                        ConnectionAction.Error);
                }
                catch (Exception auditEx) { DiagLog.Swallowed("TerminalControl.AuditError", auditEx); }
            }
            finally
            {
                _connecting = false;
            }
        }

        private void OnSessionDisconnected(object sender, EventArgs e)
        {
            if (_disposed) return;
            void Raise()
            {
                try { SessionDisconnected?.Invoke(this, EventArgs.Empty); } catch { }
            }
            if (InvokeRequired) BeginInvoke(new Action(Raise));
            else Raise();
        }

        public void EnableAutoLog(string logDirectory)
        {
            if (string.IsNullOrEmpty(logDirectory) || _autoLogger != null) return;
            _autoLogger = new TerminalAutoLogger(logDirectory)
            {
                MaxFileSizeBytes = 10 * 1024 * 1024,
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
            {
                try
                {
                    DiagLog.Info("TerminalControl.ResumeRendering",
                        "lazy-connect id=" + (_config != null ? _config.Id : ""));
                }
                catch { }
                ConnectAsyncIfNeeded();
            }
            else if (_session != null && !_session.IsConnected && _session is LocalTerminalSession local)
            {
                // 本地终端已 Attach 但进程未起
                try { local.ConnectLocal(); }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("TerminalControl.ResumeRendering.Local", ex);
                    try { _renderer?.Write("\r\n\x1b[31m本地终端启动失败: " + ex.Message + "\x1b[0m\r\n"); } catch { }
                }
            }
        }

        public bool TrySendInput(string text, bool isCommandLine = false)
        {
            if (string.IsNullOrEmpty(text) || _session == null || !_session.IsConnected)
                return false;

            var trimmed = isCommandLine ? text.TrimEnd('\r', '\n') : text;
            if (isCommandLine && !ConfirmIfDangerous(trimmed))
                return false;

            if (isCommandLine)
                ClearLocalLine(eraseDisplay: true);

            if (!SafeSend(text))
                return false;

            if (isCommandLine && !string.IsNullOrWhiteSpace(trimmed))
            {
                try { _auditLogger?.LogCommand(_config?.Id ?? "", trimmed); }
                catch { }
            }

            return true;
        }

        public void SendInput(string text)
        {
            TrySendInput(text, isCommandLine: true);
        }

        private bool UseLocalLineBuffer
        {
            get
            {
                // 本地 shell：始终本地回显（重定向进程 echo 不可靠）
                if (_session is LocalTerminalSession) return true;
                // cell/TUI：危险命令整行拦截；本地缓冲仅 Lightweight
                return _dangerousDetector != null && _cellRenderer == null;
            }
        }

        /// <summary>VtCell 下危险命令：仅在 Enter 整行时拦截（字符已直通时用 Ctrl+C 中止）。</summary>
        private bool UseVtCellDangerGate
        {
            get { return _dangerousDetector != null && _cellRenderer != null; }
        }

        public bool ConfirmDangerousCommand(string command)
        {
            return ConfirmIfDangerous(command);
        }

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

            // 暂停标签：仍喂引擎以保持 TUI/alt-screen 状态，但不进 UI 消息泵、不重绘
            // （CellGdiRenderer.Write 在 Pause 时只 Feed，不启动 timer）
            if (_isPaused)
            {
                try { _renderer?.Write(e.Text); } catch { }
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

            if (!char.IsControl(e.KeyChar))
            {
                _commandLine.Append(e.KeyChar);

                if (_cellRenderer != null)
                {
                    // TUI：优先 VtNetCore KeyPressed；失败则明文
                    var keyName = e.KeyChar.ToString();
                    bool handled = false;
                    try { handled = _cellRenderer.TryKeyPressed(keyName, false, false); } catch { }
                    if (!handled)
                        SafeSend(keyName);
                }
                else if (UseLocalLineBuffer)
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
                // Cell 路径：方向键等走 VtNetCore（应用光标模式）
                if (_cellRenderer != null && TryCellSpecialKey(e))
                {
                    e.Handled = true;
                    return;
                }

                switch (e.KeyCode)
                {
                    case Keys.Enter:
                    {
                        var cmd = _commandLine.ToString();
                        if (UseLocalLineBuffer)
                        {
                            if (!ConfirmIfDangerous(cmd))
                            {
                                ClearLocalLine(eraseDisplay: true);
                                e.Handled = true;
                                return;
                            }
                            _commandLine.Clear();
                            if (cmd.Length > 0)
                                SafeSend(cmd);
                            SafeSend("\r");
                        }
                        else if (UseVtCellDangerGate)
                        {
                            // 字符已直通远端；危险则 Ctrl+C 中止
                            _commandLine.Clear();
                            if (!ConfirmIfDangerous(cmd))
                            {
                                SafeSend("\x03");
                                e.Handled = true;
                                return;
                            }
                            if (_cellRenderer == null || !_cellRenderer.TryKeyPressed("Enter", e.Control, e.Shift))
                                SafeSend("\r");
                        }
                        else
                        {
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
                            _commandLine.Length--;
                        if (UseLocalLineBuffer)
                        {
                            try { _renderer?.Write("\b \b"); } catch { }
                        }
                        else if (_cellRenderer != null)
                        {
                            if (!_cellRenderer.TryKeyPressed("Back", e.Control, e.Shift))
                                SafeSend("\b");
                        }
                        else
                        {
                            SafeSend("\b");
                        }
                        e.Handled = true;
                        break;
                    case Keys.Tab:
                        if (UseLocalLineBuffer && _commandLine.Length > 0)
                        {
                            var partial = _commandLine.ToString();
                            _commandLine.Clear();
                            SafeSend(partial);
                        }
                        if (_cellRenderer != null)
                        {
                            if (!_cellRenderer.TryKeyPressed("Tab", e.Control, e.Shift))
                                SafeSend("\t");
                        }
                        else
                        {
                            SafeSend("\t");
                        }
                        e.Handled = true;
                        break;
                    case Keys.Escape:
                        // 不在此消费 Esc：交给 MainForm ProcessCmdKey 退出专注模式
                        // （终端应用如 vim 仍可用其它快捷键；专注模式优先可退出）
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

        /// <summary>VtNetCore 键名映射；成功则已通过 SendToHost 发往会话。</summary>
        private bool TryCellSpecialKey(KeyEventArgs e)
        {
            if (_cellRenderer == null) return false;
            string name = null;
            switch (e.KeyCode)
            {
                case Keys.Up: name = "Up"; break;
                case Keys.Down: name = "Down"; break;
                case Keys.Left: name = "Left"; break;
                case Keys.Right: name = "Right"; break;
                case Keys.Home: name = "Home"; break;
                case Keys.End: name = "End"; break;
                case Keys.Insert: name = "Insert"; break;
                case Keys.Delete: name = "Delete"; break;
                case Keys.PageUp: name = "PageUp"; break;
                case Keys.PageDown: name = "PageDown"; break;
                case Keys.F1: name = "F1"; break;
                case Keys.F2: name = "F2"; break;
                case Keys.F3: name = "F3"; break;
                case Keys.F4: name = "F4"; break;
                case Keys.F5: name = "F5"; break;
                case Keys.F6: name = "F6"; break;
                case Keys.F7: name = "F7"; break;
                case Keys.F8: name = "F8"; break;
                case Keys.F9: name = "F9"; break;
                case Keys.F10: name = "F10"; break;
                case Keys.F11: name = "F11"; break;
                case Keys.F12: name = "F12"; break;
                default: return false;
            }

            // 清空本地命令缓冲（历史导航等）
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                ClearLocalLine(eraseDisplay: false);

            try
            {
                if (_cellRenderer.TryKeyPressed(name, e.Control, e.Shift))
                    return true;
            }
            catch { }
            return false;
        }

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
                    if (_cellRenderer != null)
                    {
                        try { _cellRenderer.SendToHost -= OnCellSendToHost; } catch { }
                        try { _cellRenderer.TerminalResized -= OnCellTerminalResized; } catch { }
                    }
                    if (_session != null)
                    {
                        DiagLog.Try("TerminalControl.Dispose.Unsub", () =>
                        {
                            _session.OutputReceived -= OnTerminalOutput;
                            _session.Disconnected -= OnSessionDisconnected;
                        });
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
                            var cell = _renderer as CellGdiRenderer;
                            if (cell != null) cell.Dispose();
                            else
                            {
                                var light = _renderer as LightweightRenderer;
                                if (light != null) light.Dispose();
                            }
                        }
                    });
                    _cellRenderer = null;
                    _renderer = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
