using System;

namespace Gdterm.Security.Models
{
    /// <summary>
    /// 主密码配置（哈希存储）
    /// </summary>
    public class MasterPasswordConfig
    {
        /// <summary>
        /// 密码 SHA256 哈希（Base64 编码）
        /// </summary>
        public string PasswordHash { get; set; }

        /// <summary>
        /// 随机 salt（Base64 编码）
        /// </summary>
        public string Salt { get; set; }

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime? LastChanged { get; set; }
    }
}
