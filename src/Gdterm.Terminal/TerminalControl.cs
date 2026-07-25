using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Terminal.Models;
using Gdterm.Terminal.Rendering;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端控件——WinForms UserControl，承载终端渲染器并管理会话生命周期
    /// 支持自定义快捷键绑定
    /// </summary>
    public class TerminalControl : UserControl
    {
        private ITerminalSession _session;
        private readonly IRenderer _renderer;
        private readonly TerminalKeyBindingResolver _keyResolver;
        private bool _sessionAttached;

        /// <summary>
        /// 当前绑定的终端会话
        /// </summary>
        public ITerminalSession Session => _session;

        /// <summary>
        /// 快捷键解析器——可用于外部设置绑定
        /// </summary>
        public TerminalKeyBindingResolver KeyResolver => _keyResolver;

        /// <summary>
        /// 当快捷键动作触发时（复制/粘贴/清除等）
        /// </summary>
        public event EventHandler<KeyBindingActionEventArgs> ActionRequested;

        public TerminalControl()
        {
            _renderer = new TerminalRenderer();
            _keyResolver = new TerminalKeyBindingResolver();
            InitializeControl();
        }

        public TerminalControl(IRenderer renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _keyResolver = new TerminalKeyBindingResolver();
            InitializeControl();
        }

        private void InitializeControl()
        {
            var renderControl = _renderer.GetControl();
            renderControl.Dock = DockStyle.Fill;
            Controls.Add(renderControl);

            // 捕获键盘输入
            KeyPress += OnKeyPress;
            KeyDown += OnKeyDown;

            // 确保控件可以获得焦点
            SetStyle(ControlStyles.Selectable, true);
        }

        /// <summary>
        /// 绑定终端会话——连接输出事件，开始渲染
        /// </summary>
        public void AttachSession(ITerminalSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            // 先分离旧会话
            if (_sessionAttached)
            {
                DetachSession();
            }

            _session = session;
            _session.OutputReceived += OnSessionOutput;
            _sessionAttached = true;

            _renderer.Clear();
        }

        /// <summary>
        /// 分离终端会话——断开输出事件
        /// </summary>
        public void DetachSession()
        {
            if (_session != null)
            {
                _session.OutputReceived -= OnSessionOutput;
            }

            _session = null;
            _sessionAttached = false;
        }

        /// <summary>
        /// 获取当前选中的文本
        /// </summary>
        public string GetSelection()
        {
            return _renderer.GetSelection();
        }

        /// <summary>
        /// 清除终端内容
        /// </summary>
        public void ClearTerminal()
        {
            _renderer.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachSession();
            }
            base.Dispose(disposing);
        }

        private void OnSessionOutput(object sender, TerminalOutputEventArgs e)
        {
            // 在 UI 线程上渲染
            if (InvokeRequired)
            {
                try
                {
                    Invoke(new Action(() => _renderer.Write(e.Text)));
                }
                catch (ObjectDisposedException)
                {
                    // 控件已销毁，忽略
                }
            }
            else
            {
                _renderer.Write(e.Text);
            }
        }

        private void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            if (_session?.IsConnected == true)
            {
                try
                {
                    _session.SendInput(e.KeyChar.ToString());
                    e.Handled = true;
                }
                catch
                {
                    // 发送失败，不中断 UI
                }
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_session?.IsConnected != true) return;

            // ── 1. 先检查快捷键绑定 ──
            var result = _keyResolver.Resolve(e);
            if (result != null)
            {
                e.Handled = true;
                switch (result.Type)
                {
                    case SendType.Sequence:
                        // 转义序列 → 直接发送到终端
                        try { _session.SendInput(result.Value); } catch { }
                        break;
                    case SendType.Text:
                        // 字面文本 → 发送到终端
                        try { _session.SendInput(result.Value); } catch { }
                        break;
                    case SendType.Action:
                        // 内置动作 → 通知外部处理
                        ActionRequested?.Invoke(this, new KeyBindingActionEventArgs(result.Value, result.Binding));
                        break;
                }
                return;
            }

            // ── 2. 没有匹配的绑定 → 默认终端按键处理 ──
            try
            {
                switch (e.KeyCode)
                {
                    case Keys.Enter:
                        _session.SendInput("\r");
                        e.Handled = true;
                        break;
                    case Keys.Back:
                        _session.SendInput("\b");
                        e.Handled = true;
                        break;
                    case Keys.Tab:
                        _session.SendInput("\t");
                        e.Handled = true;
                        break;
                    case Keys.Escape:
                        _session.SendInput("\x1b");
                        e.Handled = true;
                        break;
                    case Keys.Up:
                        _session.SendInput("\x1b[A");
                        e.Handled = true;
                        break;
                    case Keys.Down:
                        _session.SendInput("\x1b[B");
                        e.Handled = true;
                        break;
                    case Keys.Right:
                        _session.SendInput("\x1b[C");
                        e.Handled = true;
                        break;
                    case Keys.Left:
                        _session.SendInput("\x1b[D");
                        e.Handled = true;
                        break;
                    case Keys.Home:
                        _session.SendInput("\x1b[H");
                        e.Handled = true;
                        break;
                    case Keys.End:
                        _session.SendInput("\x1b[F");
                        e.Handled = true;
                        break;
                    case Keys.Delete:
                        _session.SendInput("\x1b[3~");
                        e.Handled = true;
                        break;
                    case Keys.PageUp:
                        _session.SendInput("\x1b[5~");
                        e.Handled = true;
                        break;
                    case Keys.PageDown:
                        _session.SendInput("\x1b[6~");
                        e.Handled = true;
                        break;
                }

                // Ctrl+C — 仅在没有其他 Ctrl 绑定时作为默认中断
                if (e.Control && e.KeyCode == Keys.C && !_keyResolver.HasBinding(e))
                {
                    _session.SendInput("\x03");
                    e.Handled = true;
                }
            }
            catch
            {
                // 发送失败，不中断 UI
            }
        }
    }

    /// <summary>
    /// 快捷键动作事件参数
    /// </summary>
    public class KeyBindingActionEventArgs : EventArgs
    {
        public string Action { get; private set; }
        public TerminalKeyBinding Binding { get; private set; }

        public KeyBindingActionEventArgs(string action, TerminalKeyBinding binding)
        {
            Action = action;
            Binding = binding;
        }
    }
}
