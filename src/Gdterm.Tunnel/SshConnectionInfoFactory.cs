using System;
using System.IO;
using Gdterm.Core.Models;
using Renci.SshNet;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// 根据 CredentialPayload 构建 SSH.NET ConnectionInfo（私钥优先，否则密码）。
    /// 唯一实现：Terminal 与 Sftp 原先各有一份逐字节相同的副本（2026-11-11 架构审计 F2），
    /// 现收敛到两者共同引用的 Gdterm.Tunnel（都依赖 Core + SSH.NET，且 Tunnel 不反向依赖它们）。
    /// </summary>
    public static class SshConnectionInfoFactory
    {
        public static ConnectionInfo Create(string host, int port, string username, CredentialPayload credential, int timeoutSeconds = 30)
        {
            return Create(host, port, username, credential, timeoutSeconds, null);
        }

        /// <summary>
        /// 带代理的连接信息。proxy 为空或未配好时等价于直连——SSH.NET 的 ConnectionInfo 原生支持
        /// Http/Socks4/Socks5（构造 ConnectionInfo 时传 ProxyTypes，属性只读）。
        /// </summary>
        public static ConnectionInfo Create(string host, int port, string username, CredentialPayload credential,
            int timeoutSeconds, ProxyConfig proxy)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            var user = username ?? credential.Username ?? "root";
            // SSH.NET 2026 的 Proxy* 属性只读 —— 代理必须在构造 ConnectionInfo 时传，
            // 且「密码认证 + 代理认证」没有公开构造重载，只能退回 ConnectionInfo + AuthenticationMethod。
            ProxyTypes proxyType;
            bool useProxy = TryMapProxy(proxy, out proxyType);
            // proxy 为 null 是**正常**状态（ConnectionConfig.Proxy 文档：「null 或 Kind=None 表示直连」，
            // 且 ConnectionDialog 在未启用代理时就把 Proxy 置 null）——绝不能在 useProxy 检查之外解引用。
            // 2026-11-11 实证：裸 `proxy.Password ?? ""` 让每一台「没配代理」的 SSH 连接在
            // Connect() 里抛 NullReferenceException，直连 SSH 全部连不上。
            string proxyUser = useProxy && proxy != null && !string.IsNullOrEmpty(proxy.Username) ? proxy.Username : null;
            string proxyPass = proxy != null ? (proxy.Password ?? string.Empty) : string.Empty;
            ConnectionInfo info;

            PrivateKeyFile keyFile = null;
            if (credential.SshPrivateKey != null && credential.SshPrivateKey.Length > 0)
            {
                // MemoryStream 由 PrivateKeyFile 在构造时读取；不 dispose 流直到 key 解析完成
                var ms = new MemoryStream(credential.SshPrivateKey, writable: false);
                try
                {
                    keyFile = string.IsNullOrEmpty(credential.SshPrivateKeyPassphrase)
                        ? new PrivateKeyFile(ms)
                        : new PrivateKeyFile(ms, credential.SshPrivateKeyPassphrase);
                }
                finally
                {
                    ms.Dispose();
                }
            }

            var password = credential.Password ?? "";
            if (!useProxy)
            {
                info = keyFile != null
                    ? (ConnectionInfo)new PrivateKeyConnectionInfo(host, port, user, keyFile)
                    : new PasswordConnectionInfo(host, port, user, password);
            }
            else if (proxyUser != null)
            {
                // 代理要认证：ConnectionInfo 有 (proxyUsername, proxyPassword) 的完整重载
                AuthenticationMethod auth = keyFile != null
                    ? (AuthenticationMethod)new PrivateKeyAuthenticationMethod(user, keyFile)
                    : new PasswordAuthenticationMethod(user, password);
                info = new ConnectionInfo(host, port, user, proxyType, proxy.Host, proxy.Port, proxyUser, proxyPass, auth);
            }
            else
            {
                info = keyFile != null
                    ? (ConnectionInfo)new PrivateKeyConnectionInfo(host, port, user, proxyType, proxy.Host, proxy.Port, keyFile)
                    : new PasswordConnectionInfo(host, port, user, password, proxyType, proxy.Host, proxy.Port);
            }

            info.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return info;
        }

        /// <summary>ProxyConfig → SSH.NET ProxyTypes；未配好/类型不认识则 false（直连）。</summary>
        private static bool TryMapProxy(ProxyConfig proxy, out ProxyTypes type)
        {
            type = ProxyTypes.None;
            if (proxy == null || !proxy.IsUsable) return false;
            switch (proxy.Kind)
            {
                case ProxyKind.Http: type = ProxyTypes.Http; return true;
                case ProxyKind.Socks4: type = ProxyTypes.Socks4; return true;
                case ProxyKind.Socks5: type = ProxyTypes.Socks5; return true;
                default: return false;
            }
        }
    }
}
