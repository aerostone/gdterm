using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gdterm.Tools.Modules
{
    /// <summary>
    /// 端口扫描结果
    /// </summary>
    public class PortScanResult
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public bool IsOpen { get; set; }
        public string Service { get; set; }
        public TimeSpan Latency { get; set; }
    }

    /// <summary>
    /// 异步TCP端口扫描器
    /// </summary>
    public class PortScannerTool : IToolModule
    {
        private PortScannerConfig _config;
        private CancellationTokenSource _cts;

        public string ToolId { get { return "port-scanner"; } }
        public string DisplayName { get { return "端口扫描"; } }
        public string Description { get { return "异步TCP端口扫描"; } }
        public string Category { get { return "网络"; } }

        public event EventHandler<PortScanResult> PortScanned;
        public event EventHandler<string> ScanCompleted;

        public PortScannerTool()
        {
            _config = new PortScannerConfig();
        }

        public void LoadConfig() { _config.LoadFromFile(); }
        public void SaveConfig() { _config.SaveToFile(); }

        /// <summary>扫描指定主机的端口范围</summary>
        public async Task<List<PortScanResult>> ScanAsync(string host, int startPort, int endPort, IProgress<int> progress = null)
        {
            _cts = new CancellationTokenSource();
            var results = new ConcurrentBag<PortScanResult>();
            var total = endPort - startPort + 1;
            var scanned = 0;

            var tasks = new List<Task>();
            var semaphore = new SemaphoreSlim(_config.MaxConcurrency);

            for (int port = startPort; port <= endPort; port++)
            {
                var p = port;
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(_cts.Token);
                    try
                    {
                        var result = await ScanPortAsync(host, p);
                        results.Add(result);
                        PortScanned?.Invoke(this, result);
                        var done = Interlocked.Increment(ref scanned);
                        progress?.Report(done * 100 / total);
                    }
                    catch { }
                    finally { semaphore.Release(); }
                }, _cts.Token));
            }

            await Task.WhenAll(tasks);
            var list = new List<PortScanResult>(results);
            list.Sort((a, b) => a.Port.CompareTo(b.Port));
            ScanCompleted?.Invoke(this, string.Format("扫描完成: {0}:{1}-{2}, 发现{3}个开放端口", host, startPort, endPort, FindOpenCount(list)));
            return list;
        }

        /// <summary>扫描常用端口预设</summary>
        public Task<List<PortScanResult>> ScanCommonPortsAsync(string host, IProgress<int> progress = null)
        {
            return ScanAsync(host, 1, 1024, progress);
        }

        /// <summary>取消扫描</summary>
        public void CancelScan()
        {
            _cts?.Cancel();
        }

        private async Task<PortScanResult> ScanPortAsync(string host, int port)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(task, Task.Delay(_config.TimeoutMs)) == task)
                    {
                        await task;
                        sw.Stop();
                        return new PortScanResult { Host = host, Port = port, IsOpen = true, Service = GetServiceName(port), Latency = sw.Elapsed };
                    }
                }
            }
            catch { }
            sw.Stop();
            return new PortScanResult { Host = host, Port = port, IsOpen = false, Latency = sw.Elapsed };
        }

        private int FindOpenCount(List<PortScanResult> results)
        {
            int count = 0;
            foreach (var r in results) if (r.IsOpen) count++;
            return count;
        }

        private string GetServiceName(int port)
        {
            switch (port)
            {
                case 21: return "FTP";
                case 22: return "SSH";
                case 23: return "Telnet";
                case 25: return "SMTP";
                case 53: return "DNS";
                case 80: return "HTTP";
                case 110: return "POP3";
                case 143: return "IMAP";
                case 443: return "HTTPS";
                case 993: return "IMAPS";
                case 995: return "POP3S";
                case 3306: return "MySQL";
                case 3389: return "RDP";
                case 5432: return "PostgreSQL";
                case 6379: return "Redis";
                case 8080: return "HTTP-Alt";
                case 8443: return "HTTPS-Alt";
                case 27017: return "MongoDB";
                default: return "";
            }
        }

        public System.Windows.Forms.Control CreatePanel()
        {
            return ToolPanelHelper.CreateActionPanel(
                DisplayName,
                Description + "  格式: host startPort endPort  例: 192.168.1.1 1 1024",
                null,
                (inputs, output, status) =>
                {
                    var parts = (inputs[0].Text ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 1)
                    {
                        status.Text = "请输入主机";
                        return;
                    }
                    var host = parts[0];
                    int start = 1, end = 1024;
                    if (parts.Length >= 3)
                    {
                        int.TryParse(parts[1], out start);
                        int.TryParse(parts[2], out end);
                    }

                    // finding-05：扫描在后台线程执行，禁止同步阻塞 UI（此前 GetAwaiter().GetResult()
                    // 会冻住主窗体直到上千端口全部探完）。AppendLine 自带封送，可安全跨线程调用；
                    // status/按钮状态经 output 所在面板 BeginInvoke 封送回 UI 线程。
                    var root = output.Parent as System.Windows.Forms.Control;
                    Action ui = () =>
                    {
                        status.Text = "扫描中...";
                        status.ForeColor = System.Drawing.Color.FromArgb(255, 200, 80);
                    };
                    if (root != null && root.IsHandleCreated) root.BeginInvoke(ui); else ui();

                    Task.Run(async () =>
                    {
                        try
                        {
                            var results = parts.Length >= 3
                                ? await ScanAsync(host, start, end)
                                : await ScanCommonPortsAsync(host);
                            foreach (var r in results)
                                if (r.IsOpen) ToolPanelHelper.AppendLine(output, r.Host + ":" + r.Port + " open " + r.Service);
                            ToolPanelHelper.AppendLine(output, string.Format("完成：开放 {0} / 扫描 {1} 个端口", FindOpenCount(results), results.Count));
                            Action doneUi = () =>
                            {
                                status.Text = "完成，开放 " + FindOpenCount(results) + " 个端口";
                                status.ForeColor = System.Drawing.Color.FromArgb(120, 200, 120);
                            };
                            if (root != null && root.IsHandleCreated) root.BeginInvoke(doneUi); else doneUi();
                        }
                        catch (Exception ex)
                        {
                            ToolPanelHelper.AppendLine(output, "扫描失败: " + ex.Message);
                            Action failUi = () =>
                            {
                                status.Text = "失败: " + ex.Message;
                                status.ForeColor = System.Drawing.Color.FromArgb(255, 100, 100);
                            };
                            if (root != null && root.IsHandleCreated) root.BeginInvoke(failUi); else failUi();
                        }
                    });
                });
        }

        public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); }
    }

    /// <summary>端口扫描配置</summary>
    public class PortScannerConfig : ToolConfigBase
    {
        public int MaxConcurrency { get; set; }
        public int TimeoutMs { get; set; }
        public int[] CommonPorts { get; set; }

        public override void ResetDefaults()
        {
            // 低配默认并发收紧
            MaxConcurrency = 40;
            TimeoutMs = 3000;
            CommonPorts = new[] { 22, 80, 443, 3306, 3389, 5432, 6379, 8080 };
        }

        protected override void LoadFromJson(string json)
        {
            MaxConcurrency = ExtractInt(json, "maxConcurrency", 100);
            TimeoutMs = ExtractInt(json, "timeoutMs", 3000);
            var ports = ExtractStringList(json, "commonPorts");
            if (ports.Count > 0)
            {
                var list = new List<int>();
                foreach (var p in ports) { int v; if (int.TryParse(p, out v)) list.Add(v); }
                CommonPorts = list.ToArray();
            }
            else ResetDefaults();
        }

        protected override string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"maxConcurrency\":").Append(MaxConcurrency);
            sb.Append(",\"timeoutMs\":").Append(TimeoutMs);
            sb.Append(",\"commonPorts\":[");
            for (int i = 0; i < CommonPorts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(CommonPorts[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
