using System;
using System.Collections.Generic;
using System.Drawing;
using Gdterm.Terminal.Rendering.Vt;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// 选区文本计算（右键「全选」的数据层）。纯快照 → 文本，不依赖 GDI，
    /// 所以 CI/Linux 上也能断言"复制出来到底是哪几个字"。
    /// </summary>
    public static class TerminalSelectionTests
    {
        public static void Run()
        {
            Console.WriteLine("-- TerminalSelection --");
            SingleCellSelectionIsNotEmpty();
            MultiLineDragSelection();
            ReverseDragIsNormalized();
            TrailingPaddingTrimmed();
            SelectAllSkipsTrailingBlankRows();
            SelectAllOnEmptyScreen();
            WideCharSpans();
        }

        private static VtPageSnapshot MakePage(params string[] lines)
        {
            var page = new VtPageSnapshot
            {
                Columns = 20,
                Rows = lines.Length,
                Lines = new List<VtLineSnapshot>()
            };
            foreach (var text in lines)
            {
                var spans = new List<VtSpanSnapshot>();
                if (!string.IsNullOrEmpty(text))
                    spans.Add(new VtSpanSnapshot { Text = text, Foreground = Color.White, Background = Color.Black });
                page.Lines.Add(new VtLineSnapshot { Spans = spans });
            }
            return page;
        }

        /// <summary>旧实现在这里返回空串：起点=终点（只点了一个字符）时 endC-startC=0。</summary>
        private static void SingleCellSelectionIsNotEmpty()
        {
            var page = MakePage("abcdef");
            string text = TerminalSelection.ComputeText(page, 2, 0, 2, 0);
            Assert.Equal("c", text, "单格选区必须复制出 1 个字符");
        }

        private static void MultiLineDragSelection()
        {
            var page = MakePage("hello", "world", "tail");
            string text = TerminalSelection.ComputeText(page, 0, 0, 10, 1);
            Assert.Equal("hello\nworld", text, "跨行拖选按行拼接");
        }

        private static void ReverseDragIsNormalized()
        {
            var page = MakePage("abc", "def");
            string forward = TerminalSelection.ComputeText(page, 1, 0, 1, 1);
            string backward = TerminalSelection.ComputeText(page, 1, 1, 1, 0);
            Assert.Equal(forward, backward, "从下往上拖与从上往下拖结果一致");
            Assert.Equal("bc\nde", forward, "反向拖选的边界");
        }

        private static void TrailingPaddingTrimmed()
        {
            var page = MakePage("ls -la        ", "next          ");
            string text = TerminalSelection.ComputeText(page, 0, 0, 19, 1);
            Assert.Equal("ls -la\nnext", text, "行尾填充空白不进入剪贴板");
        }

        private static void SelectAllSkipsTrailingBlankRows()
        {
            var page = MakePage("first", "second", "", "", "          ");
            int sc, sr, ec, er;
            bool ok = TerminalSelection.TryComputeSelectAll(page, out sc, out sr, out ec, out er);
            Assert.True(ok, "有内容的屏要能算出全选边界");
            Assert.Equal(0, sc, "全选起点列 0");
            Assert.Equal(0, sr, "全选起点行 0");
            Assert.Equal(1, er, "全选止于最后一条有内容的行（2 号行是空行）");
            Assert.Equal(5, ec, "全选止列 = 最后一个字符的下标(second→5)");

            string text = TerminalSelection.ComputeText(page, sc, sr, ec, er);
            Assert.Equal("first\nsecond", text, "全选复制内容");
        }

        private static void SelectAllOnEmptyScreen()
        {
            var page = MakePage("", "   ");
            int sc, sr, ec, er;
            Assert.True(!TerminalSelection.TryComputeSelectAll(page, out sc, out sr, out ec, out er),
                "全空白屏不允许全选（否则复制出空串还亮着一屏高亮）");
            Assert.True(!TerminalSelection.TryComputeSelectAll(null, out sc, out sr, out ec, out er),
                "null 快照不允许全选");
        }

        /// <summary>宽字符（中日韩）在选择时按字符下标走，WideText.Strip 负责最后收敛成可粘贴文本。</summary>
        private static void WideCharSpans()
        {
            var page = MakePage("中文abc");
            string text = TerminalSelection.ComputeText(page, 0, 0, 6, 0);
            Assert.Equal("中文abc", text, "宽字符行选全文");
            Assert.Equal(5, TerminalSelection.LineTextLength(page.Lines[0]), "行文本长度按字符算");
            Assert.Equal("abc", TerminalSelection.ComputeText(page, 2, 0, 4, 0), "宽字符行内偏移选择");
        }
    }
}
