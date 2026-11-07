using System;
using Gdterm.Core;

namespace Gdterm.Core.Models
{
    /// <summary>连接前先过的代理类型（对应 SSH.NET ProxyTypes）。</summary>
    public enum ProxyKind
    {
        /// <summary>直连（默认）。</summary>
        None = 0,

        /// <summary>HTTP CONNECT 代理（企业内网最常见）。</summary>
        Http = 1,

        /// <summary>SOCKS5（含用户名/密码认证）。</summary>
        Socks5 = 2,

        /// <summary>SOCKS4（老内网仍有）。</summary>
        Socks4 = 3
    }

    /// <summary>
    /// 每连接一份的代理设置。SSH.NET 的 ConnectionInfo 原生支持，之前全仓没用（企业内网连不出去的根因）。
    /// 密码与主机/用户名一样由 ConnectionStoreJson 走 ProtectField（主密码派生密钥）落盘。
    /// </summary>
    public class ProxyConfig
    {
        public ProxyKind Kind { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        /// <summary>能不能真的用（类型选了、地址和端口合法）。</summary>
        public bool IsUsable
        {
            get
            {
                return Kind != ProxyKind.None
                    && !string.IsNullOrWhiteSpace(Host)
                    && Port > 0 && Port <= 65535;
            }
        }

        /// <summary>各类型的惯用端口（UI 切换类型时填默认值）。</summary>
        public static int DefaultPort(ProxyKind kind)
        {
            switch (kind)
            {
                case ProxyKind.Http: return 8080;
                case ProxyKind.Socks4:
                case ProxyKind.Socks5: return 1080;
                default: return 0;
            }
        }

        public static string DisplayName(ProxyKind kind)
        {
            switch (kind)
            {
                case ProxyKind.Http: return "HTTP";
                case ProxyKind.Socks4: return "SOCKS4";
                case ProxyKind.Socks5: return "SOCKS5";
                default: return L10n.T("不使用代理");
            }
        }

        /// <summary>日志/状态栏摘要（绝不含密码）。</summary>
        public string Describe()
        {
            if (!IsUsable) return L10n.T("直连");
            var user = string.IsNullOrEmpty(Username) ? "" : Username + "@";
            return DisplayName(Kind) + " " + user + Host + ":" + Port;
        }

        public ProxyConfig Clone()
        {
            return new ProxyConfig
            {
                Kind = Kind,
                Host = Host,
                Port = Port,
                Username = Username,
                Password = Password
            };
        }

        public override string ToString() { return Describe(); }
    }
}
