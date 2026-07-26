using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// .NET Framework 4.6.2 / Win7 兼容辅助：
    /// PlaceholderText、ProgressBar 状态色等在 net462 没有托管 API。
    /// </summary>
    internal static class WinFormsCompat
    {
        private const int EM_SETCUEBANNER = 0x1501;
        private const int PBM_SETSTATE = 0x0410;
        public const int ProgressStateNormal = 1; // green
        public const int ProgressStateError = 2;  // red
        public const int ProgressStatePaused = 3; // yellow

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>设置 TextBox 灰色提示文字（EM_SETCUEBANNER）。</summary>
        public static void SetCueBanner(TextBox box, string text)
        {
            if (box == null) return;
            if (!box.IsHandleCreated)
            {
                EventHandler onCreate = null;
                onCreate = (s, e) =>
                {
                    box.HandleCreated -= onCreate;
                    try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text ?? string.Empty); }
                    catch { /* ignore */ }
                };
                box.HandleCreated += onCreate;
                return;
            }

            try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text ?? string.Empty); }
            catch { /* ignore */ }
        }

        /// <summary>设置 ProgressBar Vista+ 状态色（PBM_SETSTATE）。失败则忽略。</summary>
        public static void SetProgressState(ProgressBar bar, int state)
        {
            if (bar == null) return;
            if (!bar.IsHandleCreated)
            {
                EventHandler onCreate = null;
                onCreate = (s, e) =>
                {
                    bar.HandleCreated -= onCreate;
                    try { SendMessage(bar.Handle, PBM_SETSTATE, (IntPtr)state, IntPtr.Zero); }
                    catch { /* ignore */ }
                };
                bar.HandleCreated += onCreate;
                return;
            }

            try { SendMessage(bar.Handle, PBM_SETSTATE, (IntPtr)state, IntPtr.Zero); }
            catch { /* ignore */ }
        }
    }
}
