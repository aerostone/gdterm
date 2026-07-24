using System;

namespace Gdterm.Terminal.Models
{
    /// <summary>
    /// 终端输出事件参数
    /// </summary>
    public class TerminalOutputEventArgs : EventArgs
    {
        /// <summary>
        /// 输出文本（含 ANSI 转义序列或已解码文本）
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 输出时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }
    }
}
