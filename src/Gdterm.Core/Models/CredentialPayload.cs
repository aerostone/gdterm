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

        /// <summary>每跳条目中的用户名；显式 hop.Username 优先。</summary>
        public Dictionary<string, string> HopUsernamesByRefId { get; set; }

        /// <summary>
        /// 跳板 hop.CredentialRefId → 私钥内容（OpenSSH / PEM）。
        /// </summary>
        public Dictionary<string, byte[]> HopPrivateKeysByRefId { get; set; }

        /// <summary>
        /// 跳板 hop.CredentialRefId → 私钥口令。
        /// </summary>
        public Dictionary<string, string> HopPrivateKeyPassphrasesByRefId { get; set; }

        /// <summary>
        /// 仅解析显式绑定的跳板密码；缺失与空密码必须区分，绝不回落目标密码。
        /// </summary>
        public static string ResolveHopPassword(JumpHop hop, CredentialPayload credential)
        {
            if (hop != null &&
                !string.IsNullOrEmpty(hop.CredentialRefId) &&
                credential != null &&
                credential.HopPasswordsByRefId != null)
            {
                string pwd;
                if (credential.HopPasswordsByRefId.TryGetValue(hop.CredentialRefId, out pwd))
                    return pwd;
            }
            return null;
        }

        public static string ResolveHopUsername(JumpHop hop, CredentialPayload credential)
        {
            if (!string.IsNullOrWhiteSpace(hop?.Username)) return hop.Username;
            string user;
            if (!string.IsNullOrEmpty(hop?.CredentialRefId) && credential?.HopUsernamesByRefId != null &&
                credential.HopUsernamesByRefId.TryGetValue(hop.CredentialRefId, out user) && !string.IsNullOrWhiteSpace(user))
                return user;
            return "root";
        }

        /// <summary>解析跳板私钥；无映射返回 null（非叶子私钥）。</summary>
        public static byte[] ResolveHopPrivateKey(JumpHop hop, CredentialPayload credential)
        {
            if (hop == null || string.IsNullOrEmpty(hop.CredentialRefId) || credential?.HopPrivateKeysByRefId == null)
                return null;
            byte[] key;
            if (credential.HopPrivateKeysByRefId.TryGetValue(hop.CredentialRefId, out key) &&
                key != null && key.Length > 0)
                return key;
            return null;
        }

        public static string ResolveHopPrivateKeyPassphrase(JumpHop hop, CredentialPayload credential)
        {
            if (hop == null || string.IsNullOrEmpty(hop.CredentialRefId) ||
                credential?.HopPrivateKeyPassphrasesByRefId == null)
                return null;
            string pp;
            if (credential.HopPrivateKeyPassphrasesByRefId.TryGetValue(hop.CredentialRefId, out pp))
                return pp;
            return null;
        }

        /// <summary>
        /// 清除敏感字段（锁屏/关签；finding-04）
        /// </summary>
        public void ClearSecrets()
        {
            Password = null;
            SshPrivateKeyPassphrase = null;
            HopUsernamesByRefId?.Clear();
            HopUsernamesByRefId = null;
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
            if (HopPrivateKeysByRefId != null)
            {
                foreach (var kv in HopPrivateKeysByRefId)
                {
                    var arr = kv.Value;
                    if (arr == null) continue;
                    for (int i = 0; i < arr.Length; i++) arr[i] = 0;
                }
                HopPrivateKeysByRefId.Clear();
                HopPrivateKeysByRefId = null;
            }
            if (HopPrivateKeyPassphrasesByRefId != null)
            {
                HopPrivateKeyPassphrasesByRefId.Clear();
                HopPrivateKeyPassphrasesByRefId = null;
            }
        }
    }
}
