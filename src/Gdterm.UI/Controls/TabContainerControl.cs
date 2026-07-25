using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.Logging;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Connections;
using Gdterm.Tunnel;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 标签页容器——支持 KeePass 自动凭据填充（含文件夹级继承）、关闭按钮、懒加载、暂停/恢复渲染
    /// </summary>
    public class TabContainerControl : UserControl
    {
        private readonly TunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IAiAssistantService _aiService;
        private readonly IAuditLogger _auditLogger;
        private readonly IKeePassService _keepassService;
        private readonly IFolderCredentialStore _folderCredStore;
        private readonly Dictionary<TabPage, TabSession> _sessions = new Dictionary<TabPage, TabSession>();
        private TabControl _tabControl;

        public TabContainerControl(
            TunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            ISftpServiceFactory sftpFactory,
            IAiAssistantService aiService,
            IAuditLogger auditLogger,
            IKeePassService keepassService,
            IFolderCredentialStore folderCredStore)
        {
            _tunnelManager = tunnelManager;
            _terminalFactory = terminalFactory;
            _sftpFactory = sftpFactory;
            _aiService = aiService;
            _auditLogger = auditLogger;
            _keepassService = keepassService;
            _folderCredStore = folderCredStore;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(120, 24),
                Padding = new Point(10, 3)
            };
            _tabControl.DrawItem += OnDrawTab;
            _tabControl.MouseDown += OnTabMouseDown;
            _tabControl.SelectedIndexChanged += OnTabSelectedIndexChanged;
            Controls.Add(_tabControl);
        }

        /// <summary>
        /// 打开连接标签页（自动从 KeePass 获取凭据）
        /// </summary>
        public void OpenConnection(ConnectionConfig config)
        {
            if (config == null) return;

            // 检查是否已打开相同连接
            foreach (TabPage existingTab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(existingTab, out var session) &&
                    session.Config?.Id == config.Id)
                {
                    _tabControl.SelectedTab = existingTab;
                    return;
                }
            }

            // 自动从 KeePass 获取凭据
            CredentialPayload credential = null;
            if (config.Protocol == ProtocolType.SSH || config.Protocol == ProtocolType.RDP)
            {
                credential = ResolveCredential(config);
            }

            TabPage tab;

            switch (config.Protocol)
            {
                case ProtocolType.SSH:
                    tab = CreateSshTerminalTab(config, credential);
                    break;
                case ProtocolType.RDP:
                    tab = CreateRdpTab(config, credential);
                    break;
                case ProtocolType.Serial:
                    tab = CreateSerialTab(config);
                    break;
                default:
                    MessageBox.Show($"不支持的协议: {config.Protocol}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
            }

            _tabControl.TabPages.Add(tab);
            _tabControl.SelectedTab = tab;

            _auditLogger.LogConnection(config.Id, config.Name, config.Host, true);
        }

        /// <summary>
        /// 从 KeePass 解析连接凭据
        /// 前提：SecurityManager 已解锁 → KeePass 已同步解锁
        /// 流程：CredentialRefId 精确查找 > FindEntryByConnection 智能匹配
        /// 如果 KeePass 未解锁（异常情况），返回 null 回退到手动输入
        /// </summary>
        private CredentialPayload ResolveCredential(ConnectionConfig config)
        {
            try
            {
                // KeePass 应该已通过主密码解锁，如果未解锁则跳过
                if (!_keepassService.IsUnlocked)
                    return null;

                KeePassEntry entry = null;

                // 策略1：通过 CredentialRefId 精确查找
                if (!string.IsNullOrEmpty(config.CredentialRefId))
                {
                    try
                    {
                        entry = _keepassService.GetCredential(config.CredentialRefId) != null
                            ? GetKeePassEntry(config.CredentialRefId)
                            : null;
                    }
                    catch { /* 条目不存在或已删除 */ }
                }

                // 策略2：文件夹级凭据继承（沿 GroupPath 向上逐级查找）
                if (entry == null && _folderCredStore != null && !string.IsNullOrEmpty(config.GroupPath))
                {
                    try
                    {
                        var inheritedRefId = _folderCredStore.ResolveByInheritance(config.GroupPath);
                        if (!string.IsNullOrEmpty(inheritedRefId))
                        {
                            entry = _keepassService.GetCredential(inheritedRefId) != null
                                ? GetKeePassEntry(inheritedRefId)
                                : null;
                        }
                    }
                    catch { /* 继承链查找失败，继续下一策略 */ }
                }

                // 策略3：智能匹配（host:port > 标题 > 用户名）
                if (entry == null)
                {
                    entry = _keepassService.FindEntryByConnection(config);
                }

                if (entry == null) return null;

                // 构建凭据
                var credential = new CredentialPayload
                {
                    Username = !string.IsNullOrEmpty(entry.Username) ? entry.Username : config.Username,
                    Password = entry.Password ?? ""
                };

                // SSH 密钥认证
                if (config.Protocol == ProtocolType.SSH && entry.SshPrivateKeyData != null)
                {
                    credential.SshPrivateKey = entry.SshPrivateKeyData;
                    credential.SshPrivateKeyPassphrase = entry.SshPrivateKeyPassphrase;
                }

                return credential;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 通过 ID 获取完整 KeePass 条目
        /// </summary>
        private KeePassEntry GetKeePassEntry(string entryId)
        {
            // KeePassService.GetCredential 只返回 CredentialPayload，
            // 我们需要完整条目来获取 SSH 密钥等。
            // 通过 ListEntries 遍历找到条目（已有性能优化：单次加载）
            var entries = _keepassService.ListEntries();
            foreach (var summary in entries)
            {
                if (summary.Id == entryId)
                {
                    // 获取完整凭据
                    var cred = _keepassService.GetCredential(entryId);
                    return new KeePassEntry
                    {
                        Id = summary.Id,
                        Title = summary.Title,
                        Username = cred.Username,
                        Password = cred.Password,
                        SshPrivateKeyData = _keepassService.GetSshPrivateKey(entryId),
                        SshPrivateKeyPassphrase = _keepassService.GetSshPrivateKeyPassphrase(entryId)
                    };
                }
            }
            return null;
        }

        private TabPage CreateSshTerminalTab(ConnectionConfig config, CredentialPayload credential)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = $"{config.Username}@{config.Host}:{config.Port}"
            };

            var terminalControl = new TerminalControl(config, _terminalFactory, _tunnelManager, _auditLogger);
            terminalControl.Dock = DockStyle.Fill;
            // 注入 KeePass 凭据
            terminalControl.Credentials = credential;
            tab.Controls.Add(terminalControl);

            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = terminalControl,
                Protocol = ProtocolType.SSH,
                IsConnected = false
            };

            return tab;
        }

        private TabPage CreateRdpTab(ConnectionConfig config, CredentialPayload credential)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = $"RDP: {config.Host}:{config.Port}"
            };

            // 如果有凭据，注入 RDP 凭据到 Windows 凭据管理器
            if (credential != null && !string.IsNullOrEmpty(credential.Password))
            {
                try
                {
                    _keepassService.InjectRdpCredential(
                        config.Host, credential.Username, credential.Password);
                }
                catch { /* best-effort */ }
            }

            var label = new Label
            {
                Text = $"RDP 连接: {config.Host}\n" +
                       (credential != null ? "(凭据已自动填充)" : "(无关联密码条目)") +
                       "\n\n（RDP 标签页待实现）",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            tab.Controls.Add(label);

            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = label,
                Protocol = ProtocolType.RDP,
                IsConnected = false,
                Credential = credential
            };

            return tab;
        }

        private TabPage CreateSerialTab(ConnectionConfig config)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = $"Serial: {config.Serial?.PortName ?? "Unknown"}"
            };

            var label = new Label
            {
                Text = $"串口连接: {config.Serial?.PortName}\n\n（串口标签页待实现）",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            tab.Controls.Add(label);

            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = label,
                Protocol = ProtocolType.Serial,
                IsConnected = false
            };

            return tab;
        }

        // ===== 分屏 =====

        public void SplitHorizontal() => SplitCurrentTab("horizontal");
        public void SplitVertical() => SplitCurrentTab("vertical");

        private void SplitCurrentTab(string direction)
        {
            var selectedTab = _tabControl.SelectedTab;
            if (selectedTab == null || !_sessions.ContainsKey(selectedTab))
            {
                MessageBox.Show("请先打开一个连接", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var session = _sessions[selectedTab];
            var currentControl = session.Control;

            var newTerminal = new TerminalControl(session.Config, _terminalFactory, _tunnelManager, _auditLogger);
            newTerminal.Dock = DockStyle.Fill;

            var splitPane = direction == "horizontal"
                ? SplitPaneControl.CreateHorizontal(currentControl, newTerminal, 0.5)
                : SplitPaneControl.CreateVertical(currentControl, newTerminal, 0.5);
            splitPane.Dock = DockStyle.Fill;

            selectedTab.Controls.Clear();
            selectedTab.Controls.Add(splitPane);
        }

        // ===== 标签管理 =====

        public void CloseAllTabs()
        {
            foreach (TabPage tab in _tabControl.TabPages)
                CloseTab(tab);
            _tabControl.TabPages.Clear();
            _sessions.Clear();
        }

        private void CloseTab(TabPage tab)
        {
            if (_sessions.TryGetValue(tab, out var session))
            {
                // RDP 连接关闭时清理凭据
                if (session.Protocol == ProtocolType.RDP)
                {
                    try { _keepassService.CleanupRdpCredential(session.Config.Host); }
                    catch { }
                }

                if (session.Control is IDisposable disposable)
                    disposable.Dispose();

                _sessions.Remove(tab);
            }
        }

        private void OnDrawTab(object sender, DrawItemEventArgs e)
        {
            var tab = _tabControl.TabPages[e.Index];
            var rect = e.Bounds;

            bool isSelected = (e.Index == _tabControl.SelectedIndex);
            using (var brush = new SolidBrush(isSelected ? SystemColors.ControlLight : SystemColors.Control))
                e.Graphics.FillRectangle(brush, rect);

            var textRect = new Rectangle(rect.X + 4, rect.Y + 2, rect.Width - 24, rect.Height - 4);
            TextRenderer.DrawText(e.Graphics, tab.Text, e.Font, textRect, SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var closeRect = new Rectangle(rect.Right - 18, rect.Y + 4, 14, 16);
            using (var brush = new SolidBrush(Color.DarkGray))
                e.Graphics.DrawString("×", e.Font, brush, closeRect);

            using (var pen = new Pen(SystemColors.ControlDark))
                e.Graphics.DrawRectangle(pen, rect);
        }

        private void OnTabMouseDown(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabControl.TabPages.Count; i++)
            {
                var rect = _tabControl.GetTabRect(i);
                var closeRect = new Rectangle(rect.Right - 18, rect.Y + 4, 14, 16);

                if (closeRect.Contains(e.Location))
                {
                    CloseTab(_tabControl.TabPages[i]);
                    _tabControl.TabPages.RemoveAt(i);
                    break;
                }
            }
        }

        private void OnTabSelectedIndexChanged(object sender, EventArgs e)
        {
            foreach (var kvp in _sessions)
            {
                if (kvp.Value.Control is TerminalControl tc)
                {
                    if (kvp.Key == _tabControl.SelectedTab)
                        tc.ResumeRendering();
                    else
                        tc.PauseRendering();
                }
            }
        }

        // ====== 重连 ======

        /// <summary>
        /// 关闭当前活跃标签页
        /// </summary>
        public void CloseActiveTab()
        {
            if (_tabControl.SelectedTab != null)
                CloseTab(_tabControl.SelectedTab);
        }

        /// <summary>
        /// 重连当前活跃标签页（先断开再重新连接）
        /// </summary>
        public void ReconnectActiveTab()
        {
            if (_tabControl.SelectedTab == null) return;
            if (!_sessions.TryGetValue(_tabControl.SelectedTab, out var session)) return;

            var config = session.Config;
            var cred = session.Credential;

            // 关闭当前连接
            CloseTab(_tabControl.SelectedTab);

            // 重新打开
            if (config != null)
            {
                var newTab = OpenConnection(config);
                if (newTab != null && cred != null)
                {
                    // 重新注入凭证
                    if (_sessions.TryGetValue(newTab, out var newSession))
                        newSession.Credential = cred;
                }
            }
        }

        /// <summary>
        /// 重连指定连接（按 ConnectionId）
        /// </summary>
        public void ReconnectById(string connectionId)
        {
            foreach (TabPage tab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(tab, out var session) &&
                    session.Config?.Id == connectionId)
                {
                    CloseTab(tab);
                    break;
                }
            }
            var config = _connectionStore.GetById(connectionId);
            if (config != null) OpenConnection(config);
        }

        // ====== 会话状态查询/恢复 ======

        /// <summary>
        /// 当前活跃标签页索引（-1 表示无标签页）
        /// </summary>
        public int ActiveTabIndex
        {
            get { return _tabControl.SelectedIndex; }
        }

        /// <summary>
        /// 获取所有打开标签页的状态信息（用于保存会话）
        /// </summary>
        public List<OpenTabState> GetOpenTabStates()
        {
            var result = new List<OpenTabState>();
            foreach (TabPage tab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(tab, out var session) && session.Config != null)
                {
                    result.Add(new OpenTabState
                    {
                        ConnectionId = session.Config.Id,
                        Title = session.Config.Name,
                        Protocol = session.Protocol.ToString(),
                        Host = session.Config.Host,
                        IsActive = (tab == _tabControl.SelectedTab)
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// 设置活跃标签页（恢复会话用，越界忽略）
        /// </summary>
        public void SetActiveTabIndex(int index)
        {
            if (index >= 0 && index < _tabControl.TabCount)
                _tabControl.SelectedIndex = index;
        }

        private class TabSession
        {
            public ConnectionConfig Config { get; set; }
            public Control Control { get; set; }
            public ProtocolType Protocol { get; set; }
            public bool IsConnected { get; set; }
            public CredentialPayload Credential { get; set; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseAllTabs();
            base.Dispose(disposing);
        }
    }
}
