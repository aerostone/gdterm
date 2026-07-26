using System;
using System.Drawing;
using System.Linq;
using System.Text;
using Gdterm.Terminal.Rendering.Vt;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// Phase 0：VtNetCore 引擎接线验收（无需 UI / 无需真实 SSH）。
    /// Windows: msbuild 后跑 Gdterm.Tests.exe。
    /// </summary>
    public static class VtTerminalEngineTests
    {
        public static void Run()
        {
            Console.WriteLine("-- VtTerminalEngine (Phase 0) --");
            PlainTextAndCursor();
            TrueColorRgb();
            Color256();
            AltScreenAndClear();
            ScrollAndHistoryCap();
            ParseWebColorHelper();
            SendToHostOnDeviceAttributes();
        }

        private static void PlainTextAndCursor()
        {
            using (var eng = new VtTerminalEngine(40, 10, 200))
            {
                eng.Feed("Hello");
                var screen = eng.GetScreenText();
                Assert.True(screen.Contains("Hello"), "plain text Hello visible");
                Assert.Equal(5, eng.CursorColumn, "cursor after Hello");
                Assert.Equal(0, eng.CursorRow, "cursor row 0");

                // CSI 左移 2 + 覆写
                eng.Feed("\u001b[2DXX");
                screen = eng.GetScreenText();
                Assert.True(screen.Contains("HelXX") || screen.Contains("XX"), "cursor move + overwrite");
            }
        }

        private static void TrueColorRgb()
        {
            using (var eng = new VtTerminalEngine(40, 5, 100))
            {
                // ESC[38;2;R;G;Bm — 24-bit foreground
                eng.Feed("\u001b[38;2;255;0;0mR\u001b[38;2;0;255;0mG\u001b[38;2;0;0;255mB\u001b[0m");
                var page = eng.SnapshotVisible();
                Assert.True(page != null && page.Lines != null && page.Lines.Count > 0, "truecolor page non-empty");

                var spans = page.Lines[0].Spans;
                Assert.True(spans != null && spans.Count >= 1, "truecolor has spans");

                // 在 span 中找接近纯红/绿/蓝的前景
                bool sawRed = false, sawGreen = false, sawBlue = false;
                foreach (var sp in spans)
                {
                    if (string.IsNullOrEmpty(sp.Text)) continue;
                    if (sp.Text.IndexOf('R') >= 0 && sp.Foreground.R >= 200 && sp.Foreground.G < 80 && sp.Foreground.B < 80)
                        sawRed = true;
                    if (sp.Text.IndexOf('G') >= 0 && sp.Foreground.G >= 200 && sp.Foreground.R < 80 && sp.Foreground.B < 80)
                        sawGreen = true;
                    if (sp.Text.IndexOf('B') >= 0 && sp.Foreground.B >= 200 && sp.Foreground.R < 80 && sp.Foreground.G < 80)
                        sawBlue = true;
                }

                Assert.True(sawRed, "truecolor red 38;2;255;0;0");
                Assert.True(sawGreen, "truecolor green 38;2;0;255;0");
                Assert.True(sawBlue, "truecolor blue 38;2;0;0;255");

                Console.WriteLine("   truecolor spans: " + string.Join(" | ",
                    spans.Where(s => !string.IsNullOrWhiteSpace(s.Text))
                         .Select(s => s.Text.Trim() + "@" + s.Foreground.R + "," + s.Foreground.G + "," + s.Foreground.B)));
            }
        }

        private static void Color256()
        {
            using (var eng = new VtTerminalEngine(40, 5, 100))
            {
                // ESC[38;5;196m — 256-color bright red (cube)
                eng.Feed("\u001b[38;5;196mX\u001b[0m");
                var page = eng.SnapshotVisible();
                var spans = page.Lines[0].Spans;
                bool saw = false;
                foreach (var sp in spans)
                {
                    if (sp.Text != null && sp.Text.IndexOf('X') >= 0 && sp.Foreground.R > 150)
                    {
                        saw = true;
                        Console.WriteLine("   256-color X fg=" + sp.Foreground.R + "," + sp.Foreground.G + "," + sp.Foreground.B);
                    }
                }
                Assert.True(saw, "256-color index 196 produces red-ish cell");
            }
        }

        private static void AltScreenAndClear()
        {
            using (var eng = new VtTerminalEngine(20, 8, 100))
            {
                eng.Feed("LINE1\r\nLINE2\r\nLINE3");
                Assert.True(eng.GetScreenText().Contains("LINE1"), "before alt: LINE1");

                // DECSET 1049 — alternate screen + clear (xterm)
                eng.Feed("\u001b[?1049h");
                eng.Feed("TUI-APP");
                var alt = eng.GetScreenText();
                Assert.True(alt.Contains("TUI-APP"), "alt screen shows TUI-APP");

                // Exit alt screen
                eng.Feed("\u001b[?1049l");
                var normal = eng.GetScreenText();
                // 回到主缓冲后 LINE1 应仍在（或至少不是只有 TUI-APP）
                Assert.True(normal.Contains("LINE1") || !normal.Contains("TUI-APP"),
                    "exit alt restores primary buffer");
            }
        }

        private static void ScrollAndHistoryCap()
        {
            using (var eng = new VtTerminalEngine(10, 5, 30))
            {
                Assert.Equal(30, eng.MaximumHistoryLines, "history cap set");
                for (int i = 0; i < 40; i++)
                    eng.Feed("R" + i + "\r\n");

                // 不应崩溃；可见区仍有内容
                var text = eng.GetScreenText();
                Assert.True(!string.IsNullOrEmpty(text), "scrolled screen non-empty");
                Assert.True(eng.MaximumHistoryLines <= 2000, "history hard cap");
            }
        }

        private static void ParseWebColorHelper()
        {
            var c = VtTerminalEngine.ParseWebColor("#FF0000", Color.Black);
            Assert.Equal(255, c.R, "parse R");
            Assert.Equal(0, c.G, "parse G");
            Assert.Equal(0, c.B, "parse B");

            var fb = VtTerminalEngine.ParseWebColor(null, Color.Blue);
            Assert.Equal(Color.Blue.B, fb.B, "null fallback");
        }

        private static void SendToHostOnDeviceAttributes()
        {
            using (var eng = new VtTerminalEngine(40, 10, 100))
            {
                byte[] got = null;
                eng.SendToHost += (s, data) => { got = data; };

                // CSI c — Device Attributes primary
                eng.Feed("\u001b[c");
                Assert.True(got != null && got.Length > 0, "DA response via SendToHost");
                var resp = Encoding.ASCII.GetString(got);
                Console.WriteLine("   DA response: " + resp.Replace("\u001b", "<esc>"));
                Assert.True(resp.IndexOf('[') >= 0 || resp.IndexOf("\u001b") >= 0, "DA looks like CSI");
            }
        }
    }
}
