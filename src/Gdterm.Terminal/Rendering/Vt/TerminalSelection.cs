using System;
using System.Text;

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>
    /// 选区文本计算：只吃 <see cref="VtPageSnapshot"/> 纯数据，不碰 GDI/控件，
    /// 因此可以在无图形环境（CI / Linux harness）里直接测。
    ///
    /// 从 CellGdiRenderer 抽出来的原因有两个：
    ///   1) 右键「全选」需要"整屏选区"的边界计算，逻辑不该埋在 GDI 渲染器里；
    ///   2) 之前这部分只有真机截图能验证，抽成纯函数后可以用构造快照做断言。
    /// </summary>
    public static class TerminalSelection
    {
        /// <summary>一行快照的字符数（宽字符按 span 里的字符串长度算，WideText.Strip 负责最后收敛）。</summary>
        public static int LineTextLength(VtLineSnapshot line)
        {
            if (line == null || line.Spans == null) return 0;
            int n = 0;
            foreach (var s in line.Spans)
                if (s != null && s.Text != null) n += s.Text.Length;
            return n;
        }

        /// <summary>从一行里截取 [start, start+length) 的纯文本。</summary>
        public static string ExtractSubstring(VtLineSnapshot line, int start, int length)
        {
            if (line == null || line.Spans == null || length <= 0) return string.Empty;
            var sb = new StringBuilder(length);
            int taken = 0, skipped = 0;
            foreach (var s in line.Spans)
            {
                if (s == null || string.IsNullOrEmpty(s.Text)) continue;
                if (skipped + s.Text.Length <= start)
                {
                    skipped += s.Text.Length;
                    continue;
                }
                int off = Math.Max(0, start - skipped);
                int take = Math.Min(s.Text.Length - off, length - taken);
                if (take > 0) sb.Append(s.Text, off, take);
                taken += take;
                skipped += s.Text.Length;
                if (taken >= length) break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把选区（含端点，坐标按格子下标算）转成文本。四个坐标允许反着给（从下往上拖）。
        /// 端点含入是刻意的：旧实现按排他算，只选中一个字符时复制出来是空串。
        /// 行尾空白一并去掉：整行/整屏复制时不该带一屏填充空格进去。
        /// </summary>
        public static string ComputeText(VtPageSnapshot page, int startCol, int startRow, int endCol, int endRow)
        {
            if (page == null || page.Lines == null || page.Lines.Count == 0) return string.Empty;
            int sc = startCol, sr = startRow, ec = endCol, er = endRow;
            // 规范化：从上到下、左到右
            if (er < sr || (er == sr && ec < sc))
            {
                int t = sc; sc = ec; ec = t;
                t = sr; sr = er; er = t;
            }
            if (sr < 0) sr = 0;
            if (er >= page.Lines.Count) er = page.Lines.Count - 1;
            if (er < sr) return string.Empty;

            var sb = new StringBuilder();
            for (int r = sr; r <= er; r++)
            {
                var line = page.Lines[r];
                int lineLen = LineTextLength(line);
                int startC = (r == sr) ? Math.Min(Math.Max(sc, 0), lineLen) : 0;
                int endC = (r == er) ? Math.Min(ec + 1, lineLen) : lineLen;
                if (endC > startC)
                    sb.Append(ExtractSubstring(line, startC, endC - startC).TrimEnd());
                if (r < er) sb.Append('\n');
            }
            return WideText.Strip(sb.ToString());
        }

        /// <summary>
        /// 「全选」的选区边界：从首行首列到最后一条**有内容**的行尾。
        /// 末尾的空白行不选——否则复制一大段空行进去（滚动区底部常见）。
        /// 返回 false 表示整屏没有可复制的文本。
        /// </summary>
        public static bool TryComputeSelectAll(VtPageSnapshot page, out int startCol, out int startRow, out int endCol, out int endRow)
        {
            startCol = 0; startRow = 0; endCol = 0; endRow = 0;
            if (page == null || page.Lines == null || page.Lines.Count == 0) return false;

            int lastRow = -1, lastLen = 0;
            for (int r = 0; r < page.Lines.Count; r++)
            {
                int len = LineTextLength(page.Lines[r]);
                if (len <= 0) continue;
                string text = ExtractSubstring(page.Lines[r], 0, len).TrimEnd();
                if (text.Length == 0) continue;   // 只有空白的行（居中的清屏残留）不算内容
                lastRow = r;
                lastLen = text.Length;
            }
            if (lastRow < 0) return false;
            endRow = lastRow;
            // ComputeText 的端点含入，所以末列是最后一个字符的下标
            endCol = lastLen - 1;
            return true;
        }
    }
}
