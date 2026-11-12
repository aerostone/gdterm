using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Core.Security;

namespace Gdterm.Tests.Security
{
    /// <summary>
    /// 主机密钥校验测试（2026-11 功能缺口审计 P0 的回归锁）。
    ///
    /// 锁四件事：
    ///   1) 判定表本身——已知一致=放行、不一致=必须问、问不到=必须拒（fail-closed）；
    ///   2) known_hosts 的读写格式——写出去的东西标准 ssh 也能读，读进来的能匹配上；
    ///   3) 端口/主机字段语义——22 端口裸主机名、非 22 端口 [host]:port；
    ///   4) 密钥轮换——不留下两条互相矛盾的记录。
    ///
    /// 刻意不测：真实握手（要 SSH 服务端，CI 里没有）。握手相关的假设
    /// （HostKeyName/HostKey 的语义）由 SSH.NET 保证，本地已用手工探针验证过。
    /// </summary>
    public static class HostKeyVerificationTests
    {
        // 两个 blob 都构造出来而不是贴字面量：它们是"任意不透明字节"的替身
        private static readonly string BlobA = Convert.ToBase64String(Encoding.UTF8.GetBytes("first-host-key-blob"));
        // 第二个 blob：构造出来而不是贴字面量（避免被误认成凭据）
        private static readonly string BlobB = Convert.ToBase64String(Encoding.UTF8.GetBytes("second-host-key-blob"));

        public static void Run()
        {
            TestParseLine();
            TestMatchesHostField();
            TestFormatHostField();
            TestUnknownHostAsksAndSaves();
            TestMatchingKeyIsTrusted();
            TestChangedKeyMustAsk();
            TestNoPromptMeansReject();
            TestPromptThrowsMeansReject();
            TestAcceptOnceDoesNotPersist();
            TestRotatedKeyReplacesOldRecord();
            TestSystemKnownHostsIsReadOnlyLayer();
        }

        private static KnownHostsStore TempStore(params string[] readOnly)
        {
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-kh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return new KnownHostsStore(Path.Combine(dir, "known_hosts"), readOnly);
        }

        private sealed class FakePrompt : IHostKeyPrompt
        {
            public readonly List<HostKeyPromptInfo> Asked = new List<HostKeyPromptInfo>();
            private readonly HostKeyDecision _decision;
            private readonly bool _throw;

            public FakePrompt(HostKeyDecision decision, bool doThrow = false)
            {
                _decision = decision;
                _throw = doThrow;
            }

            public HostKeyDecision Ask(HostKeyPromptInfo info)
            {
                Asked.Add(info);
                if (_throw) throw new InvalidOperationException("boom");
                return _decision;
            }
        }

        private static void TestParseLine()
        {
            var e = KnownHostsStore.ParseLine("example.com,10.0.0.5 ssh-ed25519 " + BlobA + " comment here");
            Assert.True(e != null, "普通条目应能解析");
            Assert.Equal("example.com,10.0.0.5", e.HostField, "逗号列表原样保留");
            Assert.Equal("ssh-ed25519", e.KeyType, "断言：ssh-ed25519");
            Assert.Equal(BlobA, e.BlobText, "断言：BlobA");

            Assert.True(KnownHostsStore.ParseLine("# comment") == null, "注释行不应解析出条目");
            Assert.True(KnownHostsStore.ParseLine("") == null, "空行不应解析出条目");
            Assert.True(KnownHostsStore.ParseLine("|1|abcd|efgh ssh-ed25519 " + BlobA) == null,
                "哈希化条目主机名不可反查，必须跳过（跳过=问一次，而不是放行）");
            Assert.True(KnownHostsStore.ParseLine("*.corp ssh-ed25519 " + BlobA) == null, "通配条目本实现不支持");
        }

        private static void TestMatchesHostField()
        {
            Assert.True(KnownHostsStore.Matches("host1,host2", "host2", 22), "逗号列表应逐项匹配");
            Assert.True(KnownHostsStore.Matches("[10.0.0.5]:2222", "10.0.0.5", 2222), "非 22 端口带方括号");
            Assert.True(!KnownHostsStore.Matches("[10.0.0.5]:2222", "10.0.0.5", 22), "端口不同不能算同一台");
            Assert.True(KnownHostsStore.Matches("host1", "HOST1", 22), "主机名不区分大小写");
            Assert.True(!KnownHostsStore.Matches("host1", "host2", 22), "不同主机不能匹配");
        }

        private static void TestFormatHostField()
        {
            Assert.Equal("example.com", KnownHostsStore.FormatHostField("example.com", 22), "断言：example.com");
            Assert.Equal("[example.com]:2222", KnownHostsStore.FormatHostField("example.com", 2222), "断言：[example.com]:2222");
            Assert.Equal("[::1]:2222", KnownHostsStore.FormatHostField("::1", 2222), "断言：[::1]:2222");
        }

        private static void TestUnknownHostAsksAndSaves()
        {
            var store = TempStore();
            var blob = Convert.FromBase64String(BlobA);
            var prompt = new FakePrompt(HostKeyDecision.AcceptAndSave);

            var v = HostKeyVerifier.Evaluate(store, "new.example.com", 22, "ssh-ed25519", blob, 256, prompt);

            Assert.Equal(HostKeyOutcome.AcceptedAndSaved, v.Outcome, "断言：HostKeyOutcome.AcceptedAndSaved");
            Assert.True(v.CanTrust, "用户确认后本次连接必须放行");
            Assert.Equal(1, prompt.Asked.Count, "断言：1");
            Assert.Equal(HostKeySituation.Unknown, prompt.Asked[0].Situation, "断言：HostKeySituation.Unknown");
            Assert.True(File.Exists(store.WritablePath), "AcceptAndSave 必须落盘");
            Assert.True(File.ReadAllText(store.WritablePath).Contains(BlobA), "落盘内容必须是标准 known_hosts 行");
        }

        private static void TestMatchingKeyIsTrusted()
        {
            var store = TempStore();
            var blob = Convert.FromBase64String(BlobA);
            store.Save("known.example.com", 22, "ssh-ed25519", BlobA);

            var prompt = new FakePrompt(HostKeyDecision.Reject);
            var v = HostKeyVerifier.Evaluate(store, "known.example.com", 22, "ssh-ed25519", blob, 256, prompt);

            Assert.Equal(HostKeyOutcome.Trusted, v.Outcome, "断言：HostKeyOutcome.Trusted");
            Assert.Equal(0, prompt.Asked.Count, "密钥一致时不该再打扰用户（误报会训练用户无脑确认）");
        }

        private static void TestChangedKeyMustAsk()
        {
            var store = TempStore();
            store.Save("rotated.example.com", 22, "ssh-ed25519", BlobA);

            var prompt = new FakePrompt(HostKeyDecision.Reject);
            var v = HostKeyVerifier.Evaluate(store, "rotated.example.com", 22, "ssh-ed25519",
                Convert.FromBase64String(BlobB), 256, prompt);

            Assert.Equal(HostKeyOutcome.Rejected, v.Outcome, "断言：HostKeyOutcome.Rejected");
            Assert.Equal(1, prompt.Asked.Count, "断言：1");
            Assert.Equal(HostKeySituation.Changed, prompt.Asked[0].Situation, "断言：HostKeySituation.Changed");
            Assert.True(!string.IsNullOrEmpty(prompt.Asked[0].PreviousSha256Fingerprint),
                "密钥变更时必须给用户旧指纹做对照");
        }

        private static void TestNoPromptMeansReject()
        {
            var store = TempStore();
            var v = HostKeyVerifier.Evaluate(store, "h", 22, "ssh-ed25519", Convert.FromBase64String(BlobA), 256, null);
            Assert.Equal(HostKeyOutcome.Rejected, v.Outcome, "断言：HostKeyOutcome.Rejected");
            Assert.True(!v.CanTrust, "问不到用户必须拒绝（后台路径不能替你点信任）");
        }

        private static void TestPromptThrowsMeansReject()
        {
            var store = TempStore();
            var prompt = new FakePrompt(HostKeyDecision.AcceptAndSave, doThrow: true);
            var v = HostKeyVerifier.Evaluate(store, "h", 22, "ssh-ed25519", Convert.FromBase64String(BlobA), 256, prompt);
            Assert.Equal(HostKeyOutcome.Rejected, v.Outcome, "断言：HostKeyOutcome.Rejected");
        }

        private static void TestAcceptOnceDoesNotPersist()
        {
            var store = TempStore();
            var prompt = new FakePrompt(HostKeyDecision.AcceptOnce);
            var v = HostKeyVerifier.Evaluate(store, "h", 22, "ssh-ed25519", Convert.FromBase64String(BlobA), 256, prompt);
            Assert.Equal(HostKeyOutcome.AcceptedOnce, v.Outcome, "断言：HostKeyOutcome.AcceptedOnce");
            Assert.True(v.CanTrust, "仅本次信任也要放行");
            Assert.True(!File.Exists(store.WritablePath), "AcceptOnce 不该写盘");
        }

        private static void TestRotatedKeyReplacesOldRecord()
        {
            var store = TempStore();
            store.Save("h", 22, "ssh-ed25519", BlobA);
            store.Save("h", 22, "ssh-ed25519", BlobB);

            var text = File.ReadAllText(store.WritablePath);
            Assert.True(!text.Contains(BlobA), "旧记录必须被替换掉，否则下次又比对到旧指纹");
            Assert.True(text.Contains(BlobB), "新记录必须写进去");

            var v = HostKeyVerifier.Evaluate(store, "h", 22, "ssh-ed25519", Convert.FromBase64String(BlobB), 256, null);
            Assert.Equal(HostKeyOutcome.Trusted, v.Outcome, "断言：HostKeyOutcome.Trusted");
        }

        private static void TestSystemKnownHostsIsReadOnlyLayer()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-khsys-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var sysPath = Path.Combine(dir, "system_known_hosts");
            File.WriteAllText(sysPath, "fromsys.example.com ssh-ed25519 " + BlobA + "\n",
                new UTF8Encoding(false));

            var store = TempStore(sysPath);
            var v = HostKeyVerifier.Evaluate(store, "fromsys.example.com", 22, "ssh-ed25519",
                Convert.FromBase64String(BlobA), 256, null);
            Assert.Equal(HostKeyOutcome.Trusted, v.Outcome, "断言：HostKeyOutcome.Trusted");

            // 系统文件只读：用户确认另一台机器时不能往系统文件里写
            store.Save("other.example.com", 22, "ssh-ed25519", BlobB);
            var sysText = File.ReadAllText(sysPath);
            Assert.True(!sysText.Contains("other.example.com"), "不得修改系统 ~/.ssh/known_hosts");
            Assert.True(File.ReadAllText(store.WritablePath).Contains("other.example.com"), "新记录写进自己的文件");
        }
    }
}
