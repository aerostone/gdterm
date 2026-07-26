using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 运行时凭据（含明文，仅在内存中临时传递，不持久化）
    /// 由 KeePass 解锁后自动填充
    /// </summary>
    public class CredentialPayload
    {
        /// <summary>
        /// 用户名
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// 密码明文
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// SSH 私钥数据（PEM 格式，从 KeePass 附件获取）
        /// </summary>
        public byte[] SshPrivateKey { get; set; }

        /// <summary>
        /// SSH 私钥密码短语（保护私钥的密码）
        /// </summary>
        public string SshPrivateKeyPassphrase { get; set; }

        /// <summary>
        /// 跳板 hop.CredentialRefId → 密码（UI 从 KeePass 预解析后填入；finding-06）
        /// </summary>
        public Dictionary<string, string> HopPasswordsByRefId { get; set; }

        /// <summary>
        /// 解析跳板密码：优先 hop.CredentialRefId 映射，否则回落叶子 Password。
        /// </summary>
        public static string ResolveHopPassword(JumpHop hop, CredentialPayload credential)
        {
            if (hop != null &&
                !string.IsNullOrEmpty(hop.CredentialRefId) &&
                credential != null &&
                credential.HopPasswordsByRefId != null)
            {
                string pwd;
                if (credential.HopPasswordsByRefId.TryGetValue(hop.CredentialRefId, out pwd) &&
                    !string.IsNullOrEmpty(pwd))
                    return pwd;
            }
            return credential != null ? credential.Password : null;
        }

        /// <summary>
        /// 清除敏感字段（锁屏/关签；finding-04）
        /// </summary>
        public void ClearSecrets()
        {
            Password = null;
            SshPrivateKeyPassphrase = null;
            if (SshPrivateKey != null)
            {
                for (int i = 0; i < SshPrivateKey.Length; i++)
                    SshPrivateKey[i] = 0;
                SshPrivateKey = null;
            }
            if (HopPasswordsByRefId != null)
            {
                HopPasswordsByRefId.Clear();
                HopPasswordsByRefId = null;
            }
        }
    }
}
