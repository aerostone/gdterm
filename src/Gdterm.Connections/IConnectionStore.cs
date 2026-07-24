using System.Collections.Generic;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 连接配置存储接口
    /// </summary>
    public interface IConnectionStore
    {
        /// <summary>
        /// 加载所有连接配置（从 connections.json）
        /// </summary>
        IList<ConnectionConfig> LoadAll();

        /// <summary>
        /// 保存所有连接配置（写入 connections.json）
        /// </summary>
        void SaveAll(IList<ConnectionConfig> connections);

        /// <summary>
        /// 添加连接，自动生成 Id，持久化
        /// </summary>
        ConnectionConfig Add(ConnectionConfig connection);

        /// <summary>
        /// 更新连接（按 Id 匹配），持久化
        /// </summary>
        /// <exception cref="KeyNotFoundException">Id 不存在时抛出</exception>
        void Update(ConnectionConfig connection);

        /// <summary>
        /// 删除连接（按 Id），持久化
        /// </summary>
        /// <exception cref="KeyNotFoundException">Id 不存在时抛出</exception>
        void Delete(string connectionId);

        /// <summary>
        /// 按 Id 查询单个连接
        /// </summary>
        ConnectionConfig GetById(string connectionId);

        /// <summary>
        /// 获取树形分组结构（按 GroupPath 分层）
        /// </summary>
        IList<GroupNode> GetGroupTree();
    }
}
