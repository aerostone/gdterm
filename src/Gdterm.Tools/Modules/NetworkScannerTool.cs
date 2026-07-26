using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gdterm.Tools.Modules
{
    /// <summary>
    /// 网络扫描套件——IP扫描/DNS/Traceroute/Ping/子网计算/WHOIS
    /// </summary>
    public class NetworkScannerTool : IToolModule
    {
        public string ToolId { get { return "net-scanner"; } }
        public string DisplayName { get { return "网络扫描"; } }
        public string Description { get { return "IP扫描/DNS/Traceroute/Ping监控/子网计算/WHOIS"; } }
        public string Category { get { return "网络"; } }

        public event EventHandler<string> OutputReceived;

        // ── IP扫描：Ping + 端口 + 主机名 ──
        public async Task<List<HostScanResult>> ScanSubnetAsync(string cidr, int port = 0, IProgress<int> progress = null)
        {
            var ips = ExpandCidr(cidr);
            var results = new ConcurrentBag<HostScanResult>();
            var scanned = 0;
            var semaphore = new SemaphoreSlim(30); // 低配默认并发

            var tasks = new List<Task>();
            foreach (var ip in ips)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var result = new HostScanResult { IP = ip };
                        using (var ping = new Ping())
                        {
                            try
                            {
                                var reply = await ping.SendPingAsync(ip, 2000);
                                result.IsAlive = reply.Status == IPStatus.Success;
                                result.LatencyMs = reply.RoundtripTime;
                            }
                            catch { }
                        }

                        if (result.IsAlive)
                        {
                            try { result.HostName = Dns.GetHostEntry(ip).HostName; }
                            catch { }

                            if (port > 0)
                            {
                                try
                                {
                                    using (var tcp = new TcpClient())
                                    {
                                        var task = tcp.ConnectAsync(ip, port);
                                        if (await Task.WhenAny(task, Task.Delay(2000)) == task)
                                        {
                                            await task;
                                            result.PortOpen = true;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        results.Add(result);
                        var done = Interlocked.Increment(ref scanned);
                        progress?.Report(done * 100 / ips.Count);
                    }
                    finally { semaphore.Release(); }
                }));
            }

            await Task.WhenAll(tasks);
            var list = new List<HostScanResult>(results);
            list.Sort((a, b) => CompareIP(a.IP, b.IP));
            OnOutput(string.Format("子网扫描完成: {0}, 发现{1}台主机", cidr, CountAlive(list)));
            return list;
        }

        // ── DNS查询 ──
        public DnsResult QueryDns(string hostname)
        {
            var result = new DnsResult { HostName = hostname };
            try
            {
                var entries = Dns.GetHostEntry(hostname);
                result.IPAddresses = new List<string>();
                foreach (var addr in entries.AddressList)
                {
                    result.IPAddresses.Add(addr.ToString());
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                        result.IPv4 = addr.ToString();
                    else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                        result.IPv6 = addr.ToString();
                }
                result.IsSuccess = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            OnOutput(string.Format("DNS: {0} → {1}", hostname, result.IPv4 ?? "无结果"));
            return result;
        }

        // ── Traceroute ──
        public async Task<List<TracerouteHop>> TracerouteAsync(string host, int maxHops = 30, CancellationToken ct = default)
        {
            var hops = new List<TracerouteHop>();
            var ip = Dns.GetHostEntry(host).AddressList[0];

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                var hop = new TracerouteHop { Hop = ttl };
                try
                {
                    using (var ping = new Ping())
                    {
                        var opts = new PingOptions(ttl, true);
                        var buffer = new byte[32];
                        var reply = await ping.SendPingAsync(ip, 5000, buffer, opts);

                        hop.Address = reply.Address?.ToString() ?? "*";
                        hop.LatencyMs = reply.RoundtripTime;
                        hop.Status = reply.Status;

                        if (reply.Status == IPStatus.Success)
                        {
                            hops.Add(hop);
                            OnOutput(string.Format("traceroute #{0}: {1} ({2}ms) ✓", ttl, hop.Address, hop.LatencyMs));
                            break; // 到达目标
                        }
                        else if (reply.Status == IPStatus.TtlExpired)
                        {
                            try { hop.HostName = Dns.GetHostEntry(reply.Address).HostName; }
                            catch { }
                        }
                    }
                }
                catch
                {
                    hop.Address = "*";
                    hop.Status = IPStatus.TimedOut;
                }

                hops.Add(hop);
                OnOutput(string.Format("traceroute #{0}: {1} ({2}ms)", ttl, hop.Address, hop.LatencyMs));

                if (ct.IsCancellationRequested) break;
            }

            return hops;
        }

        // ── Ping实时监控 ──
        public async Task PingMonitorAsync(string host, int count, int intervalMs, Action<PingMonitorResult> onPing, CancellationToken ct = default)
        {
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                var result = new PingMonitorResult { Sequence = i + 1 };
                try
                {
                    using (var ping = new Ping())
                    {
                        var reply = await ping.SendPingAsync(host, 5000);
                        result.Status = reply.Status;
                        result.LatencyMs = reply.RoundtripTime;
                        result.Address = reply.Address?.ToString();
                    }
                }
                catch (Exception ex)
                {
                    result.Status = IPStatus.Unknown;
                    result.Error = ex.Message;
                }

                result.Timestamp = DateTime.Now;
                onPing?.Invoke(result);

                if (i < count - 1 && !ct.IsCancellationRequested)
                    await Task.Delay(intervalMs, ct);
            }
        }

        // ── 子网CIDR计算 ──
        public SubnetInfo CalculateSubnet(string cidr)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2) return null;

            var ipBytes = IPAddress.Parse(parts[0]).GetAddressBytes();
            int prefixLen = int.Parse(parts[1]);
            if (prefixLen < 0 || prefixLen > 32) return null;

            uint mask = prefixLen == 0 ? 0 : ~((1u << (32 - prefixLen)) - 1);
            uint ip = (uint)((ipBytes[0] << 24) | (ipBytes[1] << 16) | (ipBytes[2] << 8) | ipBytes[3]);
            uint network = ip & mask;
            uint broadcast = network | ~mask;
            uint firstHost = network + 1;
            uint lastHost = broadcast - 1;
            uint hostCount = prefixLen >= 31 ? 0 : lastHost - firstHost + 1;

            return new SubnetInfo
            {
                CIDR = cidr,
                NetworkAddress = FormatIP(network),
                BroadcastAddress = FormatIP(broadcast),
                SubnetMask = FormatIP(mask),
                FirstHost = FormatIP(firstHost),
                LastHost = FormatIP(lastHost),
                HostCount = hostCount,
                PrefixLength = prefixLen
            };
        }

        // ── WHOIS查询 ──
        public async Task<string> WhoisQueryAsync(string domain)
        {
            var tld = domain.Contains(".") ? domain.Substring(domain.LastIndexOf('.') + 1) : "com";
            string server;
            switch (tld.ToLower())
            {
                case "com": server = "whois.verisign-grs.com"; break;
                case "net": server = "whois.verisign-grs.com"; break;
                case "org": server = "whois.pir.org"; break;
                case "cn": server = "whois.cnnic.cn"; break;
                default: server = "whois.iana.org"; break;
            }

            try
            {
                using (var tcp = new TcpClient())
                {
                    await tcp.ConnectAsync(server, 43);
                    using (var stream = tcp.GetStream())
                    {
                        var cmd = Encoding.ASCII.GetBytes(domain + "\r\n");
                        await stream.WriteAsync(cmd, 0, cmd.Length);
                        var buffer = new byte[8192];
                        var sb = new StringBuilder();
                        int read;
                        do
                        {
                            read = await stream.ReadAsync(buffer, 0, buffer.Length);
                            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        } while (read > 0);
                        return sb.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                return "WHOIS查询失败: " + ex.Message;
            }
        }

        // ── 辅助 ──
        private List<string> ExpandCidr(string cidr)
        {
            var result = new List<string>();
            var parts = cidr.Split('/');
            if (parts.Length != 2) { result.Add(parts[0]); return result; }

            var ipBytes = IPAddress.Parse(parts[0]).GetAddressBytes();
            int prefixLen = int.Parse(parts[1]);
            if (prefixLen >= 32) { result.Add(parts[0]); return result; }

            uint ip = (uint)((ipBytes[0] << 24) | (ipBytes[1] << 16) | (ipBytes[2] << 8) | ipBytes[3]);
            uint mask = prefixLen == 0 ? 0 : ~((1u << (32 - prefixLen)) - 1);
            uint network = ip & mask;
            uint broadcast = network | ~mask;

            // 限制扫描范围
            uint count = Math.Min(broadcast - network - 1, 1024);
            for (uint i = 1; i <= count; i++)
            {
                result.Add(FormatIP(network + i));
            }
            return result;
        }

        private string FormatIP(uint ip)
        {
            return string.Format("{0}.{1}.{2}.{3}", (ip >> 24) & 0xFF, (ip >> 16) & 0xFF, (ip >> 8) & 0xFF, ip & 0xFF);
        }

        private int CompareIP(string a, string b)
        {
            var aParts = a.Split('.'); var bParts = b.Split('.');
            for (int i = 0; i < 4; i++)
            {
                int ai, bi;
                int.TryParse(aParts[i], out ai);
                int.TryParse(bParts[i], out bi);
                if (ai != bi) return ai.CompareTo(bi);
            }
            return 0;
        }

        private int CountAlive(List<HostScanResult> list)
        {
            int count = 0;
            foreach (var h in list) if (h.IsAlive) count++;
            return count;
        }

        private void OnOutput(string msg) { OutputReceived?.Invoke(this, msg); }

        public System.Windows.Forms.Control CreatePanel()
        {
            return ToolPanelHelper.CreateActionPanel(
                DisplayName,
                "子网扫描 例: 192.168.1.0/24  | DNS 例: dns example.com  | 路由跟踪 例: tr 8.8.8.8",
                null,
                (inputs, output, status) =>
                {
                    var text = (inputs[0].Text ?? "").Trim();
                    if (string.IsNullOrEmpty(text) || text.StartsWith("目标"))
                    {
                        status.Text = "请输入参数";
                        return;
                    }
                    var parts = text.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts[0].Equals("dns", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
                    {
                        var dns = QueryDns(parts[1]);
                        ToolPanelHelper.AppendLine(output,
                            (dns.IsSuccess ? "OK" : "FAIL") + " " + dns.HostName +
                            " v4=" + (dns.IPv4 ?? "-") + " v6=" + (dns.IPv6 ?? "-") +
                            (string.IsNullOrEmpty(dns.Error) ? "" : " err=" + dns.Error));
                        status.Text = "DNS 完成";
                        return;
                    }
                    if ((parts[0].Equals("tr", StringComparison.OrdinalIgnoreCase) ||
                         parts[0].Equals("traceroute", StringComparison.OrdinalIgnoreCase)) && parts.Length > 1)
                    {
                        var hops = TracerouteAsync(parts[1]).GetAwaiter().GetResult();
                        foreach (var h in hops)
                            ToolPanelHelper.AppendLine(output,
                                h.Hop + " " + (h.Address ?? "*") + " " + (h.HostName ?? "") + " " + h.LatencyMs + "ms");
                        status.Text = "traceroute 完成";
                        return;
                    }
                    var cidr = parts[0];
                    var hosts = ScanSubnetAsync(cidr).GetAwaiter().GetResult();
                    foreach (var h in hosts)
                        if (h.IsAlive)
                            ToolPanelHelper.AppendLine(output, h.IP + " " + (h.HostName ?? "") + " " + h.LatencyMs + "ms");
                    status.Text = "存活 " + CountAlive(hosts) + " / " + hosts.Count;
                });
        }

        public void LoadConfig() { }

        public void SaveConfig() { }

        public void Dispose() { }
    }

    // ── 数据模型 ──
    public class HostScanResult
    {
        public string IP { get; set; }
        public string HostName { get; set; }
        public bool IsAlive { get; set; }
        public long LatencyMs { get; set; }
        public bool PortOpen { get; set; }
    }

    public class DnsResult
    {
        public string HostName { get; set; }
        public string IPv4 { get; set; }
        public string IPv6 { get; set; }
        public List<string> IPAddresses { get; set; }
        public bool IsSuccess { get; set; }
        public string Error { get; set; }
    }

    public class TracerouteHop
    {
        public int Hop { get; set; }
        public string Address { get; set; }
        public string HostName { get; set; }
        public long LatencyMs { get; set; }
        public IPStatus Status { get; set; }
    }

    public class PingMonitorResult
    {
        public int Sequence { get; set; }
        public DateTime Timestamp { get; set; }
        public long LatencyMs { get; set; }
        public IPStatus Status { get; set; }
        public string Address { get; set; }
        public string Error { get; set; }
        public bool IsSuccess { get { return Status == IPStatus.Success; } }
    }

    public class SubnetInfo
    {
        public string CIDR { get; set; }
        public string NetworkAddress { get; set; }
        public string BroadcastAddress { get; set; }
        public string SubnetMask { get; set; }
        public string FirstHost { get; set; }
        public string LastHost { get; set; }
        public uint HostCount { get; set; }
        public int PrefixLength { get; set; }
    }
}
