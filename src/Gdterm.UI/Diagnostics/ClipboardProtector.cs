using System;
using System.Threading;
using System.Windows.Forms;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// 剪贴板敏感内容保护：复制后在 TTL 到期时若内容未变则清空。
    /// 用户若已粘贴并改写剪贴板则不误清。
    /// WinForms Timer 在 UI 线程回调，避免跨线程 Clipboard 访问。
    /// </summary>
    internal static class ClipboardProtector
    {
        private static readonly object _lock = new object();
        private static System.Windows.Forms.Timer _timer;
        private static string _pending;
        private static int _generation;

        /// <summary>默认 TTL：30 秒</summary>
        public static int DefaultTtlMs { get; set; } = 30000;

        /// <summary>
        /// 写入剪贴板并在 ttlMs 后尝试清空（仅当仍为同一内容时）。
        /// </summary>
        public static void SetTextWithTtl(string text, int ttlMs = -1)
        {
            if (text == null) text = string.Empty;
            if (ttlMs < 0) ttlMs = DefaultTtlMs;

            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // 剪贴板被占用时忽略
                return;
            }

            lock (_lock)
            {
                _pending = text;
                var gen = Interlocked.Increment(ref _generation);

                if (_timer == null)
                {
                    _timer = new System.Windows.Forms.Timer();
                    _timer.Tick += OnTick;
                }

                _timer.Stop();
                _timer.Tag = gen;
                _timer.Interval = Math.Max(1000, ttlMs);
                _timer.Start();
            }
        }

        /// <summary>立即取消挂起的清空任务（不改剪贴板）。</summary>
        public static void CancelPendingClear()
        {
            lock (_lock)
            {
                Interlocked.Increment(ref _generation);
                if (_timer != null)
                    _timer.Stop();
                _pending = null;
            }
        }

        private static void OnTick(object sender, EventArgs e)
        {
            int gen;
            string expected;
            lock (_lock)
            {
                if (_timer != null)
                    _timer.Stop();
                gen = _timer != null && _timer.Tag is int ? (int)_timer.Tag : -1;
                expected = _pending;
            }

            if (gen < 0 || expected == null)
                return;
            if (gen != Volatile.Read(ref _generation))
                return;

            try
            {
                if (Clipboard.ContainsText())
                {
                    var current = Clipboard.GetText();
                    if (string.Equals(current, expected, StringComparison.Ordinal))
                        Clipboard.Clear();
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                lock (_lock)
                {
                    if (gen == _generation)
                        _pending = null;
                }
            }
        }
    }
}
