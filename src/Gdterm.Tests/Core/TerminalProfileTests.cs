using System;
using System.Collections.Generic;
using Gdterm.Core.Models;

namespace Gdterm.Tests.Core
{
    public static class TerminalProfileTests
    {
        public static void Run()
        {
            Console.WriteLine("-- TerminalProfile dual-track --");
            DefaultIsVtCell();
            LightweightFromMetadata();
            RendererInJsonRoundTrip();
            ScrollbackHardCapIsProfileSide();
            ScrollbackShorthandAndLimits();
            TermShorthandOverridesJson();
        }

        /// <summary>
        /// 连接属性「终端」分节写的是简写键 term/locale；它必须压过旧的 terminalProfile JSON，
        /// 否则用户在 UI 里改的 TERM 会被 JSON 里的旧值盖掉（看起来"改了没生效"）。
        /// </summary>
        private static void TermShorthandOverridesJson()
        {
            var md = new Dictionary<string, string>
            {
                { "terminalProfile", "{\"terminalType\":\"vt100\",\"locale\":\"\"}" },
                { "term", "tmux-256color" },
                { "locale", "zh_CN.UTF-8" }
            };
            var p = TerminalProfile.FromMetadata(md);
            Assert.Equal("tmux-256color", p.TerminalType, "shorthand term wins over json");
            Assert.Equal("zh_CN.UTF-8", p.Locale, "shorthand locale wins over json");

            var plain = TerminalProfile.FromMetadata(new Dictionary<string, string> { { "term", "vt220" } });
            Assert.Equal("vt220", plain.TerminalType, "term only");
            Assert.Equal("", plain.Locale, "no locale");

            var empty = TerminalProfile.FromMetadata(new Dictionary<string, string>());
            Assert.Equal("xterm-256color", empty.TerminalType, "default TERM unchanged");
        }

        private static void DefaultIsVtCell()
        {
            var p = new TerminalProfile();
            Assert.True(p.UseVtCell, "default Renderer VtCell");
            Assert.Equal("VtCell", p.Renderer, "default name");
            Assert.Equal("xterm-256color", p.TerminalType, "default TERM");
        }

        private static void LightweightFromMetadata()
        {
            var md = new Dictionary<string, string>
            {
                { "renderer", "lightweight" }
            };
            var p = TerminalProfile.FromMetadata(md);
            Assert.True(!p.UseVtCell, "renderer=lightweight => !UseVtCell");

            md["renderer"] = "VtCell";
            p = TerminalProfile.FromMetadata(md);
            Assert.True(p.UseVtCell, "renderer=VtCell => UseVtCell");
        }

        private static void RendererInJsonRoundTrip()
        {
            var p = new TerminalProfile { Renderer = "Lightweight", TerminalType = "xterm-256color" };
            var json = p.ToJson();
            Assert.True(json.Contains("renderer"), "json has renderer");
            Assert.True(json.Contains("Lightweight"), "json has Lightweight");

            var md = new Dictionary<string, string> { { "terminalProfile", json } };
            var p2 = TerminalProfile.FromMetadata(md);
            Assert.True(!p2.UseVtCell, "from json Lightweight");
        }

        private static void ScrollbackHardCapIsProfileSide()
        {
            // 低配默认 300；UI NormalizeProfile 抬到 MinScrollback、上界交给 TerminalLimits.MaxScrollback(100k)
            var p = new TerminalProfile();
            Assert.Equal(300, p.ScrollbackLines, "default scrollback 300 for low-memory");
            Assert.True(p.UseVtCell, "default remains VtCell (not auto Lightweight)");
        }

        /// <summary>
        /// Metadata["scrollback"] 简写 + 边界收敛（P1-5）：连接属性里填的行数必须能覆盖
        /// profile JSON 的 300/2000，且 0 / 负数 / 超限都被夹到合法值。
        /// </summary>
        private static void ScrollbackShorthandAndLimits()
        {
            var md = new Dictionary<string, string>
            {
                { "terminalProfile", "{\"scrollbackLines\":2000}" },
                { "scrollback", "50000" }
            };
            var p = TerminalProfile.FromMetadata(md);
            Assert.Equal(50000, p.ScrollbackLines, "shorthand scrollback wins over json");

            var json = TerminalProfile.FromMetadata(new Dictionary<string, string>
            {
                { "terminalProfile", "{\"scrollbackLines\":8000}" }
            });
            Assert.Equal(8000, json.ScrollbackLines, "json scrollback honored when no shorthand");

            var bad = TerminalProfile.FromMetadata(new Dictionary<string, string> { { "scrollback", "0" } });
            Assert.Equal(300, bad.ScrollbackLines, "scrollback=0 ignored (keep default)");

            Assert.Equal(Gdterm.Core.Constants.TerminalLimits.MaxScrollback,
                Gdterm.Core.Constants.TerminalLimits.ClampScrollback(999999, 0), "clamp upper bound");
            Assert.Equal(40, Gdterm.Core.Constants.TerminalLimits.ClampScrollback(40, 0), "engine keeps small values");
            Assert.Equal(40, Gdterm.Core.Constants.TerminalLimits.ClampScrollback(10, 40), "raised to one screen");
            Assert.Equal(0, Gdterm.Core.Constants.TerminalLimits.ClampScrollback(-5, 0), "negative floored");
        }
    }
}
