using System;
using System.Windows.Forms;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.Rdp;
using Gdterm.Terminal;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 单个标签的会话状态（从 TabContainer 内嵌类提升，供 ProtocolTabOpener 返回）。
    /// </summary>
    public sealed class TabSessionState
    {
        public ConnectionConfig Config { get; set; }
        public Control Control { get; set; }
        public ProtocolType Protocol { get; set; }
        public bool IsConnected { get; set; }
        public CredentialPayload Credential { get; set; }
        public string SessionId { get; set; }
        public IRdpClient RdpClient { get; set; }
        public ConnectionHealthMonitor HealthMonitor { get; set; }
        public Action PendingConnect { get; set; }
    }

    /// <summary>协议工厂产出的标签页 + 会话状态对。</summary>
    public sealed class OpenedTab
    {
        public TabPage Page { get; set; }
        public TabSessionState Session { get; set; }
    }
}
