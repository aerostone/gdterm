namespace Gdterm.Terminal
{
    /// <summary>
    /// xterm-256color 键序列（本层自产，不再依赖 lib\VtNetCore.dll 内置 KeyboardTranslations）。
    ///
    /// 为什么不用 VirtualTerminalController.KeyPressed 的表：
    ///   1) 内置表把 Enter/Return 映射成 LF(0x0A)。ICRNL 只把 CR 转 NL，LF 原样进 readline，
    ///      终端驱动不会把它当行结束 → bash/zsh 回车无反应。
    ///   2) 内置表 Escape 映射成 <c>ESC ESC</c> 双字节。
    ///   3) 内置表 Home=/E[1~、End=/E[4~、F1-F4=/E[11~../E[14~ 属 VT220/xterm-new 口味，
    ///      与 TERM=xterm-256color 的 terminfo（khome=/EOH、kend=/EOF、kf1-kf4=/EOP../EOS）不符。
    ///   4) 内置表无修饰键组合（Ctrl+Up 也发 /EOA）。
    ///
    /// 修饰键编码遵循 xterm：CSI 1 ; mod final，mod = 1 + Shift(1) + Alt(2) + Ctrl(4)。
    /// 光标键的“普通”形态与 DECCKM 应用光标键模式相关，由调用方向引擎询问（KeyPressed），
    /// 本表只管最终字节的正确形态（带修饰时 xterm 不再区分应用模式）。
    /// </summary>
    public static class XtermKeys
    {
        public const string Escape = "\u001b";
        public const string Enter = "\r";
        public const string Backspace = "\u007f";
        public const string Tab = "\t";
        public const string BackTab = "\u001b[Z";
        public const string Null = "\0";

        private static int Mod(bool control, bool shift, bool alt)
        {
            return 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);
        }

        /// <summary>
        /// 光标键（Up/Down/Left/Right/Home/End）。
        /// 无修饰时：Home/End 恒为 SS3（/EOH、/EOF，xterm 行为，不受 DECCKM 影响）；
        /// 方向键返回普通 CSI 形态，调用方在应用光标键模式下应改用 <see cref="IsCursorKey"/> 之外的路径。
        /// 有修饰时：CSI 1 ; mod final。
        /// </summary>
        public static string CursorKey(string name, bool control, bool shift, bool alt)
        {
            char final = CursorFinal(name);
            if (final == '\0') return null;
            int mod = Mod(control, shift, alt);
            if (mod == 1)
            {
                if (final == 'H' || final == 'F') return "\u001bO" + final; // khome=/EOH kend=/EOF
                return "\u001b[" + final;
            }
            return "\u001b[1;" + mod.ToString() + final;
        }

        /// <summary>方向键的“应用光标键模式”形态（DECCKM 置位时无修饰键走这里）。</summary>
        public static string CursorKeyApplication(string name)
        {
            char final = CursorFinal(name);
            if (final == '\0') return null;
            return "\u001bO" + final;
        }

        public static bool IsCursorKey(string name)
        {
            return CursorFinal(name) != '\0';
        }

        public static bool IsArrowKey(string name)
        {
            char f = CursorFinal(name);
            return f == 'A' || f == 'B' || f == 'C' || f == 'D';
        }

        private static char CursorFinal(string name)
        {
            if (string.IsNullOrEmpty(name)) return '\0';
            switch (name.Trim())
            {
                case "Up": return 'A';
                case "Down": return 'B';
                case "Right": return 'C';
                case "Left": return 'D';
                case "Home": return 'H';
                case "End": return 'F';
                default: return '\0';
            }
        }

        /// <summary>
        /// F1-F24。
        /// F13-F24 是 xterm 的老编号（= Shift+F1..F12 的键码），terminfo 里 kf13..kf24 就是
        /// CSI 1;2P / CSI 15;2~ 这一组；不认它们的话 Shift+F1 之后的功能键全哑。
        /// </summary>
        public static string FunctionKey(int n, bool control, bool shift, bool alt)
        {
            if (n >= 13 && n <= 24) return ExtendedFunctionKey(n);
            if (n < 1 || n > 12) return null;
            int mod = Mod(control, shift, alt);
            // F1-F4：SS3（无修饰）/ CSI 1 ; mod P..S（有修饰）
            if (n <= 4)
            {
                char f = (char)('P' + (n - 1));
                return mod == 1 ? "\u001bO" + f : "\u001b[1;" + mod.ToString() + f;
            }
            // F5-F12：CSI <num> ~ / CSI <num> ; mod ~
            int[] num = { 15, 17, 18, 19, 20, 21, 23, 24 };
            int code = num[n - 5];
            return mod == 1 ? "\u001b[" + code.ToString() + "~" : "\u001b[" + code.ToString() + ";" + mod.ToString() + "~";
        }

        /// <summary>F13-F24（xterm-256color terminfo 的 kf13..kf24 原值）。</summary>
        public static string ExtendedFunctionKey(int n)
        {
            switch (n)
            {
                case 13: return "\u001b[1;2P";
                case 14: return "\u001b[1;2Q";
                case 15: return "\u001b[1;2R";
                case 16: return "\u001b[1;2S";
                case 17: return "\u001b[15;2~";
                case 18: return "\u001b[17;2~";
                case 19: return "\u001b[18;2~";
                case 20: return "\u001b[19;2~";
                case 21: return "\u001b[20;2~";
                case 22: return "\u001b[21;2~";
                case 23: return "\u001b[23;2~";
                case 24: return "\u001b[24;2~";
                default: return null;
            }
        }

        /// <summary>Insert/Delete/PageUp/PageDown。</summary>
        public static string EditKey(string name, bool control, bool shift, bool alt)
        {
            int code;
            switch ((name ?? string.Empty).Trim())
            {
                case "Insert": code = 2; break;
                case "Delete": code = 3; break;
                case "PageUp": code = 5; break;
                case "PageDown": code = 6; break;
                default: return null;
            }
            int mod = Mod(control, shift, alt);
            return mod == 1 ? "\u001b[" + code.ToString() + "~" : "\u001b[" + code.ToString() + ";" + mod.ToString() + "~";
        }

        /// <summary>
        /// 统一入口（不含方向键——它们要按 DECCKM 决定形态，由调用方分别处理）。
        /// 返回 null 表示本表不管这个键。
        /// </summary>
        public static string For(string name, bool control, bool shift, bool alt)
        {
            if (string.IsNullOrEmpty(name)) return null;
            switch (name)
            {
                case "Enter":
                case "Return":
                    return Enter;
                case "Escape":
                    return Escape;
                case "Back":
                case "Backspace":
                    return control ? "\b" : Backspace;
                case "Tab":
                    if (shift) return BackTab;
                    return control ? Tab : Tab;
                case "Space":
                    return control ? Null : " ";
            }

            if (name.Length == 2 && name[0] == 'F' && name[1] >= '1' && name[1] <= '9')
                return FunctionKey(name[1] - '0', control, shift, alt);
            if (name.Length == 3 && name[0] == 'F' && name[1] == '1' && name[2] >= '0' && name[2] <= '9')
                return FunctionKey(10 + (name[2] - '0'), control, shift, alt);   // F10-F19
            if (name.Length == 3 && name[0] == 'F' && name[1] == '2' && name[2] >= '0' && name[2] <= '4')
                return FunctionKey(20 + (name[2] - '0'), control, shift, alt);

            if (IsCursorKey(name))
                return CursorKey(name, control, shift, alt);

            return EditKey(name, control, shift, alt);
        }

        /// <summary>
        /// xterm modifyOtherKeys（CSI > 4 ; n m）的扩展键编码：CSI 27 ; mod ; code ~。
        /// </summary>
        /// <param name="code">基础字符码（'a'=97、'1'=49…）。</param>
        public static string ModifiedKey(int code, bool control, bool shift, bool alt)
        {
            if (code <= 0) return null;
            int mod = 1;
            if (shift) mod += 1;
            if (alt) mod += 2;
            if (control) mod += 4;
            return "\u001b[27;" + mod.ToString() + ";" + code.ToString() + "~";
        }

        /// <summary>
        /// 从 WM_CHAR 的控制码反推 xterm 认的“基础字符码”。
        /// Ctrl+A..Ctrl+Z 的 KeyChar 就是 0x01..0x1A，字母按此还原；Shift 只影响大小写。
        /// 返回 0 表示无对应字符（Esc/Del 这类与功能键冲突的不做映射）。
        /// </summary>
        public static int BaseKeyCode(char keyChar, bool shift)
        {
            if (keyChar >= '\u0001' && keyChar <= '\u001a')
            {
                char letter = (char)('a' + (keyChar - 1));
                return shift ? char.ToUpperInvariant(letter) : letter;
            }
            if (keyChar == '\u0000') return ' ';
            if (keyChar >= ' ' && keyChar <= '~') return keyChar;
            return 0;
        }

        /// <summary>
        /// Ctrl+字符 的组合码（xterm：Ctrl+A..Z → 0x01..0x1A，Ctrl+[ \ ] ^ _ → 0x1B..0x1F）。
        /// 返回 null 表示不是可映射的 Ctrl 组合。
        /// </summary>
        public static string ControlChar(char c)
        {
            if (c >= 'a' && c <= 'z') return ((char)(c - 'a' + 1)).ToString();
            if (c >= 'A' && c <= 'Z') return ((char)(c - 'A' + 1)).ToString();
            switch (c)
            {
                case ' ': return Null;     // C-Space = NUL（readline set-mark）
                case '[': return "\u001b";
                case '\\': return "\u001c";
                case ']': return "\u001d";
                case '^': return "\u001e";
                case '_': return "\u001f";
                case '?': return "\u007f"; // C-? = DEL
                case '@': return Null;
                default:
                    if (c >= '@' && c <= '_') return ((char)(c - '@')).ToString();
                    return null;
            }
        }
    }
}
