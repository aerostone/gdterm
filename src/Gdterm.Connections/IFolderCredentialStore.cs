using System.Collections.Generic;

namespace Gdterm.Connections
{
    /// <summary>
    /// 文件夹级凭据存储接口 —— 管理分组路径到凭据的映射
    /// 支持继承：连接无自有 CredentialRefId 时，沿 GroupPath 向上查找最近的祖先凭据
    /// </summary>
    public interface IFolderCredentialStore
    {
        /// <summary>
        /// 加载所有文件夹凭据映射
        /// </summary>
        IList<Core.Models.FolderCredentialEntry> LoadAll();

        /// <summary>
        /// 保存所有映射
        /// </summary>
        void SaveAll(IList<Core.Models.FolderCredentialEntry> entries);

        /// <summary>
        /// 为分组设置凭据
        /// </summary>
        void SetCredential(string groupPath, string credentialRefId);

        /// <summary>
        /// 移除分组凭据
        /// </summary>
        void RemoveCredential(string groupPath);

        /// <summary>
        /// 按继承链查找凭据：从当前路径逐级向上查找最近的凭据映射
        /// </summary>
        /// <param name="groupPath">起始分组路径</param>
        /// <returns>找到的凭据ID，未找到返回 null</returns>
        string ResolveByInheritance(string groupPath);
    }
}
