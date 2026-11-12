using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gdterm.Core.Serialization
{
    /// <summary>
    /// 十六进制转储（日志查看器的二进制视图）。
    ///
    /// 为什么放进 Core 而不是 UI：这是纯函数（byte[] → 文本行），
    /// 能单测、能被将来的抓包/终端字节视图复用；放在窗体里就只能靠截图验收。
    /// 输出格式对齐 `xxd -g1` / `od -Ax -tx1z`：
    /// <code>
    /// 00000010  47 44 54 45 52 4d 00 0a  00 00 00 ff 7f 41 42 43  |GDTERM... ..ABC|
    /// </code>
    /// </summary>
    public static class HexDump
    {
        /// <summary>每行字节数（固定 16：与 xxd 默认一致，便于与外部工具对读）。</summary>
        public const int BytesPerLine = 16;

        /// <summary>半行分隔处（第 8 字节后有额外空格）。</summary>
        private const int HalfLine = 8;

        /// <summary>
        /// 格式化单行。offset 是**数据内的偏移**，absoluteOffset 是**文件内的绝对偏移**
        /// （分页读取时数据从文件中间开始，行首列必须展示绝对偏移，否则对不上上下文）。
        /// </summary>
        public static string FormatLine(byte[] data, int offset, int count, long absoluteOffset)
        {
            if (data == null) return string.Empty;
            if (offset < 0) offset = 0;
            if (offset > data.Length) offset = data.Length;
            if (count < 0) count = 0;
            if (offset + count > data.Length) count = data.Length - offset;

            // 偏移列按数据规模自适应宽度：大文件不至于把字节列挤到看不到
            int offsetDigits = Math.Max(8, absoluteOffset.ToString("x").Length);
            var sb = new StringBuilder(offsetDigits + 4 + BytesPerLine * 3 + 2 + BytesPerLine + 2);

            sb.Append(absoluteOffset.ToString("x").PadLeft(offsetDigits, '0'));
            sb.Append("  ");

            for (int i = 0; i < BytesPerLine; i++)
            {
                if (i < count) sb.Append(data[offset + i].ToString("x2"));
                else sb.Append("  ");   // 末行补齐，ASCII 列才能对齐
                sb.Append(' ');
                if (i == HalfLine - 1) sb.Append(' ');
            }

            sb.Append(" |");
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                // 只显示可打印 ASCII；控制字符/高位字节一律 '.'（与 xxd 一致，不做 latin-1 猜测）
                sb.Append(b >= 0x20 && b <= 0x7e ? (char)b : '.');
            }
            sb.Append('|');
            return sb.ToString();
        }

        /// <summary>
        /// 把一段数据整块格式化成多行。maxLines &lt;= 0 视为不限；
        /// 超出时在末尾追加一行 "…" 说明被截断（不静默丢行）。
        /// </summary>
        public static List<string> FormatLines(byte[] data, long absoluteOffset, int maxLines)
        {
            var lines = new List<string>();
            if (data == null || data.Length == 0) return lines;

            int total = (data.Length + BytesPerLine - 1) / BytesPerLine;
            int limit = maxLines > 0 && maxLines < total ? maxLines : total;
            for (int i = 0; i < limit; i++)
            {
                int off = i * BytesPerLine;
                int cnt = Math.Min(BytesPerLine, data.Length - off);
                lines.Add(FormatLine(data, off, cnt, absoluteOffset + off));
            }
            if (limit < total)
            {
                lines.Add("… (" + (total - limit).ToString(CultureInfo.InvariantCulture)
                    + " more lines)");
            }
            return lines;
        }

        /// <summary>整块数据 → 一个字符串（行间 \r\n）。小数据量/日志用。</summary>
        public static string Format(byte[] data, long absoluteOffset, int maxLines)
        {
            var lines = FormatLines(data, absoluteOffset, maxLines);
            if (lines.Count == 0) return string.Empty;
            var sb = new StringBuilder(lines.Count * 80);
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append("\r\n");
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }
    }
}
