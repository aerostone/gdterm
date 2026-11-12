using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// .NET Framework 4.6.2 / Win7 兼容辅助：
    /// PlaceholderText（EM_SETCUEBANNER）在 net462 没有托管 API。
    /// 进度条状态色曾经也靠这里（PBM_SETSTATE），现已换成 ThemedProgress 控件。
    /// </summary>
    internal static class WinFormsCompat
    {
        private const int EM_SETCUEBANNER = 0x1501;

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
    }
}
