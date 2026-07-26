using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 终端标签分屏编排。finding-05：分屏后保留 PrimaryTerminal，Control 改为 SplitPane。
    /// </summary>
    public sealed class TabSplitService
    {
        private readonly ProtocolTabOpener _opener;

        public TabSplitService(ProtocolTabOpener opener)
        {
            _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        }

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
            var currentTerminal = TabActiveSessionQuery.ResolveTerminal(session);
            if (session == null || currentTerminal == null)
            {
                MessageBox.Show("仅终端标签支持分屏", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var newTerminal = _opener.CreateSplitTerminal(session.Config, session.Credential);
            if (newTerminal == null)
            {
                MessageBox.Show("无法创建分屏终端", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // 若当前已是分屏，以当前主终端为左/上窗格根
            Control leftOrTop = session.Control is SplitPaneControl ? session.Control : currentTerminal;

            var splitPane = string.Equals(direction, "horizontal", StringComparison.OrdinalIgnoreCase)
                ? SplitPaneControl.CreateHorizontal(leftOrTop, newTerminal, 0.5)
                : SplitPaneControl.CreateVertical(leftOrTop, newTerminal, 0.5);
            splitPane.Dock = DockStyle.Fill;

            selectedTab.Controls.Clear();
            selectedTab.Controls.Add(splitPane);
            session.Control = splitPane;
            session.PrimaryTerminal = currentTerminal;
            return true;
        }
    }
}
