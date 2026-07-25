using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 终端快捷键绑定——单个按键组合 → 发送内容
    /// </summary>
    public class TerminalKeyBinding
    {
        /// <summary>绑定名称（如 "tmux: 新建窗口"）</summary>
        public string Name { get; set; }

        /// <summary>修饰键组合</summary>
        public bool Ctrl { get; set; }
        public bool Alt { get; set; }
        public bool Shift { get; set; }

        /// <summary>主键</summary>
        public string Key { get; set; }

        /// <summary>发送到终端的内容类型</summary>
        public SendType Type { get; set; }

        /// <summary>发送内容（Type=Sequence 时为转义序列，Type=Text 时为字面文本，Type=Action 时为动作名）</summary>
        public string Value { get; set; }

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>分组（如 "tmux", "screen", "custom"）</summary>
        public string Group { get; set; } = "custom";

        /// <summary>描述</summary>
        public string Description { get; set; }

        /// <summary>生成按键标识字符串（用于匹配）</summary>
        public string GetKeyCombo()
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(Key ?? "");
            return string.Join("+", parts);
        }
    }

    /// <summary>
    /// 发送类型
    /// </summary>
    public enum SendType
    {
        /// <summary>转义序列（如 \x1b[1;5A = Ctrl+Up）</summary>
        Sequence,
        /// <summary>字面文本（如 tmux 命令字符串）</summary>
        Text,
        /// <summary>内置动作（如 copy, paste, clear, scroll_up）</summary>
        Action
    }

    /// <summary>
    /// 终端快捷键配置——保存所有快捷键绑定和当前活动预设
    /// </summary>
    public class TerminalKeyBindingConfig
    {
        /// <summary>当前活动预设名</summary>
        public string ActivePreset { get; set; } = "tmux";

        /// <summary>所有预设</summary>
        public List<KeyBindingPreset> Presets { get; set; } = new List<KeyBindingPreset>();

        /// <summary>自定义绑定（追加到活动预设之后）</summary>
        public List<TerminalKeyBinding> CustomBindings { get; set; } = new List<TerminalKeyBinding>();

        /// <summary>是否拦截模式——true 时所有匹配的按键不会发送到终端，只触发绑定动作</summary>
        public bool InterceptMode { get; set; } = false;
    }

    /// <summary>
    /// 快捷键预设
    /// </summary>
    public class KeyBindingPreset
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<TerminalKeyBinding> Bindings { get; set; } = new List<TerminalKeyBinding>();
    }
}
