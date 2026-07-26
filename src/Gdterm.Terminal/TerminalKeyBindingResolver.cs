using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端快捷键解析器——将按键事件与活动绑定匹配，返回要发送的内容或动作
    /// </summary>
    public class TerminalKeyBindingResolver
    {
        private Dictionary<string, TerminalKeyBinding> _bindingMap;
        private List<TerminalKeyBinding> _bindings;

        /// <summary>更新活动绑定列表</summary>
        public void LoadBindings(List<TerminalKeyBinding> bindings)
        {
            _bindings = bindings ?? new List<TerminalKeyBinding>();
            _bindingMap = new Dictionary<string, TerminalKeyBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in _bindings)
            {
                var combo = b.GetKeyCombo();
                // 第一个匹配的优先（预设绑定在前，自定义在后）
                if (!_bindingMap.ContainsKey(combo))
                    _bindingMap[combo] = b;
            }
        }

        /// <summary>解析按键事件</summary>
        /// <returns>解析结果——如果匹配到绑定则返回结果，否则返回 null 表示按键应直接发送到终端</returns>
        public KeyResolveResult Resolve(KeyEventArgs e)
        {
            if (_bindingMap == null || _bindingMap.Count == 0) return null;

            // 构建当前按键组合
            var combo = BuildCombo(e);
            if (combo == null) return null;

            TerminalKeyBinding binding;
            if (_bindingMap.TryGetValue(combo, out binding))
            {
                return new KeyResolveResult
                {
                    Binding = binding,
                    Type = binding.Type,
                    Value = binding.Value
                };
            }

            return null;
        }

        /// <summary>检查是否有绑定匹配此按键（不消费事件）</summary>
        public bool HasBinding(KeyEventArgs e)
        {
            if (_bindingMap == null || _bindingMap.Count == 0) return false;
            var combo = BuildCombo(e);
            return combo != null && _bindingMap.ContainsKey(combo);
        }

        private static string BuildCombo(KeyEventArgs e)
        {
            // 只处理有修饰键的组合
            if (!e.Control && !e.Alt && !e.Shift) return null;

            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Alt) parts.Add("Alt");
            if (e.Shift) parts.Add("Shift");

            // 获取键名
            string keyName = GetKeyName(e.KeyCode);
            if (keyName == null) return null;

            parts.Add(keyName);
            return string.Join("+", parts);
        }

        private static string GetKeyName(Keys keyCode)
        {
            // 去掉修饰键标志
            keyCode = keyCode & ~Keys.Modifiers;

            switch (keyCode)
            {
                // 字母键
                case Keys.A: return "A";
                case Keys.B: return "B";
                case Keys.C: return "C";
                case Keys.D: return "D";
                case Keys.E: return "E";
                case Keys.F: return "F";
                case Keys.G: return "G";
                case Keys.H: return "H";
                case Keys.I: return "I";
                case Keys.J: return "J";
                case Keys.K: return "K";
                case Keys.L: return "L";
                case Keys.M: return "M";
                case Keys.N: return "N";
                case Keys.O: return "O";
                case Keys.P: return "P";
                case Keys.Q: return "Q";
                case Keys.R: return "R";
                case Keys.S: return "S";
                case Keys.T: return "T";
                case Keys.U: return "U";
                case Keys.V: return "V";
                case Keys.W: return "W";
                case Keys.X: return "X";
                case Keys.Y: return "Y";
                case Keys.Z: return "Z";

                // 数字键
                case Keys.D0: return "D0";
                case Keys.D1: return "D1";
                case Keys.D2: return "D2";
                case Keys.D3: return "D3";
                case Keys.D4: return "D4";
                case Keys.D5: return "D5";
                case Keys.D6: return "D6";
                case Keys.D7: return "D7";
                case Keys.D8: return "D8";
                case Keys.D9: return "D9";

                // 功能键
                case Keys.F1: return "F1";
                case Keys.F2: return "F2";
                case Keys.F3: return "F3";
                case Keys.F4: return "F4";
                case Keys.F5: return "F5";
                case Keys.F6: return "F6";
                case Keys.F7: return "F7";
                case Keys.F8: return "F8";
                case Keys.F9: return "F9";
                case Keys.F10: return "F10";
                case Keys.F11: return "F11";
                case Keys.F12: return "F12";

                // 方向键
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";

                // 特殊键
                case Keys.Enter: return "Enter";
                case Keys.Escape: return "Escape";
                case Keys.Back: return "Back";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PageUp";
                case Keys.PageDown: return "PageDown";
                case Keys.Tab: return "Tab";
                case Keys.Space: return "Space";

                // 符号键（美式键盘）
                case Keys.OemOpenBrackets: return "OemOpenBrackets";   // [
                case Keys.OemCloseBrackets: return "OemCloseBrackets"; // ]
                case Keys.OemSemicolon: return "OemSemicolon";         // ;
                case Keys.OemQuotes: return "OemQuotes";               // '
                case Keys.Oemcomma: return "Oemcomma";                 // ,
                case Keys.OemPeriod: return "OemPeriod";               // .
                case Keys.OemQuestion: return "OemQuestion";           // /
                case Keys.Oemtilde: return "Oemtilde";                 // `
                case Keys.OemMinus: return "OemMinus";                 // -
                case Keys.Oemplus: return "Oemplus";                   // =
                case Keys.Oem5: return "Oem5";                         // \
                // Keys.Oem7 与 OemQuotes 同为 0xDE(222)，不可重复 case

                default: return keyCode.ToString();
            }
        }
    }

    /// <summary>
    /// 按键解析结果
    /// </summary>
    public class KeyResolveResult
    {
        public TerminalKeyBinding Binding { get; set; }
        public SendType Type { get; set; }
        public string Value { get; set; }
    }
}
