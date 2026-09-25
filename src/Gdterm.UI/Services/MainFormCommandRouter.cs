using System;
using System.Windows.Forms;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// MainForm 快捷键路由（finding-10）。ProcessCmdKey 只做转发。
    /// </summary>
    public sealed class MainFormCommandRouter
    {
        private readonly TabContainerControl _tabs;
        private readonly SidePanelFactory _sidePanels;
        private readonly SidePanelHost _sideHost;
        private readonly ViewModeController _viewMode;
        private readonly Action _toggleTmuxGroup;
        private readonly Action _showAiChat;

        /// <summary>快捷键注册表项（审计 F7 SSOT：路由与帮助文本同源）。</summary>
        internal sealed class HotkeyEntry
        {
            public readonly Keys Combo;      // 键组合
            public readonly string Desc;     // 帮助文本描述
            public readonly Action Run;      // 动作
            public HotkeyEntry(Keys combo, string desc, Action run)
            { Combo = combo; Desc = desc; Run = run; }
        }

        private System.Collections.Generic.List<HotkeyEntry> _registry;

        public MainFormCommandRouter(
            TabContainerControl tabs,
            SidePanelFactory sidePanels,
            SidePanelHost sideHost,
            ViewModeController viewMode,
            Action toggleTmuxGroup = null,
            Action showAiChat = null)
        {
            _tabs = tabs;
            _sidePanels = sidePanels;
            _sideHost = sideHost;
            _viewMode = viewMode;
            _toggleTmuxGroup = toggleTmuxGroup;
            _showAiChat = showAiChat;
            BuildRegistry();
        }

        /// <summary>平表项：注册即路由、帮助即投影。参数化/特例语义（Escape/F11/Ctrl+Tab/Alt+数字）不入平表。</summary>
        private void BuildRegistry()
        {
            _registry = new System.Collections.Generic.List<HotkeyEntry>
            {
                // 快速跳转（普通 Ctrl+K 是 shell kill-line，不能被抢）——实际路由点在 MainForm.ProcessCmdKey（先于本 router），
                // 登记于此仅为帮助文本同源
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.K, "快速跳转连接", null),
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.R, "重连当前标签",
                    () => _tabs?.ReconnectActiveTab()),
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.W, "关闭当前标签",
                    () => _tabs?.CloseActiveTab()),
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.F, "终端查找",
                    () => _sidePanels?.AttachSearchBar(_tabs)),
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.P, "片段搜索",
                    () => _sideHost?.ShowSnippetSearch(_sidePanels, _tabs)),
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.H, "宏录制",
                    () => { try { _sideHost?.Show(_sidePanels.CreateMacroPanel()); } catch { } }),
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.G, "AI 助手聊天",
                    () => { try { if (_showAiChat != null) _showAiChat(); } catch { } }),
                new HotkeyEntry(Keys.Control | Keys.Shift | Keys.Z, "Zmodem 接收",
                    () =>
                    {
                        try
                        {
                            var tc = _tabs != null ? _tabs.GetActiveTerminalControl() : null;
                            if (tc != null) tc.RequestZmodemReceive();
                        }
                        catch { }
                    }),
            };
        }

        /// <summary>帮助文本渲染（审计 F7：与路由同表同源，杜绝帮助与实际键位漂移）。</summary>
        internal string RenderHelpLines()
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var e in _registry)
                lines.Add(FormatKeys(e.Combo) + "    " + e.Desc);
            return string.Join("\n", lines);
        }

        /// <summary>Keys → 展示串（Ctrl + Shift + X 风格，与既有帮助文本一致）。</summary>
        internal static string FormatKeys(Keys k)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((k & Keys.Control) != 0) parts.Add("Ctrl");
            if ((k & Keys.Shift) != 0) parts.Add("Shift");
            if ((k & Keys.Alt) != 0) parts.Add("Alt");
            var key = k & Keys.KeyCode;
            string name = key.ToString();
            if (key == Keys.Oemtilde) name = "`";
            else if (key >= Keys.D0 && key <= Keys.D9) name = ((int)key - (int)Keys.D0).ToString();
            parts.Add(name);
            return string.Join(" + ", parts);
        }

        /// <summary>处理快捷键；返回 true 表示已消费。</summary>
        public bool TryHandle(Keys keyData)
        {
            if (_viewMode != null && keyData == Keys.Escape && _viewMode.TryHandleEscape())
                return true;

            // F11：专注模式 ↔ 标准视图
            if (_viewMode != null && keyData == Keys.F11)
            {
                _viewMode.ToggleFocus();
                return true;
            }

            // 标签导航（Windows Terminal 惯例）：Ctrl+Tab 循环、Ctrl+Alt+数字 直达；
            // 不占用普通 Ctrl 组合，shell readline 不受影响
            if (keyData == (Keys.Control | Keys.Tab))
            {
                _tabs?.CycleTab(1);
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                _tabs?.CycleTab(-1);
                return true;
            }
            if ((keyData & (Keys.Control | Keys.Alt)) == (Keys.Control | Keys.Alt))
            {
                var digit = keyData & Keys.KeyCode;
                if (digit >= Keys.D1 && digit <= Keys.D9)
                {
                    _tabs?.ActivateTabIndex((int)digit - (int)Keys.D1);
                    return true;
                }
            }

            // UI 快捷键一律 Ctrl+Shift+字母：普通 Ctrl 组合留给 shell readline
            // （Ctrl+R 反向搜索 / Ctrl+W 删词 / Ctrl+F 前进字符 / Ctrl+P 上一条历史）
            // 平表分派（审计 F7：注册即路由；K 项 Run=null 仅作帮助展示，实际路由在 MainForm）
            foreach (var e in _registry)
            {
                if (e.Combo == keyData)
                {
                    if (e.Run != null) e.Run();
                    return true;
                }
            }

            // Alt+8：tmux 键组与全部之间快速切换（与菜单 tmux 快捷面板同效，经回调走 BottomBarPanel.ToggleTmuxGroup）
            if (keyData == (Keys.Alt | Keys.D8))
            {
                try { if (_toggleTmuxGroup != null) _toggleTmuxGroup(); }
                catch { }
                return true;
            }

            return false;
        }
    }
}
