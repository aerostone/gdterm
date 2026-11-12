using System;
using System.Collections.Generic;
using System.IO;
using Gdterm.Core.Models;
using Gdterm.Security;

namespace Gdterm.Tests.Security
{
    /// <summary>
    /// 命令闸门测试（2026-11 审计的回归锁）。
    ///
    /// 锁三件事：
    ///   1) 多行整批判定——粘贴/宏/脚本是一批文本，不是一条命令；
    ///   2) 规则精度——`rm -rf /tmp/x` 不该报“删根”（误报会训练用户无脑确认）；
    ///   3) 配置分层——内置规则只读 + 用户 delta，收紧的正则能到老用户机器上。
    /// </summary>
    public static class CommandGateTests
    {
        public static void Run()
        {
            TestSplitLines();
            TestBatchInspect();
            TestPrecisionNoFalsePositiveOnSubdirs();
            TestStillCatchesRealDestruction();
            TestSessionRuleExemption();
            TestPermanentRuleExemptionPersists();
            TestBuiltinRulesAreReadOnly();
            TestUserDeltaSurvivesMerge();
            TestExcludePatternVetoesCustomRule();
        }

        private static string TempConfigPath()
        {
            return Path.Combine(Path.GetTempPath(), "gdterm-gate-" + Guid.NewGuid().ToString("N") + ".json");
        }

        private static void TestSplitLines()
        {
            var crlf = CommandGate.SplitLines("ls\r\nrm -rf /\r\n");
            Assert.Equal(2, crlf.Count, "SplitLines: \\r\\n 切两行");

            var lf = CommandGate.SplitLines("a\nb\n\nc");
            Assert.Equal(3, lf.Count, "SplitLines: 空行丢弃");

            var cr = CommandGate.SplitLines("a\rb");
            Assert.Equal(2, cr.Count, "SplitLines: 纯 \\r（bracketed paste 体）也切");

            Assert.Equal(0, CommandGate.SplitLines("   ").Count, "SplitLines: 全空白不产生行");
        }

        private static void TestBatchInspect()
        {
            var gate = new CommandGate(new DangerousCommandDetector());
            var text = "cd /var/log\nls -l\nrm -rf /\necho done";

            var decision = gate.Inspect(text, InputSource.Paste);

            Assert.True(decision.IsBatch, "Inspect: 多行输入标记为 batch");
            Assert.Equal(4, decision.LineCount, "Inspect: 统计总行数（弹窗要说清 4 行里有几行命中）");
            Assert.Equal(1, decision.Hits.Count, "Inspect: 只命中危险的那一行");
            Assert.Equal(3, decision.Hits[0].LineNumber, "Inspect: 命中的行号是 3");
            Assert.Equal(DangerLevel.Critical, decision.HighestLevel, "Inspect: 最高等级 Critical");
            Assert.Equal(3, decision.ConfirmCount, "Inspect: 确认次数取最高等级那条，不按行累加");
        }

        private static void TestPrecisionNoFalsePositiveOnSubdirs()
        {
            var detector = new DangerousCommandDetector();

            foreach (var benign in new[]
            {
                "rm -rf /tmp/build",
                "rm -rf ./node_modules",
                "rm -rf ~/build",
                "chmod -R 755 /home/app",
                "chown -R app:app /srv/app",
                "chmod 755 /usr/local/bin/tool"
            })
            {
                var check = detector.Check(benign);
                Assert.True(!check.IsDangerous, "精度: 不该拦 [" + benign + "]，实际命中 [" + check.RuleName + "]");
            }
        }

        private static void TestStillCatchesRealDestruction()
        {
            var detector = new DangerousCommandDetector();

            var cases = new Dictionary<string, string>
            {
                { "rm -rf /", "rm-rf-root" },
                { "rm -rf /*", "rm-rf-root-star" },
                { "rm -rf ~", "rm-rf-home" },
                { "rm -rf /etc", "rm-system-dirs" },
                { "rm -rf /etc/*", "rm-system-dirs" },
                { "chmod -R 777 /", "chmod-777-root" },
                { "chown -R nobody /", "chown-r-root" },
            };

            foreach (var kv in cases)
            {
                var check = detector.Check(kv.Key);
                Assert.True(check.IsDangerous, "覆铜: [" + kv.Key + "] 仍必须判危险");
                Assert.Equal(kv.Value, check.RuleId, "覆铜: [" + kv.Key + "] 命中规则 Id");
                Assert.Equal(DangerLevel.Critical, check.Level, "覆铜: [" + kv.Key + "] 等级 Critical");
            }
        }

        private static void TestSessionRuleExemption()
        {
            var gate = new CommandGate(new DangerousCommandDetector());

            var before = gate.Inspect("rm -rf /", InputSource.Keyboard);
            Assert.True(before.NeedsConfirm, "会话豁免: 豁免前命中");

            gate.ExemptRuleForSession("rm-rf-root");

            var after = gate.Inspect("rm -rf /", InputSource.Keyboard);
            Assert.True(!after.NeedsConfirm, "会话豁免: 豁免后不再打扰");
            Assert.Equal(1, after.Exempted.Count, "会话豁免: 记为 Exempted（审计要能查）");

            // 粒度是规则：同一规则的其它形态也一起豁免，别的规则不受影响
            Assert.True(!gate.Inspect("rm -rf  /", InputSource.Keyboard).NeedsConfirm,
                "会话豁免: 同一规则的另一种写法也豁免");
            Assert.True(gate.Inspect("mkfs.ext4 /dev/sdb1", InputSource.Keyboard).NeedsConfirm,
                "会话豁免: 不牵连其它规则");

            gate.ClearSessionExemptions();
            Assert.True(gate.Inspect("rm -rf /", InputSource.Keyboard).NeedsConfirm,
                "会话豁免: Clear 后恢复拦截");
        }

        private static void TestPermanentRuleExemptionPersists()
        {
            var path = TempConfigPath();
            try
            {
                var detector = new DangerousCommandDetector(path);
                Assert.True(detector.Check("rm -rf /").IsDangerous, "永久豁免: 初始拦得住");

                detector.AddRuleToWhitelist("rm-rf-root");
                Assert.True(!detector.Check("rm -rf /").IsDangerous, "永久豁免: 本进程立即生效");
                Assert.True(File.Exists(path), "永久豁免: 已落盘");
                var json = File.ReadAllText(path);
                Assert.True(json.IndexOf("rule:rm-rf-root", StringComparison.OrdinalIgnoreCase) >= 0,
                    "永久豁免: 配置存成 rule:<id>（而不是整条命令）");

                var reloaded = new DangerousCommandDetector(path);
                Assert.True(!reloaded.Check("rm -rf /").IsDangerous, "永久豁免: 重启后仍生效");
                Assert.True(reloaded.Check("rm -rf /etc").IsDangerous, "永久豁免: 只豁免该规则，别的规则照拦");

                reloaded.RemoveRuleFromWhitelist("rm-rf-root");
                Assert.True(reloaded.Check("rm -rf /").IsDangerous, "永久豁免: 可撤销");
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static void TestBuiltinRulesAreReadOnly()
        {
            Assert.True(DangerousCommandDetector.IsBuiltinRule("rm-rf-root"), "只读: 内置 Id 识别");
            Assert.True(!DangerousCommandDetector.IsBuiltinRule("my-own-rule"), "只读: 自建 Id 不算内置");

            var detector = new DangerousCommandDetector();
            var edited = new DangerousCommandRule
            {
                Id = "rm-rf-root",
                Name = "改坏了",
                Pattern = "never-matches-anything",
                PatternType = PatternType.Regex,
                Level = DangerLevel.Medium
            };

            Assert.True(!detector.UpdateRule("rm-rf-root", edited), "只读: 内置规则不可改");
            Assert.True(!detector.RemoveRule("rm-rf-root"), "只读: 内置规则不可删");
            Assert.True(detector.Check("rm -rf /").IsDangerous, "只读: 拒绝改动后内置规则仍然生效");

            // 但“启用/禁用”是允许的（用户确实可能不需要某条）
            Assert.True(detector.SetRuleEnabled("rm-rf-root", false), "只读: 允许禁用内置规则");
            Assert.True(!detector.Check("rm -rf /").IsDangerous, "只读: 禁用后不再拦");
        }

        private static void TestUserDeltaSurvivesMerge()
        {
            var path = TempConfigPath();
            try
            {
                // 模拟“老用户机器上的旧配置”：内置规则是过期副本（宽正则），另一条被用户关掉，外加一条自建规则
                var oldJson = @"{
  ""enabled"": true,
  ""applyToBroadcast"": true,
  ""whitelist"": [],
  ""rules"": [
    {""id"": ""rm-rf-root"", ""name"": ""递归删除根目录"", ""pattern"": ""rm\\s+.*-[^\\s]*r[^\\s]*f[^\\s]*\\s+/"", ""patternType"": ""Regex"", ""level"": ""Critical"", ""confirmCount"": 3, ""description"": ""旧"", ""category"": ""filesystem"", ""enabled"": true},
    {""id"": ""git-push-force"", ""name"": ""强制推送"", ""pattern"": ""git push.*force"", ""patternType"": ""Regex"", ""level"": ""High"", ""confirmCount"": 2, ""description"": ""旧"", ""category"": ""git"", ""enabled"": false},
    {""id"": ""team-rule"", ""name"": ""删库"", ""pattern"": ""drop\\s+database"", ""patternType"": ""Regex"", ""level"": ""Critical"", ""confirmCount"": 3, ""description"": ""自建"", ""category"": ""db"", ""enabled"": true}
  ]
}";
                File.WriteAllText(path, oldJson);

                var detector = new DangerousCommandDetector(path);

                // 收紧后的内置正则必须覆盖旧副本：/tmp/xxx 不再报“删根”
                Assert.True(!detector.Check("rm -rf /tmp/build").IsDangerous,
                    "delta: 旧配置里的宽正则被代码版本覆盖（不再误报 /tmp）");
                Assert.True(detector.Check("rm -rf /").IsDangerous, "delta: 收紧后仍拦真正的删根");
                Assert.True(detector.Check("rm -rf /etc").IsDangerous,
                    "delta: 新补的 rm-system-dirs 规则对老用户也生效");

                // 用户的“关掉这条规则”要保留
                Assert.True(!detector.Check("git push --force origin main").IsDangerous,
                    "delta: 用户禁用的内置规则仍然禁用");

                // 自建规则原样保留
                var custom = detector.Check("drop database prod");
                Assert.True(custom.IsDangerous, "delta: 自建规则保留并生效");
                Assert.Equal("team-rule", custom.RuleId, "delta: 命中自建规则 Id");
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static void TestExcludePatternVetoesCustomRule()
        {
            var detector = new DangerousCommandDetector();
            detector.AddRule(new DangerousCommandRule
            {
                Id = "no-npm-uninstall",
                Name = "卸载依赖",
                Pattern = @"npm\s+uninstall",
                PatternType = PatternType.Regex,
                Level = DangerLevel.Medium,
                ConfirmCount = 1,
                Category = "package",
                Exclude = @"npm\s+uninstall\s+--save-dev"
            });

            Assert.True(detector.Check("npm uninstall lodash").IsDangerous, "exclude: 规则本身生效");
            Assert.True(!detector.Check("npm uninstall --save-dev lodash").IsDangerous,
                "exclude: 否决模式把已知安全的形态摘出去");

            detector.RemoveRule("no-npm-uninstall");
        }
    }
}
