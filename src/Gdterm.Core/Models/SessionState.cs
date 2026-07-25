using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 会话状态快照 —— 用于保存/恢复窗口布局和打开的标签页
    /// </summary>
    public class SessionState
    {
        /// <summary>
        /// 窗口位置 X
        /// </summary>
        public int WindowX { get; set; }

        /// <summary>
        /// 窗口位置 Y
        /// </summary>
        public int WindowY { get; set; }

        /// <summary>
        /// 窗口宽度
        /// </summary>
        public int WindowWidth { get; set; }

        /// <summary>
        /// 窗口高度
        /// </summary>
        public int WindowHeight { get; set; }

        /// <summary>
        /// 窗口状态（Normal/Maximized/Minimized）
        /// </summary>
        public string WindowState { get; set; }

        /// <summary>
        /// 视图模式（Standard/Focus/Compact）
        /// </summary>
        public string ViewMode { get; set; }

        /// <summary>
        /// 左侧连接面板宽度
        /// </summary>
        public int ConnectionPanelWidth { get; set; }

        /// <summary>
        /// 打开的标签页列表（按顺序）
        /// </summary>
        public List<OpenTabState> OpenTabs { get; set; }

        /// <summary>
        /// 当前活跃标签页索引
        /// </summary>
        public int ActiveTabIndex { get; set; }

        /// <summary>
        /// 保存时间
        /// </summary>
        public DateTime SavedAt { get; set; }

        public SessionState()
        {
            OpenTabs = new List<OpenTabState>();
            WindowState = "Normal";
            ViewMode = "Standard";
            ConnectionPanelWidth = 250;
            WindowWidth = 1200;
            WindowHeight = 800;
        }
    }

    /// <summary>
    /// 单个打开标签页的状态
    /// </summary>
    public class OpenTabState
    {
        /// <summary>
        /// 连接配置 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// 标签页标题
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// 连接协议
        /// </summary>
        public string Protocol { get; set; }

        /// <summary>
        /// 目标主机
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// 是否活跃（正在使用）
        /// </summary>
        public bool IsActive { get; set; }
    }
}
