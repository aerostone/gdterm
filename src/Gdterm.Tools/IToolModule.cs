using System;

namespace Gdterm.Tools
{
    /// <summary>
    /// 工具模块统一接口——所有运维工具实现此接口
    /// </summary>
    public interface IToolModule : IDisposable
    {
        /// <summary>工具唯一标识</summary>
        string ToolId { get; }

        /// <summary>显示名称</summary>
        string DisplayName { get; }

        /// <summary>工具描述</summary>
        string Description { get; }

        /// <summary>分类（网络、系统、安全等）</summary>
        string Category { get; }

        /// <summary>创建工具面板（WinForms Control）</summary>
        System.Windows.Forms.Control CreatePanel();

        /// <summary>加载配置</summary>
        void LoadConfig();

        /// <summary>保存配置</summary>
        void SaveConfig();
    }

    /// <summary>
    /// 支持远程 SSH 执行的工具模块
    /// </summary>
    public interface IRemoteToolModule : IToolModule
    {
        /// <summary>设置 SSH 会话（用于在远程机器上执行命令）</summary>
        void SetSshSession(Renci.SshNet.SshClient client);

        /// <summary>清除 SSH 会话</summary>
        void ClearSshSession();

        /// <summary>当前是否已连接远程</summary>
        bool HasRemoteSession { get; }
    }
}
