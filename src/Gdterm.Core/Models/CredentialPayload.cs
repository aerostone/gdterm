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
    }
}
