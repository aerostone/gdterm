using System;

namespace Gdterm.KeePass.Models
{
    /// <summary>
    /// 完整密码条目（含密码明文）
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
        /// URL
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
    }
}
