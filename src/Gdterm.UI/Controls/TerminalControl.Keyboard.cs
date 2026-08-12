using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
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
                    " keyCharCode=0x" + (e.KeyChar == '\0' ? "0" : ((int)e.KeyChar).ToString("X"))); } catch { }
                return;
            }

            if (!char.IsControl(e.KeyChar))
            {
                _commandLine.Append(e.KeyChar);

                if (_cellRenderer != null)
                {
                    // TUI：优先 VtNetCore KeyPressed；失败则明文
                    var keyName = e.KeyChar.ToString();
                    bool handled = false;
                    try { handled = _cellRenderer.TryKeyPressed(keyName, false, false); } catch { }
                    if (!handled)
                        SafeSend(keyName);
                }
                else if (UseLocalLineBuffer)
                {
                    try { _renderer?.Write(e.KeyChar.ToString()); } catch { }
                }
                else
                {
                    SafeSend(e.KeyChar.ToString());
                }
                e.Handled = true;
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
                        " backend=" + ((_session as LocalTerminalSession)?.BackendName ?? "non-local")); } catch { }
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
                // Cell 路径：方向键等走 VtNetCore（应用光标模式）
                if (_cellRenderer != null && TryCellSpecialKey(e))
                {
                    e.Handled = true;
                    return;
                }

                switch (e.KeyCode)
                {
                    case Keys.Enter:
                    {
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
                            SafeSend("\r");
                        }
                        else if (UseVtCellDangerGate)
                        {
                            // 字符已直通远端；危险则 Ctrl+C 中止
                            _commandLine.Clear();
                            if (!ConfirmIfDangerous(cmd))
                            {
                                SafeSend("\x03");
                                e.Handled = true;
                                return;
                            }
                            if (_cellRenderer == null || !_cellRenderer.TryKeyPressed("Enter", e.Control, e.Shift))
                                SafeSend("\r");
                        }
                        else
                        {
                            _commandLine.Clear();
                            if (!ConfirmIfDangerous(cmd))
                            {
                                SafeSend("\x03");
                                e.Handled = true;
                                return;
                            }
                            SafeSend("\r");
                        }
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            try { _auditLogger?.LogCommand(_config?.Id ?? "", cmd); }
                            catch { }
                        }
                        e.Handled = true;
                        break;
                    }
                    case Keys.Back:
                        if (_commandLine.Length > 0)
                            _commandLine.Length--;
                        if (UseLocalLineBuffer)
                        {
                            try { _renderer?.Write("\b \b"); } catch { }
                        }
                        else if (_cellRenderer != null)
                        {
                            if (!_cellRenderer.TryKeyPressed("Back", e.Control, e.Shift))
                                SafeSend("\b");
                        }
                        else
                        {
                            SafeSend("\b");
                        }
                        e.Handled = true;
                        break;
                    case Keys.Tab:
                        if (UseLocalLineBuffer && _commandLine.Length > 0)
                        {
                            var partial = _commandLine.ToString();
                            _commandLine.Clear();
                            SafeSend(partial);
                        }
                        if (_cellRenderer != null)
                        {
                            if (!_cellRenderer.TryKeyPressed("Tab", e.Control, e.Shift))
                                SafeSend("\t");
                        }
                        else
                        {
                            SafeSend("\t");
                        }
                        e.Handled = true;
                        break;
                    case Keys.Escape:
                        // 不在此消费 Esc：交给 MainForm ProcessCmdKey 退出专注模式
                        // （终端应用如 vim 仍可用其它快捷键；专注模式优先可退出）
                        break;
                    case Keys.Up:
                        ClearLocalLine(eraseDisplay: UseLocalLineBuffer);
                        SafeSend("\x1b[A");
                        e.Handled = true;
                        break;
                    case Keys.Down:
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
                }
            }
            catch { }
        }

        /// <summary>VtNetCore 键名映射；成功则已通过 SendToHost 发往会话。</summary>
        private bool TryCellSpecialKey(KeyEventArgs e)
        {
            if (_cellRenderer == null) return false;
            string name = null;
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
                default: return false;
            }

            // 清空本地命令缓冲（历史导航等）
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                ClearLocalLine(eraseDisplay: false);

            try
            {
                if (_cellRenderer.TryKeyPressed(name, e.Control, e.Shift))
                    return true;
            }
            catch { }
            return false;
        }
}
}

