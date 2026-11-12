using System;
using System.Collections.Generic;
using System.Text;
using Gdterm.Core.Serialization;

namespace Gdterm.Tests.Core
{
    /// <summary>
    /// 十六进制转储的纯函数断言：日志查看器的二进制视图全靠它，
    /// 排错了就是"用户按看到的字节去核对却对不上"——比不显示更坏。
    /// </summary>
    public static class HexDumpTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] HexDump");

            // 满行：偏移列 8 位补零 + 半行多一个空格 + ASCII 列用竖线包裹
            var data = Encoding.ASCII.GetBytes("GDTERM...");
            string line = HexDump.FormatLine(data, 0, data.Length, 0x10);
            Assert.Equal("00000010  47 44 54 45 52 4d 2e 2e  2e                       |GDTERM...|",
                line, "full line layout");

            // 半行：不足 16 字节时字节列补空白，ASCII 列仍对齐在同一列
            byte[] three = { 0x41, 0x00, 0x7f };
            string half = HexDump.FormatLine(three, 0, 3, 0);
            Assert.Equal("00000000  41 00 7f                                          |A..|",
                half, "partial line: byte column padded so | lands in the same column");

            // 不可打印与控制字符一律 '.'（含 DEL 0x7f 与高位字节 0xff）
            byte[] mixed = { 0x61, 0x7f, 0xff, 0x1b };
            string m = HexDump.FormatLine(mixed, 0, mixed.Length, 0);
            Assert.True(m.EndsWith("|a...|"), "non-printable rendered as dots: " + m);

            // 空白与 '~' 是可打印边界，不能被点掉
            byte[] edge = { 0x20, 0x7e, 0x1f, 0x80 };
            Assert.True(HexDump.FormatLine(edge, 0, 4, 0).EndsWith("| ~..|"),
                "printable boundaries kept");

            // 绝对偏移不等于数组偏移：分页读取时行首必须是文件内偏移
            // （19088743 == 0x01234567，写成十进制是为了避免与"十进制偏移"断言互相吃）
            string paged = HexDump.FormatLine(data, 0, 2, 19088743);
            Assert.True(paged.StartsWith("01234567  47 44"), "absolute offset column: " + paged);

            // 越界参数不得抛（调用方可能是并发刷新的 UI）：超出部分自动收缩
            string clamped = HexDump.FormatLine(three, 2, 99, 0);
            Assert.True(clamped.Contains("7f"), "count clamped to data length");
            Assert.Equal(string.Empty, HexDump.FormatLine(null, 0, 0, 0), "null safe");

            // 多行 + 截断提示：超出 maxLines 必须显式说明，不静默丢数据
            var big = new byte[64];
            var all = HexDump.FormatLines(big, 0, 0);
            Assert.Equal(4, all.Count, "64 bytes => 4 lines");
            var cut = HexDump.FormatLines(big, 0, 2);
            Assert.Equal(3, cut.Count, "2 lines + truncation marker");
            Assert.True(cut[2].Contains("2 more lines"), "truncation marker says how many: " + cut[2]);

            // 空数据返回空集合（不是含空行的集合）
            Assert.Equal(0, HexDump.FormatLines(new byte[0], 0, 0).Count, "empty => no lines");
            Assert.Equal(string.Empty, HexDump.Format(null, 0, 0), "null => empty text");

            // Format：行间 \r\n（文本框需要 CRLF，裸 \n 在 WinForms TextBox 里会并成一行）
            var text = HexDump.Format(new byte[] { 0x41, 0x42 }, 0, 0);
            Assert.True(text.Contains("\r\n") == false, "single line has no separator");
            text = HexDump.Format(new byte[32], 0, 0);
            Assert.Equal(1, CountOf(text, "\r\n"), "two lines => exactly one CRLF");
        }

        private static int CountOf(string s, string what)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
            return n;
        }
    }
}
