using System;

namespace Gdterm.Security
{
    /// <summary>
    /// 命令来源——同一条命令从哪个入口进主机，决定要不要预演、审计怎么写、豁免能不能给。
    /// 审计发现的旁路入口（粘贴 / 宏回放 / 键位绑定 / 登录脚本）全部靠这个标记落到日志里，
    /// 不标来源的拦截日志等于事故时无法回溯。
    /// </summary>
    public enum InputSource
    {
        /// <summary>手敲 + 回车</summary>
        Keyboard = 0,

        /// <summary>剪贴板粘贴（多行时会整批执行）</summary>
        Paste = 1,

        /// <summary>宏回放</summary>
        Macro = 2,

        /// <summary>快捷键绑定送出的固定文本/序列</summary>
        KeyBinding = 3,

        /// <summary>登录脚本自动发送</summary>
        LogonScript = 4,

        /// <summary>批量命令面板（一次打多个会话）</summary>
        BatchPanel = 5,

        /// <summary>多通道广播</summary>
        Broadcast = 6,

        /// <summary>AI 助手建议执行</summary>
        Ai = 7,

        /// <summary>其它程序化发送</summary>
        Programmatic = 8
    }

    /// <summary>来源的展示名（审计文案/弹窗副标题共用）。</summary>
    public static class InputSourceNames
    {
        public static string Describe(InputSource source)
        {
            switch (source)
            {
                case InputSource.Keyboard: return "键盘";
                case InputSource.Paste: return "粘贴";
                case InputSource.Macro: return "宏回放";
                case InputSource.KeyBinding: return "快捷键绑定";
                case InputSource.LogonScript: return "登录脚本";
                case InputSource.BatchPanel: return "批量命令";
                case InputSource.Broadcast: return "多通道广播";
                case InputSource.Ai: return "AI 助手";
                default: return "程序发送";
            }
        }
    }
}
