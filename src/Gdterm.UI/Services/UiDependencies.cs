using System;
using System.Collections.Generic;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Rdp;
using Gdterm.Security;
using Gdterm.Security.Otp;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 组合根依赖包（2026-11-11 架构审计 F4）。
    ///
    /// 原先 <see cref="Gdterm.UI.Forms.MainForm"/> 构造参数长到 23 个、<c>TabContainerControl</c> 14 个，
    /// 后加的依赖一撞 C# 可选参数顺序就只能改走 SetXxx 旁路（Otp 就是这么进的 _toolsDialogs），
    /// 于是同一份组合根出现两种注入形态。现在统一成：<b>谁需要依赖，就收一个依赖对象</b>。
    ///
    /// 新增一个服务只需在这里加一个属性（+ Program.cs 一行赋值），不再动 MainForm/TabContainer 签名。
    /// </summary>
    public sealed class UiDependencies
    {
        // —— 必需（缺了主界面没法工作） ——
        public IConnectionStore ConnectionStore { get; set; }
        public ITunnelManager TunnelManager { get; set; }
        public ITerminalSessionFactory TerminalFactory { get; set; }
        public ISftpServiceFactory SftpFactory { get; set; }
        public IKeePassService KeePassService { get; set; }
        public IAuditLogger AuditLogger { get; set; }
        public IAiAssistantService AiService { get; set; }
        public ISecurityManager SecurityManager { get; set; }
        public DangerousCommandDetector DangerousCommandDetector { get; set; }
        public IFolderCredentialStore FolderCredentialStore { get; set; }
        public SessionStateStore SessionStateStore { get; set; }

        // —— 可选（未注入时 Feature 自身降级 / 用默认实现） ——
        public IRdpClientFactory RdpFactory { get; set; }
        public IBookmarkStore BookmarkStore { get; set; }
        public CommandHistoryStore CommandHistoryStore { get; set; }
        public QuickCommandStore QuickCommandStore { get; set; }
        public TerminalKeyBindingStore KeyBindingStore { get; set; }
        public HighlightStore HighlightStore { get; set; }
        public AutoReconnectWatchdog ReconnectWatchdog { get; set; }
        public MultiChannelManager MultiChannelManager { get; set; }
        public ToolRegistry ToolRegistry { get; set; }
        public SecretScanner SecretScanner { get; set; }
        public CommandTemplateStore CommandTemplateStore { get; set; }
        public IOtpStore OtpStore { get; set; }

        /// <summary>必需依赖是否齐备（便于测试与启动自检给出明确信息，而不是 NRE）。</summary>
        public void RequireCore()
        {
            var missing = new List<string>();
            if (ConnectionStore == null) missing.Add(nameof(ConnectionStore));
            if (TunnelManager == null) missing.Add(nameof(TunnelManager));
            if (TerminalFactory == null) missing.Add(nameof(TerminalFactory));
            if (SftpFactory == null) missing.Add(nameof(SftpFactory));
            if (KeePassService == null) missing.Add(nameof(KeePassService));
            if (AuditLogger == null) missing.Add(nameof(AuditLogger));
            if (AiService == null) missing.Add(nameof(AiService));
            if (SecurityManager == null) missing.Add(nameof(SecurityManager));
            if (DangerousCommandDetector == null) missing.Add(nameof(DangerousCommandDetector));
            if (FolderCredentialStore == null) missing.Add(nameof(FolderCredentialStore));
            if (SessionStateStore == null) missing.Add(nameof(SessionStateStore));
            if (missing.Count > 0)
                throw new ArgumentException("UiDependencies 缺少必需依赖: " + string.Join(", ", missing.ToArray()));
        }
    }

    /// <summary>
    /// 标签容器依赖包——只要 TabContainerControl 真正用到的那几个。
    /// 刻意不与 <see cref="UiDependencies"/> 合并：容器不该看见安全/审计/工具注册等它不消费的服务。
    /// 注意没有 AiService：原先传了但容器内部 <c>_ = aiService</c> 直接丢弃（审计 F4 的僵尸依赖）。
    /// </summary>
    public sealed class TabDependencies
    {
        public ITunnelManager TunnelManager { get; set; }
        public ITerminalSessionFactory TerminalFactory { get; set; }
        public ISftpServiceFactory SftpFactory { get; set; }
        public IAuditLogger AuditLogger { get; set; }
        public IKeePassService KeePassService { get; set; }
        public IFolderCredentialStore FolderCredentialStore { get; set; }
        public IRdpClientFactory RdpFactory { get; set; }
        public DangerousCommandDetector DangerousCommandDetector { get; set; }
        public AutoReconnectWatchdog ReconnectWatchdog { get; set; }
        public IConnectionStore ConnectionStore { get; set; }
        public HighlightStore HighlightStore { get; set; }
        public CommandHistoryStore CommandHistoryStore { get; set; }
        public QuickCommandStore QuickCommandStore { get; set; }
    }
}
