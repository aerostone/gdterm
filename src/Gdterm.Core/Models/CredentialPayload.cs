namespace Gdterm.Core.Models
{
    /// <summary>
    /// 运行时凭据（含明文，仅在内存中临时传递，不持久化）
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
    }
}
