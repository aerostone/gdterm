using System;
using System.Windows.Forms;
using Gdterm.UI.Hotkeys;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 全局热键控制器——注册 Ctrl+` Quake 式显隐（finding-10）。
    /// </summary>
    public sealed class GlobalHotkeyController : IDisposable
    {
        private readonly Form _form;
        private GlobalHotkeyManager _manager;
        private int _toggleHotkeyId;
        private bool _disposed;

        public GlobalHotkeyController(Form form)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
        }

        public void Initialize()
        {
            try
            {
                _manager = new GlobalHotkeyManager(_form);
                _toggleHotkeyId = _manager.Register(HotkeyModifiers.Control, Keys.Oemtilde);
                _manager.HotkeyPressed += OnHotkeyPressed;
            }
            catch { }
        }

        private void OnHotkeyPressed(object sender, HotkeyPressedEventArgs e)
        {
            if (e.HotkeyId == _toggleHotkeyId)
                ToggleWindowVisibility();
        }

        public void ToggleWindowVisibility()
        {
            if (_form == null) return;
            if (_form.Visible && Form.ActiveForm == _form)
                _form.Hide();
            else
            {
                _form.Show();
                _form.WindowState = FormWindowState.Normal;
                _form.Activate();
                _form.BringToFront();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_manager != null)
                {
                    _manager.HotkeyPressed -= OnHotkeyPressed;
                    _manager.Dispose();
                }
            }
            catch { }
            _manager = null;
        }
    }
}
