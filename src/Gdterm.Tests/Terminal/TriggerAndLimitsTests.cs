using System;
using System.Collections.Generic;
using Gdterm.Core.Constants;
using Gdterm.Core.Models;
using Gdterm.Terminal;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// P1-5 回滚行数边界、P1-7 触发器（HighlightEngine.MatchTriggers）、P0-2 代理配置语义。
    /// 纯逻辑断言，不碰网络。
    /// </summary>
    public static class TriggerAndLimitsTests
    {
        public static void Run()
        {
            Console.WriteLine("-- 触发器 / 回滚边界 / 代理配置 --");
            ScrollbackClamp();
            TriggerHitOnlyForActionRules();
            TriggerCooldownDefaultsToFive();
            TriggerRespectsDisabled();
            ProxyUsability();
        }

        private static void ScrollbackClamp()
        {
            Assert.Equal(0, TerminalLimits.ClampScrollback(-5, 0), "负数收敛到 0");
            Assert.Equal(TerminalLimits.MaxScrollback, TerminalLimits.ClampScrollback(int.MaxValue, 24), "超大收敛到上限");
            Assert.Equal(24, TerminalLimits.ClampScrollback(10, 24), "低于一屏时抬到一屏");
            Assert.Equal(5000, TerminalLimits.ClampScrollback(5000, 24), "正常值原样");
            // 引擎层不套 MinScrollback：显式要 30 行就得给 30 行（设置界面才套下限）
            Assert.Equal(30, TerminalLimits.ClampScrollback(30, 0), "引擎层不受界面下限影响");
        }

        private static void TriggerHitOnlyForActionRules()
        {
            var rules = new List<HighlightRule>
            {
                new HighlightRule { Id = "a", Name = "只高亮", Pattern = "ERROR", Enabled = true },
                new HighlightRule
                {
                    Id = "b", Name = "发命令", Pattern = "Password:", Enabled = true,
                    Action = HighlightAction.SendCommand, ActionArgument = "hunter2", ActionCooldownSeconds = 3
                }
            };
            using (var eng = new HighlightEngine(rules))
            {
                Assert.True(eng.HasTriggers, "有动作规则时 HasTriggers 为真");

                var none = eng.MatchTriggers("ERROR: something");
                Assert.Equal(0, none.Count, "纯高亮规则不产生触发器");

                var hits = eng.MatchTriggers("Password: ");
                Assert.Equal(1, hits.Count, "有动作规则命中");
                Assert.Equal(HighlightAction.SendCommand, hits[0].Action, "动作透传");
                Assert.Equal("hunter2", hits[0].Argument, "参数透传");
                Assert.Equal(3, hits[0].CooldownSeconds, "冷却透传");
            }
        }

        private static void TriggerCooldownDefaultsToFive()
        {
            var rule = new HighlightRule { Pattern = "x" };
            Assert.Equal(5, rule.ActionCooldownSeconds, "默认冷却 5 秒（防 cat 大文件刷屏）");
            Assert.Equal(HighlightAction.None, rule.Action, "默认动作 None（老配置行为不变）");
        }

        private static void TriggerRespectsDisabled()
        {
            var rules = new List<HighlightRule>
            {
                new HighlightRule
                {
                    Id = "c", Name = "关掉的", Pattern = "boom", Enabled = false,
                    Action = HighlightAction.Notify, ActionArgument = "炸了"
                }
            };
            using (var eng = new HighlightEngine(rules))
            {
                Assert.Equal(0, eng.MatchTriggers("boom").Count, "禁用规则不触发");
                Assert.True(!eng.HasTriggers, "只有禁用规则时 HasTriggers 为假（省掉整条扫描路径）");
            }
        }

        private static void ProxyUsability()
        {
            Assert.Equal(8080, ProxyConfig.DefaultPort(ProxyKind.Http), "HTTP 代理默认端口");
            Assert.Equal(1080, ProxyConfig.DefaultPort(ProxyKind.Socks4), "SOCKS4 默认端口");
            Assert.Equal(1080, ProxyConfig.DefaultPort(ProxyKind.Socks5), "SOCKS5 默认端口");

            var none = new ProxyConfig();
            Assert.True(!none.IsUsable, "默认（None）不可用");

            var noPort = new ProxyConfig { Kind = ProxyKind.Http, Host = "proxy.corp" };
            Assert.True(!noPort.IsUsable, "没填端口不可用（UI 会按类型回落默认端口）");

            var http = new ProxyConfig { Kind = ProxyKind.Http, Host = "proxy.corp", Port = 8080 };
            Assert.True(http.IsUsable, "类型+主机+端口齐了才可用");

            var empty = new ProxyConfig { Kind = ProxyKind.Http, Host = "  ", Port = 8080 };
            Assert.True(!empty.IsUsable, "主机为空白不可用");

            var badPort = new ProxyConfig { Kind = ProxyKind.Socks5, Host = "h", Port = 70000 };
            Assert.True(!badPort.IsUsable, "端口越界不可用");
        }
    }
}
