using System;
using System.Text;
using Gdterm.Terminal.Rendering.Vt;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// 反显（SGR 7）颜色交换。
    ///
    /// 背景：VtNetCore 把反显渲染成"前景固定黑 + 背景取原前景"，主题默认前景/背景完全不参与，
    /// 于是 `\e[31m\e[7m` 变成黑底红上的黑字、默认反显变成纯黑而不是主题背景色。
    /// 修法在 VtStreamPreprocessor：见到 7 就把逻辑 fg/bg 交换后以真彩色显式下发，27 还原。
    /// </summary>
    public static class ReverseVideoTests
    {
        public static void Run()
        {
            Console.WriteLine("-- ReverseVideo (SGR 7) --");
            NoReverseLeavesBytesAlone();
            ReverseSwapsDefaultColors();
            ReverseSwapsExplicitForeground();
            ReverseOffRestoresLogicalColors();
            ResetWhileReverseEmitsReset();
            ReverseWithPaletteBackground();
            EngineEndpointColors();
            StreamSplitAcrossFeeds();
        }

        private static VtStreamPreprocessor NewPre(string fg = "#e0e0e0", string bg = "#101010")
        {
            var pre = new VtStreamPreprocessor();
            pre.DefaultForeground = fg;
            pre.DefaultBackground = bg;
            return pre;
        }

        /// <summary>不含反显的 SGR 必须原样透传（这条路径是终端核心，不能有任何扰动）。</summary>
        private static void NoReverseLeavesBytesAlone()
        {
            var pre = NewPre();
            Assert.True(pre.RewriteReverse("") == null, "空参数不改写");
            Assert.True(pre.RewriteReverse("0") == null, "重置不改写");
            Assert.True(pre.RewriteReverse("1") == null, "粗体不改写");
            Assert.True(pre.RewriteReverse("31") == null, "普通前景色不改写");
            Assert.True(pre.RewriteReverse("48;5;196") == null, "扩展背景色不改写");
            Assert.True(pre.RewriteReverse("38;2;1;2;3") == null, "真彩前景不改写");
            Assert.True(pre.RewriteReverse("27") == null, "没有反显在生效时 27 不改写");
        }

        private static void ReverseSwapsDefaultColors()
        {
            var pre = NewPre("#e0e0e0", "#101010");
            string outText = pre.RewriteReverse("7");
            Assert.Equal("\u001b[38;2;16;16;16m\u001b[48;2;224;224;224m", outText,
                "默认反显 = 主题背景作前景、主题前景作背景（而不是库的固定黑）");
        }

        private static void ReverseSwapsExplicitForeground()
        {
            var pre = NewPre("#e0e0e0", "#101010");
            pre.RewriteReverse("31");                       // 逻辑前景红
            string outText = pre.RewriteReverse("7");
            Assert.Equal("\u001b[38;2;16;16;16m\u001b[48;2;205;0;0m", outText,
                "红字反显：前景取主题背景、背景取红（旧实现前景永远是 0,0,0）");

            // 反显生效期间改前景色，也要重新交换
            string changed = pre.RewriteReverse("38;2;0;255;0");
            Assert.Equal("\u001b[38;2;16;16;16m\u001b[48;2;0;255;0m", changed,
                "反显期间换前景 = 换背景，前景仍是主题背景");
        }

        private static void ReverseOffRestoresLogicalColors()
        {
            var pre = NewPre("#e0e0e0", "#101010");
            pre.RewriteReverse("31");
            pre.RewriteReverse("7");
            string outText = pre.RewriteReverse("27");
            Assert.Equal("\u001b[38;2;205;0;0m\u001b[48;2;16;16;16m", outText,
                "27 之后回到逻辑色：红字 + 主题背景");

            // 27 之后的纯属性序列不应再被改写
            Assert.True(pre.RewriteReverse("1") == null, "反显关掉后普通属性原样透传");
        }

        private static void ResetWhileReverseEmitsReset()
        {
            var pre = NewPre("#e0e0e0", "#101010");
            pre.RewriteReverse("7");
            string outText = pre.RewriteReverse("0");
            Assert.True(outText != null && outText.StartsWith("\u001b[0m", StringComparison.Ordinal),
                "反显期间遇到 0 必须先发 0（清掉显式真彩），随后补回交换后的默认色");
            Assert.True(pre.RewriteReverse("31") == null, "0 之后反显状态已清空");
        }

        private static void ReverseWithPaletteBackground()
        {
            var pre = NewPre("#e0e0e0", "#101010");
            pre.RewriteReverse("48;5;196");                 // 逻辑背景 = 亮红
            string outText = pre.RewriteReverse("7");
            Assert.Equal("\u001b[38;2;255;0;0m\u001b[48;2;224;224;224m", outText,
                "背景反显后变前景：调色板 196 需按 xterm 立方解出真彩");
        }

        /// <summary>端到端：喂真实字节，看快照里的前景/背景是不是交换过了。</summary>
        private static void EngineEndpointColors()
        {
            using (var eng = new VtTerminalEngine(40, 5, 100))
            {
                eng.SetQueryMetadata("#e0e0e0", "#101010", "#ffffff", 9, 18);
                eng.Feed("\u001b[7mR\u001b[0m");
                var page = eng.SnapshotVisible();
                var span = FindSpan(page, "R");
                Assert.True(span != null, "反显文本出现在快照里");
                Assert.True(span.Foreground.R < 40 && span.Foreground.G < 40 && span.Foreground.B < 40,
                    "默认反显的前景必须是主题背景（深色），实际 " + Describe(span.Foreground));
                Assert.True(span.Background.R > 180, "默认反显的背景必须是主题前景（浅色），实际 " + Describe(span.Background));
            }

            using (var eng = new VtTerminalEngine(40, 5, 100))
            {
                eng.SetQueryMetadata("#e0e0e0", "#101010", "#ffffff", 9, 18);
                eng.Feed("\u001b[31;7mX\u001b[27mY\u001b[0m");
                var page = eng.SnapshotVisible();
                var x = FindSpan(page, "X");
                var y = FindSpan(page, "Y");
                Assert.True(x != null && y != null, "X/Y 都在快照里");
                Assert.True(x.Background.R > 150 && x.Background.G < 60 && x.Background.B < 60,
                    "红字反显 = 红底，实际 " + Describe(x.Background));
                Assert.True(y.Foreground.R > 150 && y.Foreground.G < 60 && y.Foreground.B < 60,
                    "27 之后回到红字，实际 " + Describe(y.Foreground));
            }
        }

        /// <summary>序列被拆到两个 Feed 里（真实网络下常见）：反显状态必须跨包保持。</summary>
        private static void StreamSplitAcrossFeeds()
        {
            using (var eng = new VtTerminalEngine(40, 5, 100))
            {
                eng.SetQueryMetadata("#e0e0e0", "#101010", "#ffffff", 9, 18);
                eng.Feed("\u001b[3");
                eng.Feed("1mZ\u001b[7mW");
                var page = eng.SnapshotVisible();
                var z = FindSpan(page, "Z");
                var w = FindSpan(page, "W");
                Assert.True(z != null && w != null, "跨包 SGR 后文本都在");
                Assert.True(w.Background.R > 150 && w.Background.G < 60,
                    "跨包后 7 依然把红转成背景，实际 " + Describe(w.Background));
            }
        }

        private static VtSpanSnapshot FindSpan(VtPageSnapshot page, string text)
        {
            if (page == null || page.Lines == null) return null;
            foreach (var line in page.Lines)
            {
                if (line == null || line.Spans == null) continue;
                foreach (var span in line.Spans)
                    if (span != null && span.Text != null && span.Text.IndexOf(text, StringComparison.Ordinal) >= 0)
                        return span;
            }
            return null;
        }

        private static string Describe(System.Drawing.Color c)
        {
            return c.R + "," + c.G + "," + c.B;
        }
    }
}
