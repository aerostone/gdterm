using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// TerminalControl 键盘处理——partial class，与主文件共享私有字段。
    /// </summary>
    public partial class TerminalControl
    {
        private void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            if (_session?.IsConnected != true)
            {
                try { DiagLog.Info("TerminalControl.OnKeyPress.GuardDrop",
                    "connected=" + (_session != null && _session.IsConnected) +
                    " backend=" + ((_session as LocalTerminalSession)?.BackendName ?? "non-local") +
                    " keyCharCode=0x" + (e.KeyChar == '\0' ? "0" : ((int)e.KeyChar).ToString("X"))); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                return;
            }

            if (!char.IsControl(e.KeyChar))
            {
                var mods = ModifierKeys;

                // Ctrl+Space：xterm 发 NUL（readline set-mark），不能被当成普通空格送出去
                if (e.KeyChar == ' ' && (mods & Keys.Control) == Keys.Control
                    && (mods & Keys.Alt) == 0)
                {
                    SafeSend(XtermKeys.Null);
                    e.Handled = true;
                    return;
                }

                // Alt+可打印键 = Meta（xterm 发 ESC 前缀）：readline 的 M-b/M-f/M-d、tmux 的 M-方向。
                // 用 KeyPress 的字符而不是 KeyCode 反查，避免非 US 布局下猜错字符。
                if ((mods & Keys.Alt) == Keys.Alt && (mods & Keys.Control) != Keys.Control)
                {
                    SafeSend(XtermKeys.Escape + e.KeyChar);
                    _commandLine.Append(e.KeyChar);
                    e.Handled = true;
                    return;
                }

                _commandLine.Append(e.KeyChar);

                if (_cellRenderer != null)
                {
                    // TUI：优先 VtNetCore KeyPressed；失败则明文
                    var keyName = e.KeyChar.ToString();
                    bool handled = false;
                    try { handled = _cellRenderer.TryKeyPressed(keyName, false, false); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                    if (!handled)
                        SafeSend(keyName);
                }
                else if (UseLocalLineBuffer)
                {
                    try { _renderer?.Write(e.KeyChar.ToString()); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                }
                else
                {
                    SafeSend(e.KeyChar.ToString());
                }
                e.Handled = true;
                try { UpdateCompletionPopup(); }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
            }
            else
            {
                // 控制码（0x01..0x1F / 0x7F）直通主机。
                //
                // 修复：原实现只在 KeyDown 里处理 Ctrl+C 一个组合，OnKeyPress 又整段跳过控制字符，
                // 于是除 Ctrl+C 外所有 Ctrl 组合（tmux 前缀 C-b、readline 的 C-a/C-e/C-r/C-w/C-k、
                // vim 的 C-f/C-b、less 的 C-v…）全留在本地——tmux 根本唤不出来。
                //
                // KeyChar 本身就是 xterm 在线上发的那个字节。本地已消费的键（Enter/Tab/BS/Esc/
                // 方向键/特殊键）在 KeyDown 里已 SuppressKeyPress，其 WM_CHAR 不会到这里，所以不会重复发。
                var mods = ModifierKeys;
                if ((mods & Keys.Control) != Keys.Control) return;

                // xterm modifyOtherKeys（CSI > 4 ; n m）开着时，Ctrl+字母改发 CSI 27;mod;code ~，
                // 否则 tmux/vim 无法区分 C-a / C-S-a / C-M-a（扩展键协议的前置能力）。
                var seq = TryModifiedKeysSequence(e.KeyChar, mods);
                if (seq == null)
                {
                    seq = XtermKeys.ControlChar(e.KeyChar) ?? e.KeyChar.ToString();
                    if ((mods & Keys.Alt) == Keys.Alt) seq = XtermKeys.Escape + seq;   // Ctrl+Alt = Meta
                }
                SafeSend(seq);
                e.Handled = true;
            }
        }

        /// <summary>
        /// modifyOtherKeys 生效时把 Ctrl+可打印键编码成 CSI 27 ; mod ; code ~（xterm 扩展键协议）。
        /// 返回 null 表示未启用或该键不在本函数职责内，走原来的控制码路径。
        /// </summary>
        private string TryModifiedKeysSequence(char keyChar, Keys mods)
        {
            try
            {
                var engine = _cellRenderer?.Engine;
                if (engine == null || engine.ModifyOtherKeys <= 0) return null;

                var code = XtermKeys.BaseKeyCode(keyChar, (mods & Keys.Shift) == Keys.Shift);
                if (code <= 0) return null;
                return XtermKeys.ModifiedKey(code, true,
                    (mods & Keys.Shift) == Keys.Shift, (mods & Keys.Alt) == Keys.Alt);
            }
            catch (System.Exception exSwallowed)
            {
                try { DiagLog.Swallowed("TerminalControl.ModifiedKeys", exSwallowed); } catch { }
                return null;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_session?.IsConnected != true)
            {
                if (e.KeyCode != Keys.ShiftKey && e.KeyCode != Keys.ControlKey && e.KeyCode != Keys.Menu)
                {
                    try { DiagLog.Info("TerminalControl.OnKeyDown.GuardDrop",
                        "kc=" + e.KeyCode + " connected=" + (_session != null && _session.IsConnected) +
                        " backend=" + ((_session as LocalTerminalSession)?.BackendName ?? "non-local")); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                }
                return;
            }

            var result = _keyResolver.Resolve(e);
            if (result != null)
            {
                e.Handled = true;
                switch (result.Type)
                {
                    case SendType.Sequence:
                    case SendType.Text:
                        // 旁路 #2（审计发现）：快捷键绑定能把任意文本/序列（含回车）直接灌进主机，
                        // 此前完全绕过危险命令闸门——一条“删除”绑定就是后门。这里补上判定。
                        if (!ConfirmDangerousCommand(result.Value, InputSource.KeyBinding)) break;
                        SafeSend(result.Value);
                        break;
                    case SendType.Action:
                        ActionRequested?.Invoke(this, new KeyBindingActionEventArgs(result.Value, result.Binding));
                        break;
                }
                return;
            }

            try
            {
                // 方向键等特殊键：序列由 XtermKeys 产出（DLL 内置表有 Enter=LF、Home=ESC[1~ 等偏差）
                if (TrySpecialKey(e))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;   // 挡住 WM_CHAR，避免同一个键再被 OnKeyPress 发一次
                    return;
                }

                switch (e.KeyCode)
                {
                    case Keys.Enter:
                    {
                        // 弹窗可见时 Enter=确认选中（不发 \r）
                        try
                        {
                            if (IsCompletionPopupShown)
                            {
                                if (ConfirmCompletionPopup()) { e.Handled = true; break; }
                                HideCompletionPopup();
                            }
                        }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        e.SuppressKeyPress = true;
                        var cmd = _commandLine.ToString();
                        if (UseLocalLineBuffer)
                        {
                            if (!ConfirmIfDangerous(cmd))
                            {
                                ClearLocalLine(eraseDisplay: true);
                                e.Handled = true;
                                return;
                            }
                            _commandLine.Clear();
                            if (cmd.Length > 0)
                                SafeSend(cmd);
                            SafeSend(XtermKeys.Enter);
                        }
                        else if (UseVtCellDangerGate)
                        {
                            // 字符已直通远端；危险则 Ctrl+C 中止
                            _commandLine.Clear();
                            if (!ConfirmIfDangerous(cmd))
                            {
                                SafeSend("\x03");
                                e.Handled = true;
                                try { HideCompletionPopup(); }
                                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                                return;
                            }
                            // 库内置键表把 Enter 映射成 LF(0x0A)，远端 readline 不认（ICRNL 只转 CR）→ 这里自己发 CR
                            SafeSend(XtermKeys.Enter);
                        }
                        else
                        {
                            _commandLine.Clear();
                            if (!ConfirmIfDangerous(cmd))
                            {
                                SafeSend("\x03");
                                e.Handled = true;
                                try { HideCompletionPopup(); }
                                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                                return;
                            }
                            SafeSend(XtermKeys.Enter);
                        }
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            try { _auditLogger?.LogCommand(_config?.Id ?? "", cmd); }
                            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        }
                        e.Handled = true;
                        try { HideCompletionPopup(); _commandLine.Clear(); }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        break;
                    }
                    case Keys.Back:
                        if (_commandLine.Length > 0)
                            _commandLine.Length--;
                        if (UseLocalLineBuffer)
                        {
                            try { _renderer?.Write("\b \b"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        }
                        else
                        {
                            // Ctrl+BS = 0x08，普通 BS = 0x7F（与 xterm kbs=\177 一致）
                            SafeSend(XtermKeys.For("Back", e.Control, e.Shift, e.Alt));
                        }
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        try { UpdateCompletionPopup(); }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        break;
                    case Keys.Tab:
                        // 弹窗确认优先，其次本地首候选；无候选走原直通过程
                        try
                        {
                            if (IsCompletionPopupShown)
                            {
                                if (ConfirmCompletionPopup()) { e.Handled = true; break; }
                            }
                            else if (TryCompleteOnTab()) { e.Handled = true; break; }
                        }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        if (UseLocalLineBuffer && _commandLine.Length > 0)
                        {
                            var partial = _commandLine.ToString();
                            _commandLine.Clear();
                            SafeSend(partial);
                        }
                        // Shift+Tab = CSI Z（反向补全），Ctrl+Tab = HT
                        SafeSend(XtermKeys.For("Tab", e.Control, e.Shift, e.Alt));
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                    case Keys.Escape:
                        // 弹窗可见时 Esc=关弹窗并吃掉；否则保持原行为（专注模式退出）
                        try
                        {
                            if (IsCompletionPopupShown)
                            {
                                HideCompletionPopup();
                                e.Handled = true;
                                break;
                            }
                        }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        // 修复：以前这里直接把 Esc 留给 MainForm 的专注模式退出，导致 vim/tmux/less
                        // 收不到 ESC。MainForm 若真要拿 Esc（专注模式），会在 ProcessCmdKey 先吃掉，
                        // 根本走不到这里——所以这里必须发给主机。
                        SafeSend(XtermKeys.Escape);
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                    case Keys.Up:
                        try
                        {
                            if (IsCompletionPopupShown)
                            {
                                CompletionPopupMove(-1);
                                e.Handled = true;
                                break;
                            }
                        }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
                        SafeSend("\x1b[A");
                        e.Handled = true;
                        break;
                    case Keys.Down:
                        try
                        {
                            if (IsCompletionPopupShown)
                            {
                                CompletionPopupMove(1);
                                e.Handled = true;
                                break;
                            }
                        }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
                        ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
                        SafeSend("\x1b[B");
                        e.Handled = true;
                        break;
                    case Keys.Right:
                        SafeSend("\x1b[C");
                        e.Handled = true;
                        break;
                    case Keys.Left:
                        SafeSend("\x1b[D");
                        e.Handled = true;
                        break;
                    case Keys.Home:
                        SafeSend("\x1b[H");
                        e.Handled = true;
                        break;
                    case Keys.End:
                        SafeSend("\x1b[F");
                        e.Handled = true;
                        break;
                    case Keys.Delete:
                        SafeSend("\x1b[3~");
                        e.Handled = true;
                        break;
                    case Keys.PageUp:
                        SafeSend("\x1b[5~");
                        e.Handled = true;
                        break;
                    case Keys.PageDown:
                        SafeSend("\x1b[6~");
                        e.Handled = true;
                        break;
                }

                if (e.Control && e.KeyCode == Keys.C && !_keyResolver.HasBinding(e))
                {
                    // 有选区时 Ctrl+C 先复制，不发中断（SecureCRT/Windows Terminal 习惯）
                    try
                    {
                        var sel = _cellRenderer != null ? _cellRenderer.GetSelection() : null;
                        if (_cellRenderer != null && !string.IsNullOrEmpty(sel))
                        {
                            Clipboard.SetText(sel);
                            // 选中后复制可选自动清选区；这里保留高亮，用户左键点击会清除
                            e.Handled = true;
                            return;
                        }
                    }
                    catch (Exception ex) { DiagLog.Swallowed("TerminalControl.CopySel", ex); }
                    ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
                    SafeSend("\x03");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }

                if (!_keyResolver.HasBinding(e))
                {
                    // Ctrl+Shift+V / Shift+Insert：终端式粘贴（走 bracketed paste 判断）
                    if ((e.Control && e.Shift && e.KeyCode == Keys.V) ||
                        (e.Shift && e.KeyCode == Keys.Insert))
                    {
                        PasteFromClipboard();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
        }

        /// <summary>
        /// 特殊键 → 主机。
        ///
        /// 序列由 <see cref="XtermKeys"/>（TERM=xterm-256color 的 terminfo）产出，不再以
        /// lib\VtNetCore.dll 1.0.30 内置键表为准——那张表把 Enter 映射成 LF、Escape 映射成双 ESC、
        /// Home/End 用 ESC[1~/ESC[4~、F1-F4 用 ESC[11~..，且没有修饰键组合（Ctrl+Up 也发 ESC OA）。
        ///
        /// 唯一仍走引擎的是“无修饰键的方向键”：它要按 DECCKM 应用光标键模式切换 ESC[A / ESCOA。
        /// </summary>
        private bool TrySpecialKey(KeyEventArgs e)
        {
            string name;
            switch (e.KeyCode)
            {
                case Keys.Up: name = "Up"; break;
                case Keys.Down: name = "Down"; break;
                case Keys.Left: name = "Left"; break;
                case Keys.Right: name = "Right"; break;
                case Keys.Home: name = "Home"; break;
                case Keys.End: name = "End"; break;
                case Keys.Insert: name = "Insert"; break;
                case Keys.Delete: name = "Delete"; break;
                case Keys.PageUp: name = "PageUp"; break;
                case Keys.PageDown: name = "PageDown"; break;
                case Keys.F1: name = "F1"; break;
                case Keys.F2: name = "F2"; break;
                case Keys.F3: name = "F3"; break;
                case Keys.F4: name = "F4"; break;
                case Keys.F5: name = "F5"; break;
                case Keys.F6: name = "F6"; break;
                case Keys.F7: name = "F7"; break;
                case Keys.F8: name = "F8"; break;
                case Keys.F9: name = "F9"; break;
                case Keys.F10: name = "F10"; break;
                case Keys.F11: name = "F11"; break;
                case Keys.F12: name = "F12"; break;
                case Keys.F13: name = "F13"; break;
                case Keys.F14: name = "F14"; break;
                case Keys.F15: name = "F15"; break;
                case Keys.F16: name = "F16"; break;
                case Keys.F17: name = "F17"; break;
                case Keys.F18: name = "F18"; break;
                case Keys.F19: name = "F19"; break;
                case Keys.F20: name = "F20"; break;
                case Keys.F21: name = "F21"; break;
                case Keys.F22: name = "F22"; break;
                case Keys.F23: name = "F23"; break;
                case Keys.F24: name = "F24"; break;
                default: return false;
            }

            // 补全弹窗可见时上下键用于选候选，不发给主机
            if (IsCompletionPopupShown && (name == "Up" || name == "Down"))
                return false;

            bool plainArrow = !e.Control && !e.Shift && !e.Alt && XtermKeys.IsArrowKey(name);

            // 清空本地命令缓冲（历史导航等）
            if (name == "Up" || name == "Down")
                ClearLocalLine(eraseDisplay: false);

            if (_cellRenderer != null && plainArrow)
            {
                try
                {
                    if (_cellRenderer.TryKeyPressed(name, false, false))
                        return true;   // 引擎按 DECCKM 出 ESC[A 或 ESCOA
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TerminalControl.Keyboard", exSwallowed); } catch { } }
            }

            var seq = XtermKeys.For(name, e.Control, e.Shift, e.Alt);
            if (string.IsNullOrEmpty(seq)) return false;
            SafeSend(seq);
            return true;
        }
}
}
