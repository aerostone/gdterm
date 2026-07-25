using System;
using System.IO;
using Gdterm.Core.Models;
using Renci.SshNet;

namespace Gdterm.Sftp
{
    /// <summary>
    /// 根据 CredentialPayload 构建 SSH.NET ConnectionInfo（私钥优先，否则密码）
    /// </summary>
    internal static class SshConnectionInfoFactory
    {
        public static ConnectionInfo Create(string host, int port, string username, CredentialPayload credential, int timeoutSeconds = 30)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            var user = username ?? credential.Username ?? "root";
            ConnectionInfo info;

            if (credential.SshPrivateKey != null && credential.SshPrivateKey.Length > 0)
            {
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
            return info;
        }
    }
}
