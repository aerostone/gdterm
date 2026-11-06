using System;
using System.IO;
using Gdterm.Core.Models;
using Renci.SshNet;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 根据 CredentialPayload 构建 SSH.NET ConnectionInfo（私钥优先，否则密码）
    /// </summary>
    internal static class SshConnectionInfoFactory
    {
        public static ConnectionInfo Create(string host, int port, string username, CredentialPayload credential, int timeoutSeconds = 30)
        {
            return Create(host, port, username, credential, timeoutSeconds, null);
        }

        /// <summary>
        /// 带代理的连接信息。proxy 为空或未配好时等价于直连——SSH.NET 的 ConnectionInfo 原生支持
        /// Http/Socks4/Socks5（ProxyType/ProxyHost/ProxyPort/ProxyUsername/ProxyPassword）。
        /// </summary>
        public static ConnectionInfo Create(string host, int port, string username, CredentialPayload credential,
            int timeoutSeconds, ProxyConfig proxy)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            var user = username ?? credential.Username ?? "root";
            ConnectionInfo info;

            if (credential.SshPrivateKey != null && credential.SshPrivateKey.Length > 0)
            {
                // MemoryStream 由 PrivateKeyFile 在构造时读取；不 dispose 流直到 key 解析完成
                var ms = new MemoryStream(credential.SshPrivateKey, writable: false);
                PrivateKeyFile keyFile;
                try
                {
                    if (!string.IsNullOrEmpty(credential.SshPrivateKeyPassphrase))
                        keyFile = new PrivateKeyFile(ms, credential.SshPrivateKeyPassphrase);
                    else
                        keyFile = new PrivateKeyFile(ms);
                }
                finally
                {
                    ms.Dispose();
                }

                info = new PrivateKeyConnectionInfo(host, port, user, keyFile);
            }
            else
            {
                info = new PasswordConnectionInfo(host, port, user, credential.Password ?? "");
            }

            info.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            ApplyProxy(info, proxy);
            return info;
        }

        /// <summary>把 ProxyConfig 灌进 ConnectionInfo（类型不认识/未填好则保持直连）。</summary>
        private static void ApplyProxy(ConnectionInfo info, ProxyConfig proxy)
        {
            if (info == null || proxy == null || !proxy.IsUsable) return;
            switch (proxy.Kind)
            {
                case ProxyKind.Http: info.ProxyType = ProxyTypes.Http; break;
                case ProxyKind.Socks4: info.ProxyType = ProxyTypes.Socks4; break;
                case ProxyKind.Socks5: info.ProxyType = ProxyTypes.Socks5; break;
                default: return;
            }
            info.ProxyHost = proxy.Host;
            info.ProxyPort = proxy.Port;
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                info.ProxyUsername = proxy.Username;
                info.ProxyPassword = proxy.Password ?? string.Empty;
            }
        }
    }
}
