using System;
using System.Collections.Generic;
using System.Drawing;
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
    /// 标签页容器——支持关闭按钮、懒加载、非活动标签暂停渲染
    /// </summary>
    public class TabContainerControl : UserControl
    {
        private readonly TunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IAiAssistantService _aiService;
        private readonly IAuditLogger _auditLogger;
        private readonly Dictionary<TabPage, TabSession> _sessions = new Dictionary<TabPage, TabSession>();
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
        /// 打开连接标签页
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

            TabPage tab;

            switch (config.Protocol)
            {
                case ProtocolType.SSH:
                    tab = CreateSshTerminalTab(config);
                    break;
                case ProtocolType.RDP:
                    tab = CreateRdpTab(config);
                    break;
                case ProtocolType.Serial:
                    tab = CreateSerialTab(config);
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
        /// 水平分割当前标签页（左右）
        /// </summary>
        public void SplitHorizontal()
        {
            var selectedTab = _tabControl.SelectedTab;
            if (selectedTab == null || !_sessions.ContainsKey(selectedTab))
            {
                MessageBox.Show("请先打开一个连接", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var session = _sessions[selectedTab];
            var currentControl = session.Control;

            // 创建新的终端控件
            var newTerminal = new TerminalControl(session.Config, _terminalFactory, _tunnelManager, _auditLogger);
            newTerminal.Dock = DockStyle.Fill;

            // 创建水平分割
            var splitPane = SplitPaneControl.CreateHorizontal(currentControl, newTerminal, 0.5);
            splitPane.Dock = DockStyle.Fill;

            // 替换标签页内容
            selectedTab.Controls.Clear();
            selectedTab.Controls.Add(splitPane);
        }

        /// <summary>
        /// 垂直分割当前标签页（上下）
        /// </summary>
        public void SplitVertical()
        {
            var selectedTab = _tabControl.SelectedTab;
            if (selectedTab == null || !_sessions.ContainsKey(selectedTab))
            {
                MessageBox.Show("请先打开一个连接", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var session = _sessions[selectedTab];
            var currentControl = session.Control;

            // 创建新的终端控件
            var newTerminal = new TerminalControl(session.Config, _terminalFactory, _tunnelManager, _auditLogger);
            newTerminal.Dock = DockStyle.Fill;

            // 创建垂直分割
            var splitPane = SplitPaneControl.CreateVertical(currentControl, newTerminal, 0.5);
            splitPane.Dock = DockStyle.Fill;

            // 替换标签页内容
            selectedTab.Controls.Clear();
            selectedTab.Controls.Add(splitPane);
        }

        /// <summary>
        /// 关闭所有标签页
        /// </summary>
        public void CloseAllTabs()
        {
            foreach (TabPage tab in _tabControl.TabPages)
            {
                CloseTab(tab);
            }
            _tabControl.TabPages.Clear();
            _sessions.Clear();
        }

        private TabPage CreateSshTerminalTab(ConnectionConfig config)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = $"{config.Username}@{config.Host}:{config.Port}"
            };

            // 创建终端控件（延迟连接：只创建控件，不立即连接）
            var terminalControl = new TerminalControl(config, _terminalFactory, _tunnelManager, _auditLogger);
            terminalControl.Dock = DockStyle.Fill;
            tab.Controls.Add(terminalControl);

            // 记录会话信息
            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = terminalControl,
                Protocol = ProtocolType.SSH,
                IsConnected = false
            };

            return tab;
        }

        private TabPage CreateRdpTab(ConnectionConfig config)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = $"RDP: {config.Host}:{config.Port}"
            };

            var label = new Label
            {
                Text = $"RDP 连接: {config.Host}\n\n（RDP 标签页待实现）",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            tab.Controls.Add(label);

            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = label,
                Protocol = ProtocolType.RDP,
                IsConnected = false
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

        private void OnDrawTab(object sender, DrawItemEventArgs e)
        {
            var tab = _tabControl.TabPages[e.Index];
            var rect = e.Bounds;

            // 绘制标签背景
            bool isSelected = (e.Index == _tabControl.SelectedIndex);
            using (var brush = new SolidBrush(isSelected ? SystemColors.ControlLight : SystemColors.Control))
            {
                e.Graphics.FillRectangle(brush, rect);
            }

            // 绘制标签文本
            var textRect = new Rectangle(rect.X + 4, rect.Y + 2, rect.Width - 24, rect.Height - 4);
            TextRenderer.DrawText(e.Graphics, tab.Text, e.Font, textRect, SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // 绘制关闭按钮 "×"
            var closeRect = new Rectangle(rect.Right - 18, rect.Y + 4, 14, 16);
            using (var brush = new SolidBrush(Color.DarkGray))
            {
                e.Graphics.DrawString("×", e.Font, brush, closeRect);
            }

            // 绘制边框
            using (var pen = new Pen(SystemColors.ControlDark))
            {
                e.Graphics.DrawRectangle(pen, rect);
            }
        }

        private void OnTabMouseDown(object sender, MouseEventArgs e)
        {
            // 检测点击关闭按钮
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
            // 暂停所有非活动标签，恢复活动标签
            foreach (var kvp in _sessions)
            {
                if (kvp.Value.Control is TerminalControl tc)
                {
                    if (kvp.Key == _tabControl.SelectedTab)
                    {
                        tc.ResumeRendering();
                    }
                    else
                    {
                        tc.PauseRendering();
                    }
                }
            }
        }

        private void CloseTab(TabPage tab)
        {
            if (_sessions.TryGetValue(tab, out var session))
            {
                if (session.Control is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                _sessions.Remove(tab);
            }
        }

        /// <summary>
        /// 标签页会话信息
        /// </summary>
        private class TabSession
        {
            public ConnectionConfig Config { get; set; }
            public Control Control { get; set; }
            public ProtocolType Protocol { get; set; }
            public bool IsConnected { get; set; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseAllTabs();
            }
            base.Dispose(disposing);
        }
    }
}
