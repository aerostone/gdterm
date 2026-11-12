using System;

namespace Gdterm.Core.Security
{
    /// <summary>用户对一颗未知/变更的主机公钥的选择。</summary>
    public enum HostKeyDecision
    {
        /// <summary>拒绝（默认；不确认就等于不可信）。</summary>
        Reject = 0,

        /// <summary>只在本次连接信任，不落盘。</summary>
        AcceptOnce = 1,

        /// <summary>信任并写进 known_hosts。</summary>
        AcceptAndSave = 2
    }

    /// <summary>为什么会问用户。</summary>
    public enum HostKeySituation
    {
        /// <summary>该主机:端口没有记录（首次连接）。</summary>
        Unknown = 0,

        /// <summary>记录过，但本次出示的密钥与记录不一致——可能是换了机器，也可能是中间人。</summary>
        Changed = 1
    }

    /// <summary>弹窗需要展示的全部信息（不依赖 SSH.NET，UI 与 Core 解耦）。</summary>
    public sealed class HostKeyPromptInfo
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string KeyType { get; set; }
        public int KeyBits { get; set; }
        public string Sha256Fingerprint { get; set; }
        public string Md5Fingerprint { get; set; }
        public HostKeySituation Situation { get; set; }

        /// <summary>密钥变更时，known_hosts 里旧记录的指纹——给用户做对照。</summary>
        public string PreviousSha256Fingerprint { get; set; }

        public string Target { get { return KnownHostsStore.FormatHostField(Host, Port); } }
    }

    /// <summary>
    /// 主机密钥确认的唯一 UI 出口（由 UI 层实现；Core 不引用 WinForms）。
    /// 返回 null 视为拒绝（实现方不得用 null 表达"放行"）。
    /// </summary>
    public interface IHostKeyPrompt
    {
        HostKeyDecision Ask(HostKeyPromptInfo info);
    }

    /// <summary>校验结果分类（写审计/日志时区分"放行"的三种理由）。</summary>
    public enum HostKeyOutcome
    {
        /// <summary>未配置 known_hosts（测试/无 UI 工具路径），未做校验。</summary>
        Disabled = 0,

        /// <summary>命中已有记录，密钥一致。</summary>
        Trusted = 1,

        /// <summary>用户选择本次信任。</summary>
        AcceptedOnce = 2,

        /// <summary>用户选择信任并写入 known_hosts。</summary>
        AcceptedAndSaved = 3,

        /// <summary>拒绝（或无法询问），连接必须失败。</summary>
        Rejected = 4
    }

    /// <summary>一次校验的结论。Message 是要进日志/异常原文的一句话理由。</summary>
    public sealed class HostKeyVerdict
    {
        public HostKeyOutcome Outcome { get; set; }
        public string Message { get; set; }

        /// <summary>
        /// 是否允许继续握手。Disabled 也算放行——那是"根本没配置 known_hosts"的测试/工具路径，
        /// 由组合根负责在应用本体里配置（tools/check-sec-gate.py S06 盯着这件事）。
        /// 注意：这里多算一个 Disabled 是必须的，否则未配置时全仓连接会被自己拦死。
        /// </summary>
        public bool CanTrust
        {
            get
            {
                return Outcome == HostKeyOutcome.Disabled
                    || Outcome == HostKeyOutcome.Trusted
                    || Outcome == HostKeyOutcome.AcceptedOnce
                    || Outcome == HostKeyOutcome.AcceptedAndSaved;
            }
        }

        public static HostKeyVerdict Reject(string message)
        {
            return new HostKeyVerdict { Outcome = HostKeyOutcome.Rejected, Message = message };
        }
    }

    /// <summary>
    /// 主机密钥判定（纯逻辑，可单测：不碰 SSH.NET、不碰文件系统以外的状态）。
    ///
    /// 判定表（fail-closed）：
    ///   store 为空            → Disabled（未配置即未校验；由组合根负责配置，见 SshHostKeyGuard）
    ///   命中 + 密钥一致        → Trusted
    ///   命中 + 密钥不一致      → Changed → 问用户 → 拒绝/仅本次/保存
    ///   未命中                → Unknown → 问用户 → 拒绝/仅本次/保存
    ///   问不到（prompt 为 null 或抛异常）→ Rejected
    ///
    /// 刻意不做的事：不静默追加 trusted key（那等于没有校验），不因"主机名对但算法变了"就放行，
    /// 也不把异常当放行——判定过程中任何异常都走 Rejected。
    /// </summary>
    public static class HostKeyVerifier
    {
        public static HostKeyVerdict Evaluate(
            KnownHostsStore store,
            string host,
            int port,
            string keyType,
            byte[] hostKeyBlob,
            int keyBits,
            IHostKeyPrompt prompt)
        {
            if (store == null)
            {
                return new HostKeyVerdict
                {
                    Outcome = HostKeyOutcome.Disabled,
                    Message = "known_hosts 未配置，未校验主机密钥"
                };
            }
            if (string.IsNullOrEmpty(host)) return HostKeyVerdict.Reject("主机名为空，无法校验主机密钥");
            if (hostKeyBlob == null || hostKeyBlob.Length == 0) return HostKeyVerdict.Reject("服务端未出示主机公钥");

            var sha256 = KnownHostsStore.FingerprintSha256(hostKeyBlob);
            var blobText = KnownHostsStore.ToBlobText(hostKeyBlob);

            KnownHostEntry existing;
            try
            {
                existing = store.FindExact(host, port, keyType);
            }
            catch (Exception ex)
            {
                return HostKeyVerdict.Reject("读取 known_hosts 失败：" + ex.Message);
            }

            HostKeySituation situation;
            string previous = null;
            if (existing != null)
            {
                if (string.Equals(existing.BlobText, blobText, StringComparison.Ordinal))
                {
                    return new HostKeyVerdict
                    {
                        Outcome = HostKeyOutcome.Trusted,
                        Message = "命中 known_hosts " + sha256
                    };
                }
                situation = HostKeySituation.Changed;
                previous = FingerprintOfEntry(existing);
            }
            else
            {
                situation = HostKeySituation.Unknown;
            }

            var info = new HostKeyPromptInfo
            {
                Host = host,
                Port = port,
                KeyType = keyType,
                KeyBits = keyBits,
                Sha256Fingerprint = sha256,
                Md5Fingerprint = KnownHostsStore.FingerprintMd5(hostKeyBlob),
                Situation = situation,
                PreviousSha256Fingerprint = previous
            };

            HostKeyDecision decision;
            if (prompt == null)
            {
                // 无 UI（后台采样/工具路径）：不能替你点"信任"
                return HostKeyVerdict.Reject(Describe(situation, host, port, keyType, sha256, previous)
                    + "；当前上下文无法询问用户，已拒绝连接");
            }
            try
            {
                decision = prompt.Ask(info);
            }
            catch (Exception ex)
            {
                return HostKeyVerdict.Reject("主机密钥确认弹窗失败：" + ex.Message);
            }

            switch (decision)
            {
                case HostKeyDecision.AcceptOnce:
                    return new HostKeyVerdict
                    {
                        Outcome = HostKeyOutcome.AcceptedOnce,
                        Message = Describe(situation, host, port, keyType, sha256, previous) + "；用户选择仅本次信任"
                    };
                case HostKeyDecision.AcceptAndSave:
                    try
                    {
                        store.Save(host, port, keyType, blobText);
                    }
                    catch (Exception ex)
                    {
                        // 存不下来仍可继续本次连接，但必须留痕：下次还会再问
                        return new HostKeyVerdict
                        {
                            Outcome = HostKeyOutcome.AcceptedOnce,
                            Message = Describe(situation, host, port, keyType, sha256, previous)
                                + "；用户选择信任但写入 known_hosts 失败：" + ex.Message
                        };
                    }
                    return new HostKeyVerdict
                    {
                        Outcome = HostKeyOutcome.AcceptedAndSaved,
                        Message = Describe(situation, host, port, keyType, sha256, previous) + "；已写入 known_hosts"
                    };
                default:
                    return HostKeyVerdict.Reject(Describe(situation, host, port, keyType, sha256, previous)
                        + "；用户拒绝接受该主机密钥");
            }
        }

        private static string FingerprintOfEntry(KnownHostEntry entry)
        {
            try
            {
                return KnownHostsStore.FingerprintSha256(Convert.FromBase64String(entry.BlobText));
            }
            catch
            {
                return "(记录格式无法解析)";
            }
        }

        private static string Describe(HostKeySituation situation, string host, int port, string keyType,
            string sha256, string previous)
        {
            var target = KnownHostsStore.FormatHostField(host, port);
            if (situation == HostKeySituation.Changed)
            {
                return "主机密钥已变更 " + target + " " + keyType + " 新指纹 " + sha256
                       + "，已知记录指纹 " + (previous ?? "(无法解析)") + "（可能换了机器，也可能是中间人）";
            }
            return "未知主机密钥 " + target + " " + keyType + " 指纹 " + sha256;
        }
    }
}
