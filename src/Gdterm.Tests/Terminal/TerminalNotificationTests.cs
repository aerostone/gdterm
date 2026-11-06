using System;
using System.Collections.Generic;
using Gdterm.Terminal.Rendering.Vt;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// P0-1 / P0-3 回归：OSC 7 目录跟随、OSC 9 / 777 / 99 通知。
    /// 都是纯引擎级断言（无 UI / 无 SSH），坏了 CI 立刻红。
    /// </summary>
    public static class TerminalNotificationTests
    {
        public static void Run()
        {
            Console.WriteLine("-- OSC 7 cwd / OSC 9-777-99 notifications --");
            Osc7SetsDirectory();
            Osc7RejectsNonFileScheme();
            Osc9Notification();
            Osc9IgnoredWhenNotItermForm();
            Osc777Notify();
            Osc99KittyTitleAndBody();
            Osc99QueryIsSilent();
            Osc99IndependentChunk();
        }

        private static void Osc7SetsDirectory()
        {
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<string>();
                eng.DirectoryChanged += (s, d) => seen.Add(d);
                eng.Feed("\u001b]7;file://myhost/home/dev/proj\u0007");
                Assert.Equal(1, seen.Count, "OSC 7 触发一次目录变更");
                Assert.Equal("/home/dev/proj", seen[0], "OSC 7 取路径部分");
            }
        }

        private static void Osc7RejectsNonFileScheme()
        {
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<string>();
                eng.DirectoryChanged += (s, d) => seen.Add(d);
                eng.Feed("\u001b]7;http://example.com/x\u0007");
                Assert.Equal(0, seen.Count, "非 file:// 的 OSC 7 不算目录");
            }
        }

        private static void Osc9Notification()
        {
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<TerminalNotification>();
                eng.NotificationReceived += (s, n) => seen.Add(n);
                eng.Feed("\u001b]9;build done\u0007");
                Assert.Equal(1, seen.Count, "OSC 9 通知");
                Assert.Equal("build done", seen[0].Body, "OSC 9 正文");
            }
        }

        private static void Osc9IgnoredWhenNotItermForm()
        {
            // xterm 的 OSC 9 还有 9;N 形式的图标/进度，不能当通知弹
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<TerminalNotification>();
                eng.NotificationReceived += (s, n) => seen.Add(n);
                eng.Feed("\u001b]9;4;0;50\u0007");
                Assert.Equal(0, seen.Count, "OSC 9;4 进度不算通知");
            }
        }

        private static void Osc777Notify()
        {
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<TerminalNotification>();
                eng.NotificationReceived += (s, n) => seen.Add(n);
                eng.Feed("\u001b]777;notify;Deploy;staging ok\u0007");
                Assert.Equal(1, seen.Count, "OSC 777 notify");
                Assert.Equal("Deploy", seen[0].Title, "OSC 777 标题");
                Assert.Equal("staging ok", seen[0].Body, "OSC 777 正文");
            }
        }

        private static void Osc99KittyTitleAndBody()
        {
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<TerminalNotification>();
                eng.NotificationReceived += (s, n) => seen.Add(n);
                // 分片：p=title 先来，正文后面补；u=2 => error
                eng.Feed("\u001b]99;i=1:p=title;Test suite\u0007");
                eng.Feed("\u001b]99;i=1:d=1;u=2;p=body;42 failed\u0007");
                Assert.Equal(1, seen.Count, "kitty 99 同一 id 合并成一条");
                Assert.Equal("Test suite", seen[0].Title, "kitty 99 标题");
                Assert.Equal("42 failed", seen[0].Body, "kitty 99 正文");
                Assert.Equal("error", seen[0].Urgency, "kitty 99 紧急度");
            }
        }

        private static void Osc99QueryIsSilent()
        {
            // kitty 能力查询 p=? 不能弹窗（否则每条命令都糊一次通知）
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<TerminalNotification>();
                eng.NotificationReceived += (s, n) => seen.Add(n);
                eng.Feed("\u001b]99;i=9:p=?\u0007");
                Assert.Equal(0, seen.Count, "p=? 查询静默");
            }
        }

        private static void Osc99IndependentChunk()
        {
            // d=2：独立单条通知，不需要前置标题分片
            using (var eng = new VtTerminalEngine(80, 24, 100))
            {
                var seen = new List<TerminalNotification>();
                eng.NotificationReceived += (s, n) => seen.Add(n);
                eng.Feed("\u001b]99;i=3:d=2;one\u0007");
                Assert.Equal(1, seen.Count, "d=2 直接发一条");
                Assert.Equal("one", seen[0].Body, "d=2 正文");
            }
        }
    }
}
