using System;

namespace Gdterm.KeePass.Models
{
    /// <summary>
    /// 完整密码条目（含密码明文和 SSH 密钥）
    /// </summary>
    public class KeePassEntry
    {
        /// <summary>
        /// KeePass 条目 UUID
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 条目标题
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// 用户名
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// 密码明文（仅在创建/更新时使用，不持久化到日志）
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// URL（格式：ssh://host:port 或 rdp://host:port）
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string Notes { get; set; }

        /// <summary>
        /// 分组路径（/ 分隔）
        /// </summary>
        public string GroupPath { get; set; }

        // ===== SSH 密钥认证 =====

        /// <summary>
        /// SSH 私钥文件路径（PEM 格式）
        /// </summary>
        public string SshPrivateKeyPath { get; set; }

        /// <summary>
        /// SSH 私钥内容（PEM 格式，存储为 KeePass 附件）
        /// </summary>
        public byte[] SshPrivateKeyData { get; set; }

        /// <summary>
        /// SSH 私钥密码（保护私钥的密码短语）
        /// </summary>
        public string SshPrivateKeyPassphrase { get; set; }

        // ===== Auto-Type 序列 =====

        /// <summary>
        /// 自定义 Auto-Type 序列（SSH 默认：{USERNAME}{ENTER}{PASSWORD}{ENTER}）
        /// RDP 默认：{USERNAME}{TAB}{PASSWORD}{ENTER}
        /// </summary>
        public string AutoTypeSequence { get; set; }

        // ===== 协议关联 =====

        /// <summary>
        /// 关联协议类型（SSH/RDP/SFTP）
        /// </summary>
        public string Protocol { get; set; }

        /// <summary>
        /// 关联主机名（用于智能匹配）
        /// </summary>
        public string Hostname { get; set; }

        /// <summary>
        /// 关联端口
        /// </summary>
        public int Port { get; set; }
    }
}
