using System;

namespace Gdterm.Security.Models
{
    /// <summary>
    /// 主密码配置（哈希存储）。
    /// Algorithm: null/sha256 = 旧版单次 SHA256(salt‖pwd)；pbkdf2 = PBKDF2-HMAC-SHA256。
    /// </summary>
    public class MasterPasswordConfig
    {
        /// <summary>密码派生哈希（Base64）</summary>
        public string PasswordHash { get; set; }

        /// <summary>随机 salt（Base64）</summary>
        public string Salt { get; set; }

        /// <summary>
        /// 算法标识：null/"sha256" = 旧版；"pbkdf2" = PBKDF2-HMAC-SHA256。
        /// </summary>
        public string Algorithm { get; set; }

        /// <summary>PBKDF2 迭代次数（仅 algorithm=pbkdf2 有效，默认 100000）</summary>
        public int Iterations { get; set; }

        /// <summary>最后修改时间</summary>
        public DateTime? LastChanged { get; set; }

        /// <summary>是否为旧版单次 SHA256 格式</summary>
        public bool IsLegacySha256
        {
            get
            {
                return string.IsNullOrEmpty(Algorithm)
                    || string.Equals(Algorithm, "sha256", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
