using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Gdterm.UI.Hotkeys
{
    /// <summary>
    /// 全局热键管理器——使用 Win32 API 注册系统级热键，支持一键呼出/隐藏窗口
    /// 类似 Quake 终端风格
    /// </summary>
    public class GlobalHotkeyManager : IDisposable
    {
        // Win32 API 常量
        private const int WM_HOTKEY = 0x0312;
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_SHIFT = 0x0004;
        private const int MOD_WIN = 0x0008;
        private const int MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly Form _targetForm;
        private readonly IntPtr _hWnd;
        private int _nextId = 1;
        private bool _disposed;

        /// <summary>
        /// 热键按下时触发
        /// </summary>
        public event EventHandler<HotkeyPressedEventArgs> HotkeyPressed;

        public GlobalHotkeyManager(Form targetForm)
        {
            _targetForm = targetForm ?? throw new ArgumentNullException(nameof(targetForm));
            _hWnd = targetForm.Handle;

            // 安装消息过滤器
            Application.AddMessageFilter(new HotkeyMessageFilter(this));
        }

        /// <summary>
        /// 注册全局热键
        /// </summary>
        /// <param name="modifiers">修饰键（Ctrl/Alt/Shift/Win）</param>
        /// <param name="key">按键</param>
        /// <returns>热键 ID，用于注销</returns>
        public int Register(HotkeyModifiers modifiers, Keys key)
        {
            int id = _nextId++;
            uint mod = (uint)modifiers | MOD_NOREPEAT;

            if (!RegisterHotKey(_hWnd, id, mod, (uint)key))
            {
                throw new InvalidOperationException(
                    $"注册热键失败: {modifiers}+{key}，可能已被其他程序占用");
            }

            return id;
        }

        /// <summary>
        /// 注销全局热键
        /// </summary>
        public void Unregister(int id)
        {
            UnregisterHotKey(_hWnd, id);
        }

        /// <summary>
        /// 注销所有热键
        /// </summary>
        public void UnregisterAll()
        {
            for (int i = 1; i < _nextId; i++)
            {
                try { UnregisterHotKey(_hWnd, i); } catch { }
            }
        }

        private void OnHotkeyPressed(int id)
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(id));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            Application.RemoveMessageFilter(new HotkeyMessageFilter(this));
        }

        /// <summary>
        /// 消息过滤器——拦截 WM_HOTKEY 消息
        /// </summary>
        private class HotkeyMessageFilter : IMessageFilter
        {
            private readonly GlobalHotkeyManager _manager;

            public HotkeyMessageFilter(GlobalHotkeyManager manager)
            {
                _manager = manager;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    _manager.OnHotkeyPressed(id);
                    return true;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// 热键修饰键
    /// </summary>
    [Flags]
    public enum HotkeyModifiers
    {
        Alt = 1,
        Control = 2,
        Shift = 4,
        Win = 8
    }

    /// <summary>
    /// 热键按下事件参数
    /// </summary>
    public class HotkeyPressedEventArgs : EventArgs
    {
        public int HotkeyId { get; }

        public HotkeyPressedEventArgs(int hotkeyId)
        {
            HotkeyId = hotkeyId;
        }
    }
}
