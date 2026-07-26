using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签选中切换：活动标签恢复渲染/健康监控，非活动暂停；RDP 懒连接触发（finding-10）。
    /// </summary>
    public sealed class TabSelectionCoordinator
    {
        /// <summary>
        /// 处理 SelectedIndexChanged。调用方负责再触发 ActiveSessionChanged。
        /// </summary>
        public void OnSelectedChanged(
            TabControl tabControl,
            IDictionary<TabPage, TabSessionState> sessions)
        {
            if (tabControl == null || sessions == null) return;

            var selected = tabControl.SelectedTab;
            foreach (var kvp in sessions)
            {
                bool isSelected = kvp.Key == selected;
                var state = kvp.Value;
                if (state == null) continue;

                if (state.Control is TerminalControl tc)
                {
                    if (isSelected) tc.ResumeRendering();
                    else tc.PauseRendering();
                }

                if (state.HealthMonitor != null)
                    state.HealthMonitor.IsPaused = !isSelected;

                // RDP 懒连接：仅在首次选中时触发
                if (isSelected && state.PendingConnect != null && !state.IsConnected)
                {
                    var connect = state.PendingConnect;
                    state.PendingConnect = null;
                    try { connect(); }
                    catch { }
                }
            }
        }
    }
}
