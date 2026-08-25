using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;

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
            try
            {
                DiagLog.Info("TabSelect.OnSelectedChanged",
                    "selected=" + (selected != null ? selected.Text : "<none>") +
                    " sessions=" + sessions.Count);
            }
            catch { }
            foreach (var kvp in sessions)
            {
                bool isSelected = kvp.Key == selected;
                var state = kvp.Value;
                if (state == null) continue;

                // finding-05：分屏标签下对所有 TerminalControl 暂停/恢复
                var terminals = new List<TerminalControl>();
                TabActiveSessionQuery.CollectSessionTerminals(state, terminals);
                foreach (var tc in terminals)
                {
                    if (tc == null) continue;
                    if (isSelected) tc.ResumeRendering();
                    else tc.PauseRendering();
                }

                try
                {
                    DiagLog.Info("TabSelect.OnSelectedChanged",
                        (isSelected ? "resume" : "pause") + " proto=" + state.Protocol +
                        " id=" + state.SessionId + " terminals=" + terminals.Count);
                }
                catch { }

                if (state.HealthMonitor != null)
                    state.HealthMonitor.IsPaused = !isSelected;

                // RDP 懒连接：仅在首次选中时触发
                if (isSelected && state.PendingConnect != null && !state.IsConnected)
                {
                    var connect = state.PendingConnect;
                    state.PendingConnect = null;
                    try
                    {
                        DiagLog.Info("TabSelect.RdpLazyConnect", "id=" + state.SessionId);
                        connect();
                    }
                    catch (Exception ex)
                    {
                        // RDP 连接失败必须留痕（此前静默吞掉，用户只看到无反应）
                        DiagLog.Swallowed("TabSelect.RdpLazyConnect", ex);
                    }
                }
            }
        }
    }
}
