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
            // 低配默认 300；UI NormalizeProfile 夹到 100..2000
            var p = new TerminalProfile();
            Assert.Equal(300, p.ScrollbackLines, "default scrollback 300 for low-memory");
            Assert.True(p.UseVtCell, "default remains VtCell (not auto Lightweight)");
        }
    }
}
