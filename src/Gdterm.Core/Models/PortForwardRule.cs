using System;
using System.Net;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 端口转发规则
    /// </summary>
    public class PortForwardRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public PortForwardType Type { get; set; }
        public string LocalHost { get; set; } = "127.0.0.1";
        public int LocalPort { get; set; }
        public string RemoteHost { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; }
        public string ConnectionId { get; set; }
        public string ConnectionName { get; set; }
        public bool Enabled { get; set; } = true;
        public string Description { get; set; }

        public string EndpointError()
        {
            if (!Enum.IsDefined(typeof(PortForwardType), Type)) return L10n.T("转发类型无效");
            if (!ValidHost(LocalHost)) return L10n.T("请填写有效的本地地址");
            if (LocalPort < 1 || LocalPort > 65535) return L10n.T("本地端口必须在 1–65535 之间");
            if (Type != PortForwardType.Dynamic)
            {
                if (!ValidHost(RemoteHost)) return L10n.T("请填写有效的远程地址");
                if (RemotePort < 1 || RemotePort > 65535) return L10n.T("远程端口必须在 1–65535 之间");
            }
            return null;
        }

        private static bool ValidHost(string host)
        {
            return !string.IsNullOrWhiteSpace(host) &&
                (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns);
        }
    }

    public enum PortForwardType
    {
        /// <summary>本地转发（L）——本地端口 → 远程主机</summary>
        Local,
        /// <summary>远程转发（R）——远程端口 → 本地主机</summary>
        Remote,
        /// <summary>动态转发（D）——SOCKS5 代理</summary>
        Dynamic
    }
}
