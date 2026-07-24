namespace Gdterm.Core.Models
{
    /// <summary>
    /// 跳板链中的单个节点
    /// </summary>
    public class JumpHop
    {
        /// <summary>
        /// 跳板主机（IP 或域名）
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// SSH 端口，默认 22
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 登录用户名
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// 关联的 KeePass 条目 UUID（可选）
        /// </summary>
        public string CredentialRefId { get; set; }
    }
}
