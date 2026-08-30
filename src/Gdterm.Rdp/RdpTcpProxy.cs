using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP TCP 抓包代理——监听本地端口，转发到真实目标，双向 hex-dump。
    ///
    /// 用法（手动）：
    ///   1. Start(localPort, targetHost, targetPort)
    ///   2. 把 RDP 客户端连接到 127.0.0.1:{localPort}
    ///   3. 双向转发，hex dump 写到 logs/rdp-dump/
    ///   4. 停止代理（Stop/Dispose）
    ///
    /// 用法（自动，连接级抓包）：
    ///   RdpDumpProxy.StartFor(targetHost, targetPort) 返回本地端口
    ///   RdpDumpProxy.Stop() 停止
    ///
    /// 线程安全：Start/Stop/Dispose 由调用方串行化，内部 Accept 循环可取消。
    /// </summary>
    public sealed class RdpTcpProxy : IDisposable
    {
        private readonly string _dumpDir;
        private TcpListener _listener;
        private volatile bool _running;
        private readonly List<Task> _activeForwards = new List<Task>();
        private readonly object _lock = new object();

        public int LocalPort { get; private set; }
        public string TargetHost { get; private set; }
        public int TargetPort { get; private set; }
        public bool IsRunning => _running;

        /// <param name="dumpDir">hex dump 输出目录（绝对路径），如 logs/rdp-dump</param>
        public RdpTcpProxy(string dumpDir)
        {
            _dumpDir = dumpDir ?? throw new ArgumentNullException("dumpDir");
            if (!Directory.Exists(_dumpDir))
                Directory.CreateDirectory(_dumpDir);
        }

        /// <summary>
        /// 启动代理。
        /// </summary>
        /// <param name="localPort">本地监听端口，默认 3390</param>
        /// <param name="targetHost">真实目标 IP/主机名</param>
        /// <param name="targetPort">真实目标端口</param>
        public void Start(int localPort, string targetHost, int targetPort)
        {
            if (_running)
                throw new InvalidOperationException("代理已在运行中");
            if (string.IsNullOrEmpty(targetHost))
                throw new ArgumentNullException("targetHost");

            LocalPort = localPort;
            TargetHost = targetHost;
            TargetPort = targetPort;

            _listener = new TcpListener(IPAddress.Loopback, localPort);
            _listener.Start();
            _running = true;

            RdpLog.Info("RdpTcpProxy.Start",
                string.Format("监听 127.0.0.1:{0} → {1}:{2}，dump 目录 {3}",
                    localPort, targetHost, targetPort, _dumpDir));

            // 异步 Accept 循环
            Task.Run(() => AcceptLoop());
        }

        /// <summary>
        /// 停止代理，关闭所有活跃转发。
        /// </summary>
        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }

            Task[] active;
            lock (_lock)
            {
                active = _activeForwards.ToArray();
                _activeForwards.Clear();
            }

            foreach (var t in active)
            {
                try { t.Dispose(); } catch { }
            }

            RdpLog.Info("RdpTcpProxy.Stop", "代理已停止");
        }

        public void Dispose()
        {
            Stop();
            try { _listener?.Server?.Dispose(); } catch { }
        }

        private async void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!_running) break;
                    RdpLog.Info("RdpTcpProxy.Accept", "Accept 异常: " + ex.Message);
                    continue;
                }

                if (!_running) { try { client?.Dispose(); } catch { } break; }

                var local = ((IPEndPoint)client.Client.LocalEndPoint).Port;
                var remote = ((IPEndPoint)client.Client.RemoteEndPoint).ToString();
                RdpLog.Info("RdpTcpProxy.Accept",
                    string.Format("新连接: {0} → :{1}", remote, local));

                var forwardTask = ForwardAsync(client, local);
                lock (_lock)
                {
                    _activeForwards.Add(forwardTask);
                    _activeForwards.RemoveAll(t => t.IsCompleted);
                }
            }
        }

        private async Task ForwardAsync(TcpClient client, int localPort)
        {
            TcpClient target = null;
            try
            {
                target = new TcpClient();
                await target.ConnectAsync(TargetHost, TargetPort).ConfigureAwait(false);

                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var dumpPath = Path.Combine(_dumpDir,
                    string.Format("rdp-dump-{0}-port{1}.hex", timestamp, localPort));

                RdpLog.Info("RdpTcpProxy.Forward",
                    string.Format("开始转发，dump → {0}", dumpPath));

                using (var dumpStream = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var dumpWriter = new StreamWriter(dumpStream))
                {
                    dumpWriter.NewLine = "\n";
                    dumpWriter.WriteLine(string.Format("# RDP TCP Proxy dump"));
                    dumpWriter.WriteLine(string.Format("# Time: {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now));
                    dumpWriter.WriteLine(string.Format("# Client: {0}", ((IPEndPoint)client.Client.RemoteEndPoint).ToString()));
                    dumpWriter.WriteLine(string.Format("# Target: {0}:{1}", TargetHost, TargetPort));
                    dumpWriter.WriteLine("#");
                    dumpWriter.Flush();

                    var clientStream = client.GetStream();
                    var targetStream = target.GetStream();

                    var dir = Path.Combine(_dumpDir, "raw");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var rawClientPath = Path.Combine(dir, string.Format("rdp-dump-{0}-port{1}-c2s.bin", timestamp, localPort));
                    var rawTargetPath = Path.Combine(dir, string.Format("rdp-dump-{0}-port{1}-s2c.bin", timestamp, localPort));

                    using (var rawClient = new FileStream(rawClientPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var rawTarget = new FileStream(rawTargetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        var c2s = ForwardOneWay(clientStream, targetStream, dumpWriter, rawClient, "C2S");
                        var s2c = ForwardOneWay(targetStream, clientStream, dumpWriter, rawTarget, "S2C");
                        await Task.WhenAny(c2s, s2c).ConfigureAwait(false);
                    }
                }

                RdpLog.Info("RdpTcpProxy.Forward", "转发结束");
            }
            catch (Exception ex)
            {
                RdpLog.Info("RdpTcpProxy.Forward", "转发异常: " + ex.Message);
            }
            finally
            {
                try { client?.Dispose(); } catch { }
                try { target?.Dispose(); } catch { }
            }
        }

        private static async Task ForwardOneWay(
            NetworkStream source, NetworkStream dest,
            StreamWriter dumpWriter, FileStream rawStream,
            string label)
        {
            var buf = new byte[8192];
            var hexBuf = new char[16 * 3 + 1];
            var offset = 0L;
            try
            {
                while (true)
                {
                    var n = await source.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (n <= 0) break;

                    // 写入 raw 二进制
                    await rawStream.WriteAsync(buf, 0, n).ConfigureAwait(false);

                    // 写入 hex dump
                    lock (dumpWriter)
                    {
                        dumpWriter.WriteLine(string.Format("--- {0} offset={1} len={2} ---",
                            label, offset, n));
                        WriteHexDump(dumpWriter, buf, n, hexBuf);
                        dumpWriter.WriteLine();
                        dumpWriter.Flush();
                    }

                    await dest.WriteAsync(buf, 0, n).ConfigureAwait(false);
                    offset += n;
                }
            }
            catch (IOException) { } // 对端关闭
            catch (ObjectDisposedException) { }
        }

        private static void WriteHexDump(StreamWriter w, byte[] buf, int len, char[] hexBuf)
        {
            for (int row = 0; row < len; row += 16)
            {
                var rowLen = Math.Min(16, len - row);
                var hexIdx = 0;
                for (int i = 0; i < rowLen; i++)
                {
                    var b = buf[row + i];
                    hexBuf[hexIdx++] = HexChar(b >> 4);
                    hexBuf[hexIdx++] = HexChar(b & 0xF);
                    hexBuf[hexIdx++] = ' ';
                }
                hexBuf[hexIdx - 1] = ' '; // 最后一个空格替换
                w.Write(hexBuf, 0, hexIdx);

                // ASCII 列
                w.Write(" |");
                for (int i = 0; i < rowLen; i++)
                {
                    var b = buf[row + i];
                    w.Write(b >= 32 && b < 127 ? (char)b : '.');
                }
                w.WriteLine("|");
            }
        }

        private static char HexChar(int nibble)
        {
            nibble &= 0xF;
            return (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
        }

        /// <summary>找到一个可用的本地端口（127.0.0.1 随机端口）。</summary>
        public static int PickFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    /// <summary>
    /// RDP 抓包代理静态管理器——连接级自动抓包入口。
    /// 单例模式：一次只支持一个抓包会话。
    /// </summary>
    public static class RdpDumpProxy
    {
        private static RdpTcpProxy _instance;
        private static readonly object _lock = new object();

        /// <summary>是否正在抓包。</summary>
        public static bool IsRunning
        {
            get { lock (_lock) return _instance != null && _instance.IsRunning; }
        }

        /// <summary>
        /// 启动抓包代理。自动选择空闲端口，返回本地端口号。
        /// </summary>
        /// <param name="targetHost">真实目标 IP/主机名</param>
        /// <param name="targetPort">真实目标端口</param>
        /// <param name="dumpDir">hex dump 输出目录</param>
        /// <returns>本地监听端口（127.0.0.1:port）</returns>
        public static int StartFor(string targetHost, int targetPort, string dumpDir)
        {
            lock (_lock)
            {
                if (_instance != null && _instance.IsRunning)
                    throw new InvalidOperationException("已有抓包代理在运行中");

                var port = RdpTcpProxy.PickFreePort();
                _instance = new RdpTcpProxy(dumpDir);
                _instance.Start(port, targetHost, targetPort);
                return port;
            }
        }

        /// <summary>停止抓包代理。</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                try { _instance?.Stop(); } catch { }
                try { _instance?.Dispose(); } catch { }
                _instance = null;
            }
        }
    }
}