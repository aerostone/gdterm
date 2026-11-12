using System;
using System.Collections.Generic;
using Gdterm.Core.Models;
using Renci.SshNet;
using Gdterm.Tunnel;

namespace Gdterm.Terminal.Monitoring
{
    /// <summary>一次远端采样结果（FinalShell 式主机资源面板的一帧）。</summary>
    public sealed class RemoteStatsSnapshot
    {
        /// <summary>CPU 使用率（0-100，两次采样差分；首次为 0）。</summary>
        public double CpuPercent { get; set; }

        /// <summary>内存使用率（0-100）。</summary>
        public double MemPercent { get; set; }

        /// <summary>Swap 使用率（0-100，无 swap 时为 0）。</summary>
        public double SwapPercent { get; set; }

        /// <summary>根分区使用率（0-100）。</summary>
        public double DiskPercent { get; set; }

        /// <summary>接收速率 KiB/s（两次采样差分；首次为 0）。</summary>
        public double NetRxKbps { get; set; }

        /// <summary>发送速率 KiB/s（两次采样差分；首次为 0）。</summary>
        public double NetTxKbps { get; set; }

        /// <summary>1 分钟负载。</summary>
        public double Load1 { get; set; }

        public long MemTotalKb { get; set; }
        public long DiskTotalKb { get; set; }
        public DateTime SampledAt { get; set; }

        /// <summary>采样失败原因（成功为 null）。</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 远端主机资源采样：自己开一条独立 SSH 连接跑只读采集（不干扰终端 shell，也不吃交互通道），
    /// 一次 exec 拿完 /proc/stat + meminfo + loadavg + net/dev + df，5 秒一轮很便宜。
    /// 代理/跳板无需在这里重复：跳板模式下采样也应该直连（采样失败就显示失败）。
    /// </summary>
    public sealed class RemoteStatsSampler : IDisposable
    {
        private const string Separator = "===GDTERM===";

        private readonly ConnectionConfig _config;
        private readonly CredentialPayload _credential;
        private readonly object _sync = new object();
        private SshClient _client;

        private ulong _prevCpuTotal;
        private ulong _prevCpuIdle;
        private long _prevRx;
        private long _prevTx;
        private DateTime _prevAt;

        public RemoteStatsSampler(ConnectionConfig config, CredentialPayload credential)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            _config = config;
            _credential = credential;
        }

        /// <summary>采样一次。失败不抛：返回带 Error 的快照，UI 照常画（灰掉）。</summary>
        public RemoteStatsSnapshot Sample(int timeoutSeconds = 8)
        {
            lock (_sync)
            {
                var snap = new RemoteStatsSnapshot { SampledAt = DateTime.Now };
                try
                {
                    EnsureConnected(timeoutSeconds);
                    string text = RunCollect(timeoutSeconds);
                    Fill(snap, text);
                }
                catch (Exception ex)
                {
                    snap.Error = ex.Message;
                    // 连接坏了就丢掉，下次重连（远端重启/网络抖动都能自愈）
                    try { if (_client != null) { _client.Dispose(); } } catch (Exception) { }
                    _client = null;
                }
                return snap;
            }
        }

        private void EnsureConnected(int timeoutSeconds)
        {
            if (_client != null && _client.IsConnected) return;
            try { if (_client != null) _client.Dispose(); } catch (Exception) { }
            _client = null;

            var info = Gdterm.Tunnel.SshConnectionInfoFactory.Create(
                _config.Host,
                _config.Port,
                _credential.Username ?? _config.Username,
                _credential,
                Math.Max(5, timeoutSeconds),
                _config.Proxy);
            // 后台采样同样要过校验：指纹不符时应该报错，而不是把陌生主机的 CPU 数字画到面板上
            var client = SshHostKeyGuard.CreateClient(info, _config.Host, _config.Port);
            try { client.Connect(); }
            catch (Exception ex) { throw SshHostKeyGuard.Describe(client, ex); }
            _client = client;
            // 新连接：上一轮的差分基线作废
            _prevCpuTotal = 0; _prevCpuIdle = 0; _prevRx = 0; _prevTx = 0; _prevAt = default(DateTime);
        }

        private string RunCollect(int timeoutSeconds)
        {
            const string cmd =
                "LC_ALL=C; cat /proc/stat | head -1; echo " + Separator + "; " +
                "cat /proc/meminfo; echo " + Separator + "; " +
                "cat /proc/loadavg; echo " + Separator + "; " +
                "cat /proc/net/dev; echo " + Separator + "; " +
                "df -kP / 2>/dev/null";
            using (var command = _client.CreateCommand(cmd))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds));
                return command.Execute() ?? string.Empty;
            }
        }

        private void Fill(RemoteStatsSnapshot snap, string text)
        {
            var blocks = (text ?? string.Empty).Replace("\r", "").Split(new string[] { Separator }, StringSplitOptions.None);
            string statBlock = Block(blocks, 0);
            string memBlock = Block(blocks, 1);
            string loadBlock = Block(blocks, 2);
            string netBlock = Block(blocks, 3);
            string dfBlock = Block(blocks, 4);

            ulong total, idle;
            if (RemoteStatsParser.ParseCpuFirstLine(FirstLine(statBlock), out total, out idle))
            {
                if (_prevCpuTotal > 0 && total > _prevCpuTotal)
                {
                    ulong dTotal = total - _prevCpuTotal;
                    ulong dIdle = idle >= _prevCpuIdle ? idle - _prevCpuIdle : 0;
                    if (dTotal > 0)
                        snap.CpuPercent = Clamp(100.0 * (dTotal - dIdle) / dTotal);
                }
                _prevCpuTotal = total;
                _prevCpuIdle = idle;
            }

            long memTotal, memAvail;
            if (RemoteStatsParser.ParseMemInfo(memBlock, out memTotal, out memAvail))
            {
                snap.MemTotalKb = memTotal;
                if (memTotal > 0) snap.MemPercent = Clamp(100.0 * (memTotal - Math.Max(0, memAvail)) / memTotal);
            }
            long swapTotal, swapFree;
            if (RemoteStatsParser.ParseSwapInfo(memBlock, out swapTotal, out swapFree) && swapTotal > 0)
                snap.SwapPercent = Clamp(100.0 * (swapTotal - Math.Max(0, swapFree)) / swapTotal);

            snap.Load1 = RemoteStatsParser.ParseLoad1(loadBlock);

            long rx, tx;
            if (RemoteStatsParser.ParseNetDev(netBlock, out rx, out tx))
            {
                var now = DateTime.Now;
                if (_prevAt != default(DateTime) && (rx >= _prevRx || tx >= _prevTx))
                {
                    double secs = Math.Max(0.5, (now - _prevAt).TotalSeconds);
                    snap.NetRxKbps = Math.Max(0, (rx - _prevRx) / 1024.0 / secs);
                    snap.NetTxKbps = Math.Max(0, (tx - _prevTx) / 1024.0 / secs);
                }
                _prevRx = rx;
                _prevTx = tx;
                _prevAt = now;
            }

            long diskTotal, diskUsed;
            if (RemoteStatsParser.ParseDf(dfBlock, out diskTotal, out diskUsed))
            {
                snap.DiskTotalKb = diskTotal;
                if (diskTotal > 0) snap.DiskPercent = Clamp(100.0 * diskUsed / diskTotal);
            }
        }

        private static string Block(string[] blocks, int index)
        {
            return blocks != null && index < blocks.Length ? blocks[index] : string.Empty;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int nl = text.IndexOf('\n');
            return nl < 0 ? text : text.Substring(0, nl);
        }

        private static double Clamp(double v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try { if (_client != null) _client.Dispose(); } catch (Exception) { }
                _client = null;
            }
        }
    }
}
