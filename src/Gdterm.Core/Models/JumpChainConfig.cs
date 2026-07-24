using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 跳板链配置——从客户端到目标机器的跳板节点有序列表
    /// </summary>
    /// <remarks>
    /// null 表示直连；Hops 不可为空列表（空列表非法）。
    /// </remarks>
    public class JumpChainConfig
    {
        /// <summary>
        /// 按顺序的跳板节点列表
        /// </summary>
        public List<JumpHop> Hops { get; set; }
    }
}
