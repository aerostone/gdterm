using System;

namespace Gdterm.KeePass.Models
{
    /// <summary>
    /// 条目摘要（不含密码明文，用于 UI 展示和关联选择）
    /// </summary>
    public class KeePassEntrySummary
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
        /// 分组路径（/ 分隔）
        /// </summary>
        public string GroupPath { get; set; }
    }
}
