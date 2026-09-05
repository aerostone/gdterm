using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Tools.Scanning;

namespace Gdterm.Tests.Scanning
{
    /// <summary>
    /// 扫描插件体系纯逻辑回归：输出契约解析（StripFindingLines）、severity 归一化、
    /// manifest 版本比较、插件目录路径逃逸防护、TOCTOU 双哈希复验、批准台账哈希记账。
    /// </summary>
    public static class ScanPluginTests
    {
        public static void Run()
        {
            ConsoleWrite("ScanPluginStripFindings");
            StripFindingLinesTests();
            ConsoleWrite("ScanPluginSeverity");
            NormalizeSeverityTests();
            ConsoleWrite("ScanPluginStore");
            StoreLoadTests();
        }

        private static void ConsoleWrite(string name)
        {
            Console.WriteLine("-- " + name);
        }

        // ===== 输出契约：FINDING|severity|title|detail =====

        private static void StripFindingLinesTests()
        {
            var sink = new List<ScanFinding>();

            var log = ScanRunner.StripFindingLines(
                "line1\r\nFINDING|high|弱密码策略|密码最短 4 位\r\nline2\r\nFINDING|warn|旧协议|SMBv1 已启用|尾部多余分隔\r\nFINDING|critical||空标题丢弃\r\nFINDING|bogus|非法级别归 info|detail",
                sink, "本机", "p1");

            Assert.Equal(3, sink.Count, "3 findings kept");
            Assert.Equal("high", sink[0].Severity, "finding severity passthrough");
            Assert.Equal("弱密码策略", sink[0].Title, "finding title");
            Assert.Equal("密码最短 4 位", sink[0].Detail, "4th split keeps detail with | inside");
            Assert.Equal("medium", sink[1].Severity, "warn normalized to medium");
            Assert.Equal("info", sink[2].Severity, "bogus severity to info");
            Assert.Equal("p1", sink[0].PluginId, "plugin id stamped");
            Assert.Equal("本机", sink[0].TargetName, "target name stamped");
            Assert.NotContains(log, "FINDING|", "finding lines stripped from raw log");
            Assert.Contains(log, "line1", "plain log lines kept");
            Assert.Contains(log, "line2", "plain log lines kept 2");

            // 上限 200 条
            var bigSink = new List<ScanFinding>();
            var sb = new StringBuilder();
            for (int i = 0; i < 210; i++) sb.Append("FINDING|info|t").Append(i).Append("|d\r\n");
            ScanRunner.StripFindingLines(sb.ToString(), bigSink, "t", "p");
            Assert.Equal(200, bigSink.Count, "findings capped at 200");

            Assert.Equal("", ScanRunner.StripFindingLines(null, sink, "t", "p"), "null stdout -> empty log");
            Assert.Equal("", ScanRunner.StripFindingLines("", sink, "t", "p"), "empty stdout -> empty log");
        }

        // ===== severity 归一化 =====

        private static void NormalizeSeverityTests()
        {
            Assert.Equal("critical", ScanRunner.NormalizeSeverity("CRITICAL"), "critical upper");
            Assert.Equal("high", ScanRunner.NormalizeSeverity(" error "), "error to high");
            Assert.Equal("medium", ScanRunner.NormalizeSeverity("Warning"), "warning to medium");
            Assert.Equal("info", ScanRunner.NormalizeSeverity(""), "empty to info");
            Assert.Equal("info", ScanRunner.NormalizeSeverity(null), "null to info");
            Assert.Equal("info", ScanRunner.NormalizeSeverity("whatever"), "unknown to info");
            Assert.Equal("low", ScanRunner.NormalizeSeverity("low"), "low passthrough");
        }

        // ===== 仓库加载：路径逃逸防护 / TOCTOU 双哈希 / 台账 =====

        private static void StoreLoadTests()
        {
            var root = NewTempDir();
            try
            {
                var store = new ScanPluginStore(Path.Combine(root, "builtin"), Path.Combine(root, "user"));

                // -- 合法插件 --
                var good = Path.Combine(root, "user", "good-plugin");
                Directory.CreateDirectory(good);
                File.WriteAllText(Path.Combine(good, "manifest.json"), Manifest("good-plugin", "scan.ps1"), Utf8);
                File.WriteAllText(Path.Combine(good, "scan.ps1"), "Write-Output hi", Utf8);

                // -- ../ 逃逸（JSON 内反斜杠需双写转义）--
                var evil = Path.Combine(root, "user", "evil-plugin");
                Directory.CreateDirectory(evil);
                File.WriteAllText(Path.Combine(evil, "manifest.json"), Manifest("evil-plugin", "..\\\\payload.ps1"), Utf8);
                File.WriteAllText(Path.Combine(root, "user", "payload.ps1"), "evil", Utf8);

                // -- 同级前缀目录绕过（good-plugin-extra 逃逸进 good-plugin）--
                var prefixSibling = Path.Combine(root, "user", "good-plugin-extra");
                Directory.CreateDirectory(prefixSibling);
                File.WriteAllText(Path.Combine(prefixSibling, "manifest.json"), Manifest("good-plugin-extra", "..\\\\good-plugin\\\\scan.ps1"), Utf8);

                // -- 缺 manifest --
                var noManifest = Path.Combine(root, "user", "no-manifest");
                Directory.CreateDirectory(noManifest);
                File.WriteAllText(Path.Combine(noManifest, "scan.ps1"), "x", Utf8);

                // -- targets 为空 --
                var noTargets = Path.Combine(root, "user", "no-targets");
                Directory.CreateDirectory(noTargets);
                File.WriteAllText(Path.Combine(noTargets, "manifest.json"),
                    "{\"id\":\"no-targets\",\"name\":\"n\",\"targets\":[],\"scriptFile\":\"s.ps1\"}", Utf8);

                store.Reload();
                var plugins = store.Plugins;
                var byId = new Dictionary<string, ScanPlugin>();
                foreach (var p in plugins) byId[p.Id] = p;

                Assert.True(byId.ContainsKey("good-plugin"), "good plugin loaded");
                Assert.True(byId["good-plugin"].IsRunnable, "good plugin runnable");
                Assert.Equal("good-plugin", byId["good-plugin"].Manifest.Id, "manifest deserialized");
                Assert.True(byId["good-plugin"].VerifiedScriptSha256 != null, "script hash captured at load");
                Assert.True(byId["good-plugin"].VerifiedManifestSha256 != null, "manifest hash captured at load");
                Assert.Equal(ScanTrust.Unsigned, byId["good-plugin"].Trust, "unsigned plugin is Unsigned");

                Assert.True(byId.ContainsKey("evil-plugin"), "evil plugin entry exists");
                Assert.Contains(byId["evil-plugin"].LoadError, "越出插件目录", "../ escape rejected");
                Assert.True(!byId["evil-plugin"].IsRunnable, "escaped plugin not runnable");

                Assert.True(byId.ContainsKey("good-plugin-extra"), "prefix-sibling entry exists");
                Assert.Contains(byId["good-plugin-extra"].LoadError, "越出插件目录", "sibling-prefix escape rejected");

                Assert.Contains(byId["no-manifest"].LoadError, "manifest.json", "missing manifest reported");
                Assert.Contains(byId["no-targets"].LoadError, "targets", "empty targets reported");

                // -- TOCTOU：脚本被换后复验拒绝（先在干净内容上建插件快照，再换文件）--
                var goodDir = Path.GetDirectoryName(byId["good-plugin"].ScriptPath);
                var snapshot = MakePlugin(goodDir);   // 快照哈希 = 干净内容
                File.WriteAllText(byId["good-plugin"].ScriptPath, "Write-Output EVIL", Utf8);
                var r1 = new ScanRunner().RunOne(snapshot, new NullChannel());
                Assert.Contains(r1.RuntimeError, "脚本内容在加载后被变更", "tampered script rejected at run");

                // -- TOCTOU：manifest 被改（换超时/目标）同样拒绝 --
                var clean = Path.Combine(root, "user", "clean-plugin");
                Directory.CreateDirectory(clean);
                File.WriteAllText(Path.Combine(clean, "manifest.json"), Manifest("clean-plugin", "s.ps1"), Utf8);
                File.WriteAllText(Path.Combine(clean, "s.ps1"), "ok", Utf8);
                store.Reload();
                var cleanPlugin = null as ScanPlugin;
                foreach (var p in store.Plugins) if (p.Id == "clean-plugin") cleanPlugin = p;
                File.WriteAllText(Path.Combine(clean, "manifest.json"), Manifest("clean-plugin", "s.ps1") + "\n", Utf8);
                var r2 = new ScanRunner().RunOne(cleanPlugin, new NullChannel());
                Assert.Contains(r2.RuntimeError, "manifest 在加载后被变更", "tampered manifest rejected at run");

                // -- 批准台账：按 id+内容哈希记账，内容变更后重新问 --
                Assert.True(!store.IsApproved(cleanPlugin), "unapproved plugin not approved");
                store.Approve(cleanPlugin);
                Assert.True(store.IsApproved(cleanPlugin), "approved after Approve");
                File.WriteAllText(Path.Combine(clean, "s.ps1"), "changed", Utf8);
                store.Reload();
                var cleanPlugin2 = null as ScanPlugin;
                foreach (var p in store.Plugins) if (p.Id == "clean-plugin") cleanPlugin2 = p;
                Assert.True(!store.IsApproved(cleanPlugin2), "approval invalidated after content change");

                // -- 版本感知：CompareVersions 私有，经由 RefreshOutdatedBuiltin 间接覆盖；
                //    这里只验证 manifest version 解析路径（加载不抛异常即通过）--
                Assert.True(cleanPlugin2.Manifest.Version == "1.0.0", "version roundtrip");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static ScanPlugin MakePlugin(string pluginDir)
        {
            var manifestPath = Path.Combine(pluginDir, "manifest.json");
            var scriptPath = Path.Combine(pluginDir, "scan.ps1");
            return new ScanPlugin
            {
                Manifest = new ScanPluginManifest
                {
                    Id = Path.GetFileName(pluginDir),
                    Name = Path.GetFileName(pluginDir),
                    Targets = new List<string> { "windows" },
                    ScriptFile = "scan.ps1",
                    TimeoutSeconds = 5
                },
                ScriptPath = scriptPath,
                Source = "test",
                Trust = ScanTrust.Unsigned,
                VerifiedScriptSha256 = ScanPluginStore.ScriptSha256Hex(scriptPath),
                VerifiedManifestSha256 = ScanPluginStore.FileSha256Hex(manifestPath)
            };
        }

        private static string Manifest(string id, string scriptFile)
        {
            return "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"description\":\"d\",\"category\":\"c\","
                 + "\"targets\":[\"windows\"],\"scriptFile\":\"" + scriptFile + "\",\"timeoutSeconds\":60,\"version\":\"1.0.0\",\"enabled\":true}";
        }

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-scan-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>永不执行的空通道——只用于触发 RunOne 的哈希门禁路径。</summary>
        private sealed class NullChannel : IScanChannel
        {
            public string Name { get { return "null"; } }
            public bool Supports(string scriptKind) { return true; }
            public ScanExecutionOutput Execute(ScanPlugin plugin, string scriptContent, int timeoutSeconds)
            {
                return new ScanExecutionOutput { ExitCode = 0, Stdout = "", Stderr = "" };
            }
        }
    }
}
