using System.Collections.Generic;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 树形分组节点
    /// </summary>
    public class GroupNode
    {
        /// <summary>
        /// 当前层级名称（如 "Web"）
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 完整路径（如 "Jump/Web"）
        /// </summary>
        public string FullPath { get; set; }

        /// <summary>
        /// 子分组
        /// </summary>
        public IList<GroupNode> Children { get; set; }

        /// <summary>
        /// 本组连接
        /// </summary>
        public IList<ConnectionConfig> Connections { get; set; }

        public GroupNode()
        {
            Children = new List<GroupNode>();
            Connections = new List<ConnectionConfig>();
        }
    }
}
