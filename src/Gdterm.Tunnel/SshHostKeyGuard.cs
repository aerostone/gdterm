using System;
using Gdterm.Core.Security;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// 主机密钥校验的唯一入口（2026-11 功能缺口审计 P0）。
    ///
    /// 背景：SSH.NET 在**没有**注册 HostKeyReceived 处理器时完全不校验服务端主机密钥，
    /// 中间人可以透明接管链路而界面上毫无提示。审计前全仓 `HostKeyReceived` 命中数为 0，
    /// 5 处建连点全是裸 `new SshClient(...)`。
    ///
    /// 现在所有建连只允许走 <see cref="CreateClient"/> / <see cref="CreateSftpClient"/>：
    /// 工厂负责挂校验、按 known_hosts 判定、必要时问用户。`tools/check-sec-gate.py`
    /// 有一条门禁盯着「新建 SshClient/SftpClient 必须经工厂」，防止新代码再绕过。
    ///
    /// 组合根（Gdterm.UI/Program.cs）负责 <see cref="Configure"/>；未配置时（单测、无 UI 工具）
    /// 判定为 Disabled 并放行——这是刻意的，因为校验需要 known_hosts 路径与 UI 提问能力，
    /// 硬失败会让所有非 UI 用例无法跑。应用本体一定先 Configure 再建连。
    /// </summary>
    public static class SshHostKeyGuard
    {
        private static KnownHostsStore _store;
        private static IHostKeyPrompt _prompt;

        /// <summary>
        /// 每个客户端自己的拒绝理由。原来用 [ThreadStatic] 存"最近一条"，
        /// 实测（本地 2222 端口探针）**拿不到**：HostKeyReceived 在 SSH.NET 的会话线程上触发，
        /// 调用方 catch 到的是另一条线程，ThreadStatic 天然丢失。改用弱表按客户端实例挂。
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BaseClient, string> Rejections =
            new System.Runtime.CompilerServices.ConditionalWeakTable<BaseClient, string>();

        private static volatile string _lastRejectionGlobal;

        /// <summary>kept host key store（未配置为 null）。</summary>
        public static KnownHostsStore Store { get { return _store; } }

        /// <summary>最近一次被拒的理由（进程级，仅用于兜底日志）。</summary>
        public static string LastRejection { get { return _lastRejectionGlobal; } }

        /// <summary>某个客户端被拒的理由（没有则为 null）。</summary>
        public static string LastRejectionFor(BaseClient client)
        {
            if (client == null) return null;
            string reason;
            return Rejections.TryGetValue(client, out reason) ? reason : null;
        }

        /// <summary>配置校验所需的 known_hosts 与提问实现（启动时调用一次）。</summary>
        public static void Configure(KnownHostsStore store, IHostKeyPrompt prompt)
        {
            _store = store;
            _prompt = prompt;
        }

        /// <summary>
        /// 日志出口（level:source, message）。Tunnel 不引用 Terminal/Logging（架构 F2 约束），
        /// 由组合根把 TerminalLog/DiagLog 接进来；未接则静默。
        /// </summary>
        public static Action<string, string> LogSink { get; set; }

        /// <summary>
        /// 建一个带主机密钥校验的 SshClient。
        /// </summary>
        /// <param name="info">已构建的连接信息</param>
        /// <param name="hostKeyHost">用来钉住公钥的身份主机名——跳板/本地转发场景要传**目标机**，
        /// 不能传 127.0.0.1，否则钉住的是转发端点（同一台堡垒机的所有目标会共用一个指纹）。</param>
        /// <param name="hostKeyPort">同上，目标机端口</param>
        public static SshClient CreateClient(ConnectionInfo info, string hostKeyHost, int hostKeyPort)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            var client = new SshClient(info);
            Attach(client, hostKeyHost, hostKeyPort);
            return client;
        }

        /// <summary>建一个带主机密钥校验的 SftpClient（SFTP 与终端必须钉同一份指纹）。</summary>
        public static SftpClient CreateSftpClient(ConnectionInfo info, string hostKeyHost, int hostKeyPort)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            var client = new SftpClient(info);
            Attach(client, hostKeyHost, hostKeyPort);
            return client;
        }

        /// <summary>挂校验处理器（少数必须复用外部 client 的场合使用）。</summary>
        public static void Attach(BaseClient client, string hostKeyHost, int hostKeyPort)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            client.HostKeyReceived += (sender, e) =>
            {
                HostKeyVerdict verdict;
                try
                {
                    verdict = HostKeyVerifier.Evaluate(
                        _store,
                        hostKeyHost,
                        hostKeyPort,
                        e.HostKeyName,
                        e.HostKey,
                        e.KeyLength,
                        _prompt);
                }
                catch (Exception ex)
                {
                    // 判定炸了不能当放行——任何异常都必须落到 CanTrust=false
                    verdict = HostKeyVerdict.Reject("主机密钥校验异常：" + ex.Message);
                }

                e.CanTrust = verdict.CanTrust;
                if (!verdict.CanTrust)
                {
                    // net462 的 ConditionalWeakTable 没有 AddOrUpdate，只能 Remove+Add
                    Rejections.Remove(client);
                    Rejections.Add(client, verdict.Message);
                    _lastRejectionGlobal = verdict.Message;
                }

                Log("security:SshHostKey", KnownHostsStore.FormatHostField(hostKeyHost, hostKeyPort)
                    + " " + e.HostKeyName + " " + verdict.Outcome + ": " + verdict.Message);
            };
        }

        private static void Log(string source, string message)
        {
            var sink = LogSink;
            if (sink == null) return;
            try { sink(source, message); } catch { }
        }

        /// <summary>
        /// 把连接异常补充成可读信息：主机密钥被拒时 SSH.NET 只会抛
        /// "Host key could not be verified."（本地实测原文），用户根本不知道是被我们自己拦下的、
        /// 更不知道该用哪个指纹去核对。优先取该客户端自己的理由。
        /// </summary>
        public static Exception Describe(BaseClient client, Exception ex)
        {
            return Describe(ex, LastRejectionFor(client));
        }

        /// <summary>无客户端上下文时的兜底（取进程内最近一条拒绝理由）。</summary>
        public static Exception Describe(Exception ex)
        {
            return Describe(ex, _lastRejectionGlobal);
        }

        private static Exception Describe(Exception ex, string reason)
        {
            if (ex == null) return null;
            if (string.IsNullOrEmpty(reason)) return ex;
            var message = ex.Message + "（主机密钥校验未通过：" + reason + "）";
            if (ex is SshConnectionException)
                return new SshConnectionException(message, ex);
            return new SshException(message, ex);
        }
    }
}
