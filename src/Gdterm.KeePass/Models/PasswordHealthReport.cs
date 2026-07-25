using System.Collections.Generic;

namespace Gdterm.KeePass.Models
{
    /// <summary>
    /// 密码库健康报告
    /// </summary>
    public class PasswordHealthReport
    {
        /// <summary>总条目数</summary>
        public int TotalEntries { get; set; }

        /// <summary>弱密码条目（强度评分 ≤ 40）</summary>
        public IList<PasswordIssue> WeakPasswords { get; set; } = new List<PasswordIssue>();

        /// <summary>重复使用的密码（同一密码被多个条目使用）</summary>
        public IList<DuplicatePasswordGroup> DuplicatePasswords { get; set; } = new List<DuplicatePasswordGroup>();

        /// <summary>空密码条目</summary>
        public IList<PasswordIssue> EmptyPasswords { get; set; } = new List<PasswordIssue>();

        /// <summary>过期密码（超过 90 天未更新）</summary>
        public IList<PasswordIssue> ExpiredPasswords { get; set; } = new List<PasswordIssue>();

        /// <summary>健康评分 0-100</summary>
        public int HealthScore { get; set; }

        /// <summary>总体评价</summary>
        public string Summary { get; set; }
    }

    /// <summary>
    /// 单个密码问题
    /// </summary>
    public class PasswordIssue
    {
        /// <summary>条目 ID</summary>
        public string EntryId { get; set; }

        /// <summary>条目标题</summary>
        public string Title { get; set; }

        /// <summary>用户名</summary>
        public string Username { get; set; }

        /// <summary>分组路径</summary>
        public string GroupPath { get; set; }

        /// <summary>问题描述</summary>
        public string Issue { get; set; }

        /// <summary>密码强度评分（0-100）</summary>
        public int StrengthScore { get; set; }
    }

    /// <summary>
    /// 重复密码组
    /// </summary>
    public class DuplicatePasswordGroup
    {
        /// <summary>密码哈希（不暴露明文）</summary>
        public string PasswordHash { get; set; }

        /// <summary>使用此密码的条目</summary>
        public IList<PasswordIssue> Entries { get; set; } = new List<PasswordIssue>();
    }
}
