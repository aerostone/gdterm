using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 终端标签分屏编排（finding-10）。TabContainer 只转发菜单/快捷键调用。
    /// </summary>
    public sealed class TabSplitService
    {
        private readonly ProtocolTabOpener _opener;

        public TabSplitService(ProtocolTabOpener opener)
        {
            _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        }

        /// <summary>
        /// 将当前终端标签拆为水平/垂直双窗格。成功返回 true。
        /// </summary>
        public bool TrySplit(
            string direction,
            TabPage selectedTab,
            IDictionary<TabPage, TabSessionState> sessions)
        {
            if (selectedTab == null || sessions == null || !sessions.ContainsKey(selectedTab))
            {
                MessageBox.Show("请先打开一个连接", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var session = sessions[selectedTab];
            if (session == null || !(session.Control is TerminalControl))
            {
                MessageBox.Show("仅终端标签支持分屏", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var currentControl = session.Control;
            var newTerminal = _opener.CreateSplitTerminal(session.Config, session.Credential);
            if (newTerminal == null)
            {
                MessageBox.Show("无法创建分屏终端", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var splitPane = string.Equals(direction, "horizontal", StringComparison.OrdinalIgnoreCase)
                ? SplitPaneControl.CreateHorizontal(currentControl, newTerminal, 0.5)
                : SplitPaneControl.CreateVertical(currentControl, newTerminal, 0.5);
            splitPane.Dock = DockStyle.Fill;

            selectedTab.Controls.Clear();
            selectedTab.Controls.Add(splitPane);
            session.Control = splitPane;
            return true;
        }
    }
}
