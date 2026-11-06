using System;

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>
    /// 终端应用请求的桌面通知：OSC 9（iTerm2 简写）、OSC 777（urxvt 风格 notify）、
    /// kitty OSC 99（分片 title/body）。条目标题/正文都是 UTF-8，由预处理器自己解。
    /// </summary>
    public sealed class TerminalNotification
    {
        /// <summary>标题（OSC 9 无标题时为空串）。</summary>
        public string Title { get; set; }

        /// <summary>正文。</summary>
        public string Body { get; set; }

        /// <summary>紧急度：info（默认）/ warn / error，来自 kitty 99 的 u= 参数。</summary>
        public string Urgency { get; set; }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Title)) return Body ?? "";
            if (string.IsNullOrEmpty(Body)) return Title;
            return Title + "：" + Body;
        }
    }
}
