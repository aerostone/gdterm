using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 文件夹凭据映射条目 —— 将分组路径关联到 KeePass 凭据
    /// </summary>
    public class FolderCredentialEntry
    {
        /// <summary>
        /// 分组路径（如 "生产环境/Web"）
        /// </summary>
        public string GroupPath { get; set; }

        /// <summary>
        /// 关联的 KeePass 条目 UUID
        /// </summary>
        public string CredentialRefId { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string Note { get; set; }
    }
}
