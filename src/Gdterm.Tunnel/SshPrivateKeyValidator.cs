using System;
using System.IO;
using System.Text;
using Gdterm.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Gdterm.Tunnel
{
    /// <summary>导入时使用与 SSH 认证相同的解析器，避免把不可用的密钥存进密码库。</summary>
    public static class SshPrivateKeyValidator
    {
        public static bool TryValidate(byte[] data, string passphrase, out string error)
        {
            error = null;
            if (data == null || data.Length == 0)
                error = L10n.T("请选择有效的私钥文件。");
            else
            {
                var header = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 80)).TrimStart();
                if (header.StartsWith("ssh-", StringComparison.Ordinal) || header.StartsWith("ecdsa-", StringComparison.Ordinal)
                    || header.Contains("BEGIN PUBLIC KEY") || header.Contains("BEGIN RSA PUBLIC KEY"))
                    error = L10n.T("选择的是公钥，请选择对应的私钥文件（不要选 .pub）。");
                else
                {
                    try
                    {
                        using (var stream = new MemoryStream(data, false))
                        using (var key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase))
                            return true;
                    }
                    catch (SshPassPhraseNullOrEmptyException)
                    {
                        error = L10n.T("该私钥已加密，请输入密钥口令。");
                    }
                    catch (Exception)
                    {
                        error = L10n.T("无法解析私钥，请检查文件格式及密钥口令（支持 OpenSSH / PEM）。");
                    }
                }
            }
            return false;
        }
    }
}
