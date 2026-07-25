using System.Collections.Generic;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 会话书签存储接口
    /// </summary>
    public interface IBookmarkStore
    {
        /// <summary>
        /// 加载所有书签
        /// </summary>
        IList<SessionBookmark> LoadAll();

        /// <summary>
        /// 保存所有书签
        /// </summary>
        void SaveAll(IList<SessionBookmark> bookmarks);

        /// <summary>
        /// 添加书签
        /// </summary>
        void Add(SessionBookmark bookmark);

        /// <summary>
        /// 删除书签
        /// </summary>
        void Delete(string bookmarkId);

        /// <summary>
        /// 更新书签
        /// </summary>
        void Update(SessionBookmark bookmark);

        /// <summary>
        /// 记录最近连接
        /// </summary>
        void AddRecentConnection(RecentConnection connection);

        /// <summary>
        /// 获取最近连接列表
        /// </summary>
        IList<RecentConnection> GetRecentConnections(int limit = 20);
    }
}
