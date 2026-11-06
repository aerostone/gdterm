using System;
using System.Text;

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>
    /// 宽字符列宽模型（替代 lib 缺失的 wcwidth）。
    ///
    /// 背景：lib\VtNetCore.dll 把每个 Unicode 码点都当成 1 列存储，而真实终端里
    /// 汉字/假名/韩文/全角符号/emoji 占 2 列。后果是任何“按显示列定位”的程序
    /// （bash readline 改行、vim、less、tmux 状态栏）都会错位：
    /// 实测 “中文” + CSI 2D + “X” 得到 “X文”（正确应为 “中X”），
    /// 每多一个宽字符就多偏 1 列，且不可重编库。
    ///
    /// 对策：喂给库之前给宽字符补一个“占位列”（<see cref="Filler"/>，PUA U+E000），
    /// 让库的列号与真实显示列一致；所有对外出文本的地方再用 <see cref="Strip"/> 去掉占位列。
    /// 渲染器遇到占位列不画、不推进 x（宽字符字形本身已占 2 格）。
    /// 组合字符（零宽）先在本地做 NFC 合成，合成不掉的直接丢弃——宁可少一个
    /// 重音符号，也不要整行文字右移（lib 会把它当 1 列）。
    /// </summary>
    public static class WideText
    {
        /// <summary>占位列字符（私用区 U+E000）。对用户不可见，出文本前必须 Strip。</summary>
        public const char Filler = '\uE000';

        /// <summary>显示宽度：0=组合/零宽，1=半角，2=全角。</summary>
        public static int Width(int cp)
        {
            if (cp < 0x7F) return 1;          // ASCII / DEL
            if (cp < 0xA0) return 1;          // C1 控制符由库处理，按 1 列兜底
            if (IsZeroWidth(cp)) return 0;
            return IsWide(cp) ? 2 : 1;
        }

        /// <summary>组合字符、变体选择符、零宽连接符等不占列。</summary>
        public static bool IsZeroWidth(int cp)
        {
            return (cp >= 0x0300 && cp <= 0x036F)   // 组合变音符
                || (cp >= 0x0483 && cp <= 0x0489)
                || (cp >= 0x0591 && cp <= 0x05BD)
                || (cp >= 0x0610 && cp <= 0x061A)
                || (cp >= 0x064B && cp <= 0x065F)
                || (cp >= 0x0900 && cp <= 0x0903)
                || (cp >= 0x093A && cp <= 0x094F)
                || (cp >= 0x0E31 && cp <= 0x0E3A)
                || (cp >= 0x1AB0 && cp <= 0x1AFF)
                || (cp >= 0x1DC0 && cp <= 0x1DFF)
                || (cp >= 0x200B && cp <= 0x200F)   // ZWSP/ZWJ/ZWNJ/LRM/RLM
                || (cp >= 0x20D0 && cp <= 0x20F0)
                || cp == 0x2060
                || (cp >= 0xFE00 && cp <= 0xFE0F)   // 变体选择符 VS1-16
                || (cp >= 0xFE20 && cp <= 0xFE2F)
                || cp == 0xFEFF                         // BOM
                || (cp >= 0xE0100 && cp <= 0xE01EF);    // 变体选择符补充
        }

        /// <summary>东亚宽/全角 + emoji 图形字符。</summary>
        public static bool IsWide(int cp)
        {
            return (cp >= 0x1100 && cp <= 0x115F)     // 韩文字母
                || (cp >= 0x2E80 && cp <= 0x303E)     // CJK 部首、标点
                || (cp >= 0x3041 && cp <= 0x33FF)     // 假名、注音、CJK 兼容
                || (cp >= 0x3400 && cp <= 0x4DBF)     // CJK 扩展 A
                || (cp >= 0x4E00 && cp <= 0x9FFF)     // CJK 基本区
                || (cp >= 0xA000 && cp <= 0xA4CF)     // 彝文
                || (cp >= 0xA960 && cp <= 0xA97F)
                || (cp >= 0xAC00 && cp <= 0xD7A3)     // 韩文音节
                || (cp >= 0xF900 && cp <= 0xFAFF)     // CJK 兼容汉字
                || (cp >= 0xFE10 && cp <= 0xFE19)     // 竖排标点
                || (cp >= 0xFE30 && cp <= 0xFE6F)     // CJK 兼容形式
                || (cp >= 0xFF00 && cp <= 0xFF60)     // 全角 ASCII
                || (cp >= 0xFFE0 && cp <= 0xFFE6)     // 全角符号
                || (cp >= 0x1F1E6 && cp <= 0x1F1FF)   // 区域指示符（国旗）
                || (cp >= 0x1F300 && cp <= 0x1F5FF)   // 图形/表情
                || (cp >= 0x1F600 && cp <= 0x1F64F)
                || (cp >= 0x1F680 && cp <= 0x1F6FF)
                || (cp >= 0x1F900 && cp <= 0x1F9FF)
                || (cp >= 0x1FA70 && cp <= 0x1FAFF)
                || (cp >= 0x1F004 && cp <= 0x1F0CF)
                || (cp >= 0x1F200 && cp <= 0x1F2FF)
                || (cp >= 0x17000 && cp <= 0x18AFF)   // 女真文字
                || (cp >= 0x20000 && cp <= 0x3FFFD);  // CJK 扩展 B 及以后
        }

        /// <summary>文本的显示列数（占位列算 1 列，与库的列号一致）。</summary>
        public static int Columns(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80 || c == Filler) { n++; continue; }
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                }
                n += Math.Max(1, Width(cp));
            }
            return n;
        }

        /// <summary>去掉占位列（复制、导出、搜索、标题等对外文本必须调用）。</summary>
        public static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf(Filler) < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (s[i] != Filler) sb.Append(s[i]);
            return sb.ToString();
        }

        /// <summary>
        /// 喂库前展开：NFC 合成组合字符 → 宽字符后补占位列 → 丢弃残余零宽字符。
        /// 纯 ASCII 直接原样返回（热路径）。
        /// </summary>
        public static string Expand(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            bool ascii = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // 占位列已存在（重复喂入）或非 ASCII → 需要处理
                if (c >= 0x80) { ascii = false; break; }
            }
            if (ascii) return text;

            string norm;
            try { norm = text.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { norm = text; }

            var sb = new StringBuilder(norm.Length + 8);
            for (int i = 0; i < norm.Length; i++)
            {
                char c = norm[i];
                if (c < 0x80) { sb.Append(c); continue; }

                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < norm.Length && char.IsLowSurrogate(norm[i + 1]))
                {
                    cp = char.ConvertToUtf32(c, norm[i + 1]);
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    continue;   // 孤立代理项：丢掉，别让库当成 1 列
                }

                int w = Width(cp);
                if (w <= 0) continue;   // 组合字符/零宽：NFC 没合成掉就丢弃

                if (cp > 0xFFFF) sb.Append(char.ConvertFromUtf32(cp));
                else sb.Append((char)cp);

                if (w == 2) sb.Append(Filler);
            }
            return sb.ToString();
        }
    }
}
