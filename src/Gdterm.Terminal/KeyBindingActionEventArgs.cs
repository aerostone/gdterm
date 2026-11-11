using System;
using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 快捷键动作事件参数——解析器判定本次按键命中"动作型"绑定时，
    /// 携带动作别名与命中的绑定，由 UI 侧执行（见 TerminalControl.ActionRequested）。
    ///
    /// 2026-11-11 从渲染层旧 TerminalControl.cs 里取出：那个类本身是死码
    /// （只被同文件的 TerminalRenderer 引用），但**它尾部定义的这一个类型是活的**
    /// （Gdterm.UI.Controls.TerminalControl.ActionRequested 用到），删除时随之消失，
    /// CI 立刻 CS0246。故事后归位到解析器的同一命名空间：动作结果是解析器的输出。
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
