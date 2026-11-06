using System;
using System.Drawing;
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
    public partial class TerminalControl : UserControl, IDisposable
    {
        private readonly ConnectionConfig _config;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly ITunnelManager _tunnelManager;
        private readonly IAuditLogger _auditLogger;
        private readonly DangerousCommandDetector _dangerousDetector;
        private readonly TerminalKeyBindingResolver _keyResolver = new TerminalKeyBindingResolver();
        private readonly StringBuilder _commandLine = new StringBuilder();
        private MacroRecorder _macroRecorder;
        private TerminalCompletion _completion;
        private TerminalCompletionPopup _popup;
        /// <summary>补全数据源（可选，不设则只用内置常用命令）。由 TabContainer.HandleTerminalConnected 下发。</summary>
        public Gdterm.Logging.CommandHistoryStore HistoryStore { get; set; }
        public Gdterm.Connections.QuickCommandStore QuickStore { get; set; }

        private ITerminalSession _session;
        private IRenderer _renderer;
        private CellGdiRenderer _cellRenderer;
        private TerminalProfile _profile;
        private TerminalAutoLogger _autoLogger;
        private bool _isPaused = true;
        private bool _disposed;
        private bool _connecting;
        private bool _fontMetricsLoggedWithLayout;
        private bool _fontMetricsLoggedVisible;
        private Task _connectTask;
        private bool _mouseDown;
        private int _mouseButton;
        private bool _selecting;

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
        /// <summary>用户在右键菜单点了「打开对应连接」（选中文本含 host:port，A3-lite）。</summary>
        public event EventHandler<HostPortEventArgs> HostPortOpenRequested;
        /// <summary>OSC 7 上报的远端当前目录（SFTP 跟随、标题栏用）。</summary>
        public event EventHandler<string> DirectoryChanged;
        /// <summary>OSC 9 / 777 / 99 应用通知。</summary>
        public event EventHandler<Gdterm.Terminal.Rendering.Vt.TerminalNotification> NotificationReceived;
        /// <summary>Ctrl+Click 到远端绝对路径（由宿主决定用 SFTP 还是复制）。</summary>
        public event EventHandler<string> RemotePathOpenRequested;

        /// <summary>host:port 参数。</summary>
        public sealed class HostPortEventArgs : EventArgs
        {
            public string Host { get; private set; }
            public int Port { get; private set; }
            public HostPortEventArgs(string host, int port) { Host = host; Port = port; }
        }

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
                        AppDomain.CurrentDomain.BaseDirectory, "logs", "terminal");
                    EnableAutoLog(logDir);
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                    BeginInvoke(new Action(() => { try { c.Focus(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } } }));
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
            // 终端字号优先级：GlobalAppearance.FontSize 是“全局用户选择”，应优先于 Profile.FontSize 默认 12 生效。
            // 若 Profile.FontSize 被显式设置（>0），则用 Profile 值覆盖全局；
            // 最后兜底 14f。
            float fontSize = (ga != null && ga.FontSize > 0)
                ? (float)ga.FontSize
                : (_profile != null && _profile.FontSize > 0
                    ? (float)_profile.FontSize
                    : 14f);
            // CJK 补充字体（可空）—— Xshell 风格非 ASCII 字体。
            string cjkFontName = ga != null && !string.IsNullOrWhiteSpace(ga.CjkFontName) ? ga.CjkFontName : null;

            // 可观测性：字号/字体的实际来源链（“字号不匹配”排查第一步——先看是谁提供的值）
            try
            {
                DiagLog.Info("TerminalControl.Appearance",
                    "renderer=" + (_profile != null && _profile.UseVtCell ? "VtCell" : "Lightweight") +
                    " scheme=" + schemeName +
                    " font=" + fontName +
                    " size=" + fontSize.ToString("0.#") +
                    " sizeSrc=" + (ga != null && ga.FontSize > 0 ? "global" : (_profile != null && _profile.FontSize > 0 ? "profile" : "default14")) +
                    " fontSrc=" + ((_profile != null && !string.IsNullOrWhiteSpace(_profile.FontName)
                        && !string.Equals(_profile.FontName, "Consolas", StringComparison.OrdinalIgnoreCase)) ? "profile" : (ga != null && !string.IsNullOrWhiteSpace(ga.FontName) ? "global" : "default")) +
                    " cjk=" + (string.IsNullOrEmpty(cjkFontName) ? "-" : cjkFontName));
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }

            int rows = 24;
            int cols = 80;
            // 与 TerminalProfile 默认 300 对齐，低配多标签更省
            int history = Gdterm.Core.Constants.TerminalLimits.ClampScrollback(
                _profile?.ScrollbackLines ?? Gdterm.Core.Constants.TerminalLimits.DefaultScrollback, rows);

            if (_profile != null && _profile.UseVtCell)
            {
                _cellRenderer = new CellGdiRenderer(rows, cols, scheme, history);
                _cellRenderer.SendToHost += OnCellSendToHost;
                _cellRenderer.TerminalResized += OnCellTerminalResized;
                _cellRenderer.ClipboardSetRequested += OnCellClipboardSetRequested;
                _cellRenderer.DirectoryChanged += (s2, path) =>
                {
                    var h = DirectoryChanged;
                    if (h != null) { try { h(this, path); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } } }
                };
                _cellRenderer.NotificationReceived += (s2, n) =>
                {
                    var h = NotificationReceived;
                    if (h != null) { try { h(this, n); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } } }
                };
                _renderer = _cellRenderer;
                try { _cellRenderer.ApplyFont(fontName, fontSize, cjkFontName); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                // TERM 名与 pty 请求保持一致（XTGETTCAP TN / XTVERSION 会回它）
                try { if (_profile != null) _cellRenderer.ApplyTerminalType(_profile.TerminalType); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                LogFontMetrics("cell");
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
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                LogFontMetrics("light");
            }

            var canvas = _renderer.GetControl();
            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);

            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;

            canvas.KeyPress += OnKeyPress;
            canvas.KeyDown += OnKeyDown;
            canvas.PreviewKeyDown += (s, e) => { e.IsInputKey = true; };
            canvas.GotFocus += (s, e) =>
            {
                Focus();
                // ?1004：应用要求焦点事件时才发（vim/neovim/tmux 靠它判断窗口切换）
                try { if (_cellRenderer != null) _cellRenderer.ReportFocus(true); }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            };
            canvas.LostFocus += (s, e) =>
            {
                try { if (_cellRenderer != null) _cellRenderer.ReportFocus(false); }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            };

            if (_cellRenderer != null)
            {
                canvas.MouseDown += OnCellMouseDown;
                canvas.MouseUp += OnCellMouseUp;
                canvas.MouseMove += OnCellMouseMove;
                canvas.MouseWheel += OnCellMouseWheel;
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
                try { hasSel = !string.IsNullOrWhiteSpace(GetSelection()); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                SetMenuItemEnabled("_copyItem", hasSel);
                SetMenuItemEnabled("_pasteItem", IsConnected && ClipboardContainsText());
                SetMenuItemEnabled("_clearItem", IsConnected);
                SetMenuItemEnabled("_reconnectItem", !IsConnected || _config != null);
                SetMenuItemEnabled("_exportItem", IsConnected);
                // 在线动作：需要选中；IP 查询需选中含合法 IPv4；host:port 需含合法地址
                SetMenuItemEnabled("_onlineSearchItem", hasSel);
                SetMenuItemEnabled("_onlineTranslateItem", hasSel);
                bool hasIp = false;
                bool hasHostPort = false;
                try
                {
                    hasIp = hasSel && Gdterm.Core.OnlineLinks.ExtractFirstIpv4(GetSelection()) != null;
                    hasHostPort = hasSel && Gdterm.Core.OnlineLinks.ExtractFirstHostPort(GetSelection()) != null;
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                SetMenuItemEnabled("_onlineIpItem", hasIp);
                SetMenuItemEnabled("_onlineHostPortItem", hasHostPort);
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
                try { PasteFromClipboard(); }
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
                try { SearchRequested?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            };
            _termMenu.Items.Add(searchItem);

            // 在线动作（nyaterm 对比吸收 A4）：选中文本 → 浏览器搜索/翻译/IP 查询
            var onlineMenu = new ToolStripMenuItem("在线(&S)");
            var onlineSearchItem = new ToolStripMenuItem("搜索(Bing)(&G)");
            onlineSearchItem.Name = "_onlineSearchItem";
            onlineSearchItem.Click += (s, e) => OpenOnlineUrl(
                () => Gdterm.Core.OnlineLinks.BuildSearchUrl(GetSelection()), "TerminalControl.OnlineSearch");
            var onlineTranslateItem = new ToolStripMenuItem("翻译(&T)");
            onlineTranslateItem.Name = "_onlineTranslateItem";
            onlineTranslateItem.Click += (s, e) => OpenOnlineUrl(
                () => Gdterm.Core.OnlineLinks.BuildTranslateUrl(GetSelection()), "TerminalControl.OnlineTranslate");
            var onlineIpItem = new ToolStripMenuItem("IP 查询(&I)");
            onlineIpItem.Name = "_onlineIpItem";
            onlineIpItem.Click += (s, e) =>
            {
                try
                {
                    var ip = Gdterm.Core.OnlineLinks.ExtractFirstIpv4(GetSelection());
                    if (ip == null)
                    {
                        MessageBox.Show(this, "选中文本中未找到合法 IPv4 地址。",
                            "IP 查询", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    OpenOnlineUrl(() => Gdterm.Core.OnlineLinks.BuildIpLookupUrl(ip), "TerminalControl.OnlineIp");
                }
                catch (Exception ex) { DiagLog.Swallowed("TerminalControl.OnlineIp", ex); }
            };
            onlineMenu.DropDownItems.Add(onlineSearchItem);
            onlineMenu.DropDownItems.Add(onlineTranslateItem);
            onlineMenu.DropDownItems.Add(onlineIpItem);
            var onlineHostPortItem = new ToolStripMenuItem("打开对应连接(host:port)(&O)");
            onlineHostPortItem.Name = "_onlineHostPortItem";
            onlineHostPortItem.Click += (s, e) =>
            {
                try
                {
                    var hp = Gdterm.Core.OnlineLinks.ExtractFirstHostPort(GetSelection());
                    if (hp == null)
                    {
                        MessageBox.Show(this, "选中文本中未找到 host:port 地址。",
                            "打开连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var hpe = HostPortOpenRequested;
                    if (hpe != null) hpe(this, new HostPortEventArgs(hp.Host, hp.Port));
                }
                catch (Exception ex) { DiagLog.Swallowed("TerminalControl.HostPort", ex); }
            };
            onlineMenu.DropDownItems.Add(onlineHostPortItem);
            _termMenu.Items.Add(onlineMenu);
            _termMenu.Items.Add(new ToolStripSeparator());

            var reconnectItem = new ToolStripMenuItem("重连(&R)");
            reconnectItem.Name = "_reconnectItem";
            reconnectItem.Click += (s, e) =>
            {
                try { ReconnectRequested?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            };
            _termMenu.Items.Add(reconnectItem);

            var exportItem = new ToolStripMenuItem("导出缓冲(&E)...");
            exportItem.Name = "_exportItem";
            exportItem.Click += (s, e) =>
            {
                try { ExportRequested?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            };
            _termMenu.Items.Add(exportItem);
            _termMenu.Items.Add(new ToolStripSeparator());

            var settingsItem = new ToolStripMenuItem("外观设置(&A)...");
            settingsItem.Name = "_settingsItem";
            settingsItem.Click += (s, e) =>
            {
                try { AppearanceSettingsRequested?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            };
            _termMenu.Items.Add(settingsItem);

            // 绑到 TerminalControl 本身；canvas 会在 OnCellMouseDown 里拦右键后手动 Show。,
            ContextMenuStrip = _termMenu;
        }

        private void SetMenuItemEnabled(string name, bool enabled)
        {
            foreach (ToolStripItem item in _termMenu.Items)
                SetMenuItemEnabledRecursive(item, name, enabled);
        }

        /// <summary>递归查找（在线子菜单的项在 DropDownItems 里，顶层查找够不到）。</summary>
        private static void SetMenuItemEnabledRecursive(ToolStripItem item, string name, bool enabled)
        {
            if (item.Name == name) item.Enabled = enabled;
            var drop = item as ToolStripMenuItem;
            if (drop != null)
                foreach (ToolStripItem child in drop.DropDownItems)
                    SetMenuItemEnabledRecursive(child, name, enabled);
        }

        /// <summary>在线动作统一出口：默认浏览器打开 URL（A4）。</summary>
        private void OpenOnlineUrl(Func<string> urlBuilder, string diagTag)
        {
            try
            {
                var text = GetSelection();
                if (string.IsNullOrWhiteSpace(text)) return;
                var url = urlBuilder();
                if (string.IsNullOrEmpty(url)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { DiagLog.Swallowed(diagTag, ex); }
        }

        private static bool ClipboardContainsText()
        {
            try { return Clipboard.ContainsText(); } catch { return false; }
        }

        private bool _bootstrapArmed;
        private bool _bootstrapSent;

        /// <summary>挂上环境引导：第一段主机输出后（且不是密码提示）再发。</summary>
        private void ArmEnvironmentBootstrap()
        {
            _bootstrapArmed = BuildEnvironmentBootstrap().Length > 0;
            _bootstrapSent = false;
        }

        /// <summary>在 OnTerminalOutput 收到首批数据后调用一次。</summary>
        private void TryFireEnvironmentBootstrap(string firstPayload)
        {
            if (!_bootstrapArmed || _bootstrapSent) return;
            var script = BuildEnvironmentBootstrap();
            if (script.Length == 0) { _bootstrapArmed = false; return; }

            // 出现密码/口令提示时不发，等下一次输出（登录完成）再说。
            var peek = firstPayload ?? string.Empty;
            var low = peek.ToLowerInvariant();
            if (low.EndsWith("password:") || low.EndsWith("password: ") || low.EndsWith("密码：")
                || low.EndsWith("passphrase:") || low.EndsWith("verification code:"))
                return;

            _bootstrapSent = true;
            _bootstrapArmed = false;
            try { TrySendInput(script); }        // 原样发送，末尾 \n 保留
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.EnvBootstrap", exSwallowed); } catch { } }
        }

        /// <summary>
        /// 生成登录后的环境引导脚本：locale + TerminalProfile.EnvironmentVariables。
        /// 值按 POSIX 单引号转义；返回空串表示无需发送。
        /// </summary>
        private string BuildEnvironmentBootstrap()
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                var locale = _profile != null ? _profile.Locale : null;
                if (!string.IsNullOrWhiteSpace(locale))
                {
                    var v = ShellQuote(locale.Trim());
                    parts.Add("export LANG=" + v);
                    parts.Add("export LC_ALL=" + v);
                }
                var env = _profile != null ? _profile.EnvironmentVariables : null;
                if (env != null)
                {
                    foreach (var kv in env)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                        var key = kv.Key.Trim();
                        if (!IsShellIdentifier(key)) continue;
                        parts.Add("export " + key + "=" + ShellQuote(kv.Value ?? string.Empty));
                    }
                }
                if (parts.Count == 0) return string.Empty;
                return string.Join("; ", parts) + "\n";
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TerminalControl.EnvBootstrap", ex);
                return string.Empty;
            }
        }

        private static bool IsShellIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        /// <summary>POSIX 单引号转义：' → '\''。</summary>
        private static string ShellQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";
        }

        private void OnCellSendToHost(object sender, byte[] data)
        {
            if (_disposed || data == null || data.Length == 0) return;
            if (_session == null || !_session.IsConnected)
            {
                try { DiagLog.Info("TerminalControl.OnCellSendToHost.Drop",
                    "sessionNull=" + (_session == null) + " connected=" + (_session != null && _session.IsConnected) +
                    " backend=" + ((_session as LocalTerminalSession)?.BackendName ?? "non-local")); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return;
            }
            try
            {
                _session.SendBytes(data);
                try
                {
                    if (_macroRecorder != null && _macroRecorder.IsRecording)
                        _macroRecorder.RecordInput(Encoding.UTF8.GetString(data));
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Macro", exSwallowed); } catch { } }
            }
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

        /// <summary>
        /// 字体可观测性：把实际生效的字体度量写入 diag.log，
        /// 定位“字号过大/行重叠/被 UI 遮挡/跨屏 DPI 漂移”类问题。
        /// 记录：实际字体（含 CJK 补充）、pt/px/DPI、cell 宽高、网格与画布、
        /// 窗口实时 per-monitor DPI（对比 AppliedDpi 发现漂移）、UI 字体对照、遮挡扫描。
        /// </summary>
        private void LogFontMetrics(string path)
        {
            try
            {
                var c = _renderer != null ? _renderer.GetControl() : null;
                int canvasW = 0, canvasH = 0;
                float liveDpi = -1f;
                if (c != null)
                {
                    canvasW = c.ClientSize.Width; canvasH = c.ClientSize.Height;
                    // 窗口所在显示器的实时 DPI：窗口 DC 在 PerMonitorV2 下随所在屏缩放。
                    // 与 ApplyFont 时的系统 DPI 对比即可发现跨屏漂移。
                    // 仅在句柄已就绪时探测——构造期强制建句柄会干扰后续父级挂接。
                    if (c.IsHandleCreated || c.Parent != null)
                    {
                        try
                        {
                            using (var g = c.CreateGraphics()) liveDpi = g.DpiX;
                        }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                    }
                }
                string msg;
                if (_cellRenderer != null)
                {
                    msg = "path=" + path +
                          " font=" + _cellRenderer.AppliedFontName +
                          " size=" + _cellRenderer.AppliedFontSizePt.ToString("0.#") + "pt/" + _cellRenderer.AppliedFontSizePx.ToString("0.#") + "px" +
                          " dpi=" + _cellRenderer.AppliedDpi.ToString("0") +
                          (liveDpi > 0 && Math.Abs(liveDpi - _cellRenderer.AppliedDpi) > 1f
                              ? " LIVE-DPI=" + liveDpi.ToString("0") + "(漂移!字号未重应用)"
                              : "") +
                          " cjk=" + (_cellRenderer.AppliedCjkFontName ?? "-") +
                          " cell=" + _cellRenderer.CharWidth.ToString("0.#") + "x" + _cellRenderer.CharHeight.ToString("0.#") +
                          " grid=" + _cellRenderer.Columns + "x" + _cellRenderer.Rows +
                          " canvas=" + canvasW + "x" + canvasH;
                }
                else
                {
                    msg = "path=" + path +
                          " canvas=" + canvasW + "x" + canvasH +
                          " grid=" + (_renderer != null ? _renderer.Columns + "x" + _renderer.Rows : "?");
                }
                // UI 字体对照：终端 cell 度量 vs 外壳 pt（两套度量不一致是“字号不匹配”常见根因）
                try
                {
                    var ga2 = Gdterm.UI.Program.GlobalAppearance;
                    if (ga2 != null)
                        msg += " uiFont=" + (string.IsNullOrEmpty(ga2.UIFontName) ? "Microsoft YaHei UI" : ga2.UIFontName) + "/" + (ga2.UIFontSize > 0 ? ga2.UIFontSize : 9) + "pt";
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                msg += " " + DescribeOcclusion(c);
                DiagLog.Info("TerminalControl.FontMetrics", msg);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        /// <summary>
        /// 遮挡扫描：从画布向上走最多 4 层父级，报告每层与画布屏幕矩形相交的可见兄弟控件
        /// （搜索栏/状态栏/Dock 冲突等）。front=true 表示该兄弟在画布之上（z 序更靠前，真遮挡候选）。
        /// </summary>
        private static string DescribeOcclusion(Control canvas)
        {
            try
            {
                if (canvas == null) return "occl=?";
                var screen = canvas.RectangleToScreen(canvas.ClientRectangle);
                int hits = 0;
                string detail = "";
                var node = canvas.Parent;
                int depth = 0;
                while (node != null && depth < 4)
                {
                    foreach (Control sib in node.Controls)
                    {
                        if (sib == canvas || !sib.Visible || sib.Width <= 0 || sib.Height <= 0) continue;
                        Rectangle sb2;
                        try { sb2 = sib.RectangleToScreen(sib.ClientRectangle); } catch { continue; }
                        if (!sb2.IntersectsWith(screen)) continue;
                        hits++;
                        if (hits <= 3)
                        {
                            bool front = node.Controls.GetChildIndex(sib, false) < node.Controls.GetChildIndex(canvas, false);
                            detail += " " + sib.GetType().Name + (front ? "[front]" : "[back]") + sb2.Size.ToString();
                        }
                    }
                    node = node.Parent;
                    depth++;
                }
                return "occl=" + hits + detail;
            }
            catch { return "occl=?"; }
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
                // 首次实际布局后补记一次带真实画布尺寸的度量（构造时 canvas=0x0）
                if (!_fontMetricsLoggedWithLayout)
                {
                    _fontMetricsLoggedWithLayout = true;
                    LogFontMetrics("cell-layout");
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            if (_session == null || !_session.IsConnected) return;
            try
            {
                _session.Resize(_cellRenderer.Columns, _cellRenderer.Rows);
            }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.CellResize", ex); }
        }

        /// <summary>
        /// 度量兑底：空闲终端可能始终无输出、永不触发 TerminalResized，导致 cell-layout
        /// 度量永远缺失。首次变为可见且句柄就绪时补记一次（真实画布尺寸）。
        /// </summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            try
            {
                if (Visible && IsHandleCreated && !_fontMetricsLoggedVisible)
                {
                    _fontMetricsLoggedVisible = true;
                    BeginInvoke(new Action(() =>
                    {
                        try { if (!_disposed) LogFontMetrics("cell-visible"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                    }));
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        /// <summary>
        /// OSC 52：远端应用（tmux 的 copy-mode、neovim 的 "+y）请求写剪贴板。
        /// 生效范围仅限本机剪贴板；读取请求在预处理器里已被拒绝。
        /// </summary>
        private void OnCellClipboardSetRequested(object sender, string base64)
        {
            if (string.IsNullOrEmpty(base64)) return;
            try
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch { return; }
                if (bytes.Length == 0 || bytes.Length > 1024 * 1024) return;   // 上限 1MB，防炸
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                if (InvokeRequired) BeginInvoke(new Action(() => { try { Clipboard.SetText(text); } catch { } }));
                else Clipboard.SetText(text);
            }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.Clipboard52", ex); }
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

            if (_cellRenderer == null) return;

            // Ctrl+左键：打开 URL / 本地路径 / 远端路径（同 Xshell、MobaXterm）；命中则不再拖选
            if (e.Button == MouseButtons.Left
                && (ModifierKeys & Keys.Control) != 0
                && (ModifierKeys & Keys.Shift) == 0)
            {
                try { if (TryOpenLinkAt(e.X, e.Y)) return; }
                catch (Exception ex) { DiagLog.Swallowed("TerminalControl.CtrlClick", ex); }
            }

            // 左键：若应用未启用鼠标上报，则走本地拖选（SecureCRT 习惯）
            bool mouseTracking = _cellRenderer.IsMouseTrackingEnabled;
            if (e.Button == MouseButtons.Left && !mouseTracking)
            {
                try { _cellRenderer.ClearSelection(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } } // 新选区从空开始
                _selecting = true;
                try { _cellRenderer.BeginSelection(e.X, e.Y); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return;
            }
            if (e.Button == MouseButtons.Left && mouseTracking && (ModifierKeys & Keys.Shift) != 0)
            {
                // Shift+左键在鼠标应用里仍允许本地拖选（强制选择）
                _selecting = true;
                try { _cellRenderer.BeginSelection(e.X, e.Y); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return;
            }

            if (_session?.IsConnected != true) return;
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
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        /// <summary>
        /// 滚轮：
        ///   - 应用开了鼠标上报且没按 Shift（tmux/vim 的 mouse on）→ 作为 button 64/65 送给远端；
        ///   - 否则（含 Shift+滚轮）→ 滚本地回看缓冲，3 行/格。
        /// 之前整个 UI 没有 MouseWheel 订阅，滚轮在任何渲染器下都是死的。
        /// </summary>
        private void OnCellMouseWheel(object sender, MouseEventArgs e)
        {
            if (_cellRenderer == null || e.Delta == 0) return;
            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bool shift = (ModifierKeys & Keys.Shift) != 0;

            bool tracking = false;
            try { tracking = _cellRenderer.IsMouseTrackingEnabled; }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }

            if (tracking && !shift && _session?.IsConnected == true)
            {
                int col, row;
                if (!_cellRenderer.TryHitTest(e.X, e.Y, out col, out row)) return;
                // 滚轮一格 = 3 行，和 Windows Terminal 一致
                for (int i = 0; i < 3; i++)
                {
                    try { _cellRenderer.MouseWheel(col, row, e.Delta > 0 ? 0 : 1, ctrl, shift); }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                }
                return;
            }

            int lines = (e.Delta > 0 ? -3 : 3);
            try { _cellRenderer.ScrollViewport(lines); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        private void OnCellMouseUp(object sender, MouseEventArgs e)
        {
            if (_cellRenderer == null) return;

            // 拖选释放：选区已保留，不自动复制（右键复制 / Ctrl+C 复制）
            if (_selecting)
            {
                _selecting = false;
                try { _cellRenderer.ExtendSelection(e.X, e.Y); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return;
            }

            if (!_mouseDown) return;
            _mouseDown = false;
            int col, row;
            if (!_cellRenderer.TryHitTest(e.X, e.Y, out col, out row)) return;
            try
            {
                _cellRenderer.MouseRelease(col, row,
                    (ModifierKeys & Keys.Control) != 0,
                    (ModifierKeys & Keys.Shift) != 0);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        private void OnCellMouseMove(object sender, MouseEventArgs e)
        {
            if (_cellRenderer == null) return;
            UpdateLinkCursor(e.X, e.Y);
            // 本地拖选扩展
            if (_selecting)
            {
                try { _cellRenderer.ExtendSelection(e.X, e.Y); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return;
            }
            if (!_mouseDown) return;
            int col, row;
            if (!_cellRenderer.TryHitTest(e.X, e.Y, out col, out row)) return;
            try
            {
                _cellRenderer.MouseMove(col, row, _mouseButton,
                    (ModifierKeys & Keys.Control) != 0,
                    (ModifierKeys & Keys.Shift) != 0);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }

                if (session is LocalTerminalSession)
                {
                    try
                    {
                        _renderer?.Write("\r\n\x1b[32m[本地终端] 可输入命令；输入 exit 退出\x1b[0m\r\n");
                    }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                }
            }
            catch (Exception ex)
            {
                try { _renderer?.Write("\r\n\x1b[31m本地终端启动失败: " + ex.Message + "\x1b[0m\r\n"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                DiagLog.Swallowed("TerminalControl.AttachLocal", ex);
            }
        }

public async void Connect()
        {
            try { await ConnectAsyncCore().ConfigureAwait(true); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        public Task ConnectAsyncIfNeeded()
        {
            if (_disposed) return Task.FromResult(false).ContinueWith(_ => { });
            if (_session != null && _session.IsConnected) return Task.CompletedTask;
            if (_connectTask != null && !_connectTask.IsCompleted)
            {
                try { DiagLog.Info("TerminalControl.ConnectAsyncIfNeeded",
                    "skip: connect already running id=" + (_config != null ? _config.Id : "")); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return _connectTask;
            }
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
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }

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
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                else if (_config.Protocol == ProtocolType.Telnet)
                {
                    // Telnet 明文：仅直连/跳板转发入口；凭据仅取 Username（交换机 login 仍由设备交互式提示）。
                    if (_terminalFactory == null)
                        throw new InvalidOperationException("ITerminalSessionFactory 未注入，无法创建 Telnet 会话");
                    session = _terminalFactory.CreateTelnet();
                    bool needTunnelTelnet = _tunnelManager != null && (
                        (_config.JumpChain != null && _config.JumpChain.Hops != null && _config.JumpChain.Hops.Count > 0)
                        || _config.Tunnel != null);
                    if (needTunnelTelnet)
                    {
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
                    try { session.Dispose(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                    return;
                }

                _session = session;
                _session.OutputReceived += OnTerminalOutput;
                _session.Disconnected += OnSessionDisconnected;

                // 连接后同步一次尺寸（cell 路径）
                if (_cellRenderer != null)
                {
                    try { _session.Resize(_cellRenderer.Columns, _cellRenderer.Rows); }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                }

                // locale / 环境变量：SSH 的 env 请求要服务端 AcceptEnv 才生效（默认几乎都不开），
                // 所以在 shell 起来后直接 export 一次。Locale 为空且没有自定义变量时一个字节都不发。
                // 时机：等主机第一段输出（登录横幅/提示符）再发，避免在 sshd 还没 fork shell 时把字节
                // 塞给可能出现的二次认证提示（“Password:”会把我们的 export 当成密码收走）。
                ArmEnvironmentBootstrap();

                if (_profile.AutoRunCommands != null)
                {
                    void RunAuto()
                    {
                        foreach (var cmd in _profile.AutoRunCommands)
                        {
                            if (string.IsNullOrWhiteSpace(cmd)) continue;
                            // 危险命令闸门走 isCommandLine，但该模式会 TrimEnd 掉末尾换行——
                            // 原来把 "\n" 拼在同一个字符串里，等于把回车吃掉，命令根本没执行过。
                            if (TrySendInput(cmd, isCommandLine: true))
                                SafeSend(_profile.NewLineSequence ?? "\n");
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
                try { SessionDisconnected?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
            try { _autoLogger.StartRecording(_config?.Host ?? "session", _config?.Name); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                ConnectAsyncIfNeeded();
            }
            else if (_session != null && !_session.IsConnected && _session is LocalTerminalSession local)
            {
                // 本地终端已 Attach 但进程未起
                try { DiagLog.Info("TerminalControl.ResumeRendering",
                    "local-start id=" + (_config != null ? _config.Id : "")); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                try { local.ConnectLocal(); }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("TerminalControl.ResumeRendering.Local", ex);
                    try { _renderer?.Write("\r\n\x1b[31m本地终端启动失败: " + ex.Message + "\x1b[0m\r\n"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                }
            }
            else
            {
                // 分支未命中的原因必须可见：否则无法区分“已连上”与“状态卡死”
                try
                {
                    DiagLog.Info("TerminalControl.ResumeRendering",
                        "skip id=" + (_config != null ? _config.Id : "") +
                        " sessionNull=" + (_session == null) +
                        " connecting=" + _connecting +
                        " connected=" + (_session != null && _session.IsConnected));
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            }
        }

        /// <summary>
        /// 粘贴：应用开了 bracketed paste（?2004，vim/tmux/bash 5 默认会开）时用
        /// ESC[200~ ... ESC[201~ 包住，否则多行粘贴会被远端当成逐行回车直接执行。
        /// 单行/短文本仍按原路发送，保持老行为。
        /// </summary>
        public bool PasteFromClipboard()
        {
            string text = null;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.PasteRead", ex); }
            if (string.IsNullOrEmpty(text)) return false;
            if (!IsConnected) return false;

            bool bracketed = false;
            try { bracketed = _cellRenderer != null && _cellRenderer.IsBracketedPasteEnabled; }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }

            if (!bracketed) return TrySendInput(text);

            // 括号粘贴内部：换行统一成 \r（远端自己决定要不要执行），去掉 NUL
            var body = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r").Replace("\0", "");
            return TrySendInput("\u001b[200~" + body + "\u001b[201~");
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
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                // cell/TUI 路径：字符逐键直通远端（TryKeyPressed→SendToHost），不能用本地行缓冲——
                // 否则 Enter 会把 _commandLine 整行 SafeSend 重发一遍，主机收到 ls+ls\r，回显成 lsls/clearclear。
                if (_cellRenderer != null) return false;
                // 本地 shell（LightweightRenderer）：始终本地回显（重定向进程 echo 不可靠）
                if (_session is LocalTerminalSession) return true;
                // SSH + LightweightRenderer：仅危险命令拦截时本地缓冲
                return _dangerousDetector != null;
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
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                try
                {
                    MessageBox.Show(
                        FindForm(),
                        "危险命令检测失败，已阻止发送该命令。\n" + ex.Message,
                        "安全拦截",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                    return false;
                }
                if (dlg.RememberChoice)
                {
                    try { _dangerousDetector.AddToWhitelist(command); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                try { _renderer?.Write(erase.ToString()); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            }
            _commandLine.Clear();
        }

        private bool SafeSend(string text)
        {
            try
            {
                _session.SendInput(text);
                try { if (_macroRecorder != null && _macroRecorder.IsRecording) _macroRecorder.RecordInput(text); }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Macro", exSwallowed); } catch { } }
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

        /// <summary>Zmodem 接收：选保存目录 → session 开始接收（仅 SSH 会话；串口/本地提示不支持）。</summary>
        public void RequestZmodemReceive()
        {
            try
            {
                if (_session == null || !_session.IsConnected)
                {
                    System.Windows.Forms.MessageBox.Show(FindForm(), "请先连接终端。", "Zmodem 接收",
                        System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                    return;
                }
                if (_session.IsZmodemReceiving)
                {
                    System.Windows.Forms.MessageBox.Show(FindForm(), "已有接收在进行中。", "Zmodem 接收",
                        System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                    return;
                }
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择 Zmodem 接收保存目录" })
                {
                    if (dlg.ShowDialog(FindForm()) != System.Windows.Forms.DialogResult.OK) return;
                    try
                    {
                        _session.StartZmodemReceive(dlg.SelectedPath);
                    }
                    catch (NotSupportedException nse)
                    {
                        System.Windows.Forms.MessageBox.Show(FindForm(), nse.Message, "Zmodem 接收",
                            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                    }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        /// <summary>宏录制是否进行中（本标签页）。</summary>
        public bool IsMacroRecording { get { return _macroRecorder != null && _macroRecorder.IsRecording; } }

        /// <summary>当前录制步数（未录制返回 0）。</summary>
        public int MacroStepCount { get { return _macroRecorder != null ? _macroRecorder.StepCount : 0; } }

        /// <summary>当前录制时长（未录制返回 Zero）。</summary>
        public TimeSpan MacroDuration { get { return _macroRecorder != null ? _macroRecorder.Duration : TimeSpan.Zero; } }

        /// <summary>开始宏录制：旁录本标签页后续全部发送动作，正常发送不受影响。</summary>
        public void StartMacroRecording()
        {
            if (_macroRecorder == null) _macroRecorder = new MacroRecorder();
            _macroRecorder.StartRecording();
        }

        /// <summary>停止宏录制，返回录制器（可回放/保存；无录制返回 null）。</summary>
        public MacroRecorder StopMacroRecording()
        {
            if (_macroRecorder == null || !_macroRecorder.IsRecording) return null;
            _macroRecorder.StopRecording();
            return _macroRecorder;
        }

        /// <summary>确保补全器已按当前 store 构建（懒初始化）。</summary>
        private void EnsureCompletion()
        {
            try
            {
                if (_completion != null) return;
                var hs = HistoryStore;
                var qs = QuickStore;
                _completion = new TerminalCompletion(
                    hs != null ? (Func<System.Collections.Generic.IList<string>>)(() => hs.GetRecentCommands(50)) : null,
                    qs != null ? (Func<System.Collections.Generic.IList<string>>)(() =>
                    {
                        var list = new System.Collections.Generic.List<string>();
                        try
                        {
                            var all = qs.LoadAll();
                            if (all != null) foreach (var q in all)
                            {
                                if (q != null && !string.IsNullOrWhiteSpace(q.Command)) list.Add(q.Command);
                            }
                        }
                        catch { }
                        return list;
                    }) : null);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        /// <summary>按键后刷新补全弹窗（词长≥2 且有候选则显示，否则隐藏）。canvas 为渲染器画布。</summary>
        public void UpdateCompletionPopup()
        {
            try
            {
                EnsureCompletion();
                if (_completion == null) { HideCompletionPopup(); return; }
                string prefix;
                var cands = _completion.Complete(_commandLine.ToString(), out prefix);
                if (cands == null || cands.Count == 0 || string.IsNullOrEmpty(prefix) || prefix.Length < 2)
                {
                    HideCompletionPopup();
                    return;
                }
                if (_popup == null) _popup = new TerminalCompletionPopup();
                var canvas = _renderer != null ? _renderer.GetControl() : null;
                if (canvas == null && _cellRenderer != null) canvas = _cellRenderer.GetControl();
                if (canvas == null) return;
                _popup.ShowPopup(canvas, cands);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        public void HideCompletionPopup()
        {
            try { if (_popup != null) _popup.HidePopup(); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        public bool IsCompletionPopupShown
        {
            get
            {
                try { return _popup != null && _popup.IsShown; }
                catch { return false; }
            }
        }

        public void CompletionPopupMove(int delta)
        {
            try { if (_popup != null) _popup.MoveSelection(delta); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        /// <summary>确认弹窗选中项：把剩余后缀写入行缓冲 + 回显/直发。返回 false=无选中。</summary>
        public bool ConfirmCompletionPopup()
        {
            try
            {
                if (_popup == null || !_popup.IsShown) return false;
                string selected = _popup.SelectedCandidate;
                HideCompletionPopup();
                if (string.IsNullOrEmpty(selected)) return false;
                string prefix = TerminalCompletion.LastWord(_commandLine.ToString());
                if (string.IsNullOrEmpty(prefix) || !selected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
                string rest = selected.Substring(prefix.Length);
                if (string.IsNullOrEmpty(rest)) return true;
                _commandLine.Append(rest);
                if (_cellRenderer == null)
                {
                    try { _renderer?.Write(rest); }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                }
                else
                {
                    SafeSend(rest);
                }
                return true;
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            return false;
        }

        /// <summary>Tab 补全：有候选则补首个（回显擦写），无则返回 false 由调用方直通远端。
        /// 双渲染器通用（_commandLine 两边都攒）。本地回显擦写：退格擦 prefix 再写全词。</summary>
        public bool TryCompleteOnTab()
        {
            try
            {
                EnsureCompletion();
                if (_completion == null) return false;
                string line = _commandLine.ToString();
                string prefix;
                var cands = _completion.Complete(line, out prefix);
                if (cands == null || cands.Count == 0 || string.IsNullOrEmpty(prefix)) return false;
                string top = cands[0];
                string rest = top.Substring(prefix.Length);
                if (string.IsNullOrEmpty(rest)) return false;
                // 本地行缓冲追加 + 回显
                _commandLine.Append(rest);
                if (_cellRenderer == null)
                {
                    try { _renderer?.Write(rest); }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                }
                else
                {
                    // cell 路径字符已直通远端：补全后缀直发（远端行编辑器自行回显）
                    SafeSend(rest);
                }
                return true;
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            return false;
        }

        /// <summary>下发关键词高亮规则到渲染器（Lightweight 生效；CellGdi 暂不支持）。
        /// 规则来源：HighlightStore.Load().Rules。</summary>
        /// <summary>查找跳行（Cell 视口有效；Lightweight 当前行永远可见，返回 0 表示已可见）。
        /// 返回屏内行号，-1=未命中。</summary>
        public int ScrollToMatch(string text, bool searchDown)
        {
            try
            {
                if (_cellRenderer != null) return _cellRenderer.ScrollToMatch(text, searchDown);
                return 0;
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
            return -1;
        }

        public void SetHighlightRules(System.Collections.Generic.List<Gdterm.Core.Models.HighlightRule> rules)
        {
            try
            {
                var light = _renderer as Gdterm.Terminal.Rendering.LightweightRenderer;
                if (light != null) light.SetHighlightRules(rules);
                if (_cellRenderer != null) _cellRenderer.SetHighlightRules(rules);
                // P1-7：同一份规则里的「动作」在这里变成触发器
                SetTriggerRules(rules);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
        }

        private void OnTerminalOutput(object sender, TerminalOutputEventArgs e)
        {
            if (_disposed || e == null || string.IsNullOrEmpty(e.Text)) return;

            // 暂停标签：仍喂引擎以保持 TUI/alt-screen 状态，但不进 UI 消息泵、不重绘
            // （CellGdiRenderer.Write 在 Pause 时只 Feed，不启动 timer）
            if (_isPaused)
            {
                try { _renderer?.Write(e.Text); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                try { EvaluateTriggers(e.Text); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Triggers", exSwallowed); } catch { } }
                try { _autoLogger?.LogOutput(e.Text); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return;
            }

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnTerminalOutput(sender, e))); }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                return;
            }

            // 首段输出到手 → 环境引导（locale / 自定义变量）可以发了
            try { TryFireEnvironmentBootstrap(e.Text); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.EnvBootstrap", exSwallowed); } catch { } }

            _renderer?.Write(e.Text);
            // P1-7：触发器只看输出流，与渲染无关
            try { EvaluateTriggers(e.Text); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Triggers", exSwallowed); } catch { } }
            try { _autoLogger?.LogOutput(e.Text); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                        try { _cellRenderer.SendToHost -= OnCellSendToHost; } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                        try { _cellRenderer.ClipboardSetRequested -= OnCellClipboardSetRequested; } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
                        try { _cellRenderer.TerminalResized -= OnCellTerminalResized; } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
                        try { SessionDisconnected?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl", exSwallowed); } catch { } }
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
