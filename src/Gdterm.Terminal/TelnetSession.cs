using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Gdterm.Core.Models;
using Gdterm.Terminal.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// Telnet 终端会话——RFC 854 最小实现：裸 Socket + IAC 过滤 + 选项一律拒绝。
    /// 协商策略：收到 DO → 回 WONT；收到 WILL → 回 DONT；绝不主动发起协商。
    /// 对端（交换机/路由器/工控 console）看到 WONT/DONT 后即回落裸 NVT ASCII，
    /// 足够 login:/Password: 交互与 CLI 会话；NAWS/TTYPE 等高级选项不在 scope。
    /// 无加密——连接对话框明示；敏感环境请用 SSH。
    /// </summary>
    public class TelnetSession : ITerminalSession
    {
        // RFC 854 控制字节
        private const byte IAC = 255;   // Interpret As Command
        private const byte DONT = 254;
        private const byte DO = 253;
        private const byte WONT = 252;
        private const byte WILL = 251;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private readonly List<string> _outputBuffer = new List<string>();
        private readonly StringBuilder _currentLine = new StringBuilder();
        private readonly object _lock = new object();
        private Thread _readThread;
        private volatile bool _connected;
        private readonly List<byte> _pendingReply = new List<byte>();
        private readonly object _replyLock = new object();

        private const int MaxBufferLines = 500;
        private int _disconnectRaised;

        public string ConnectionId { get; private set; }
        public string Hostname { get; private set; }
        public string OsType => "Unknown";
        public bool IsConnected => _connected && _tcp != null && _tcp.Connected;

        public object TryGetSshClient() => null;

        public event EventHandler<TerminalOutputEventArgs> OutputReceived;
        public event EventHandler Disconnected;

        public void Connect(ConnectionConfig config, CredentialPayload credential, int rows = 24, int columns = 80)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var host = (config.Host ?? "").Trim();
            if (host.Length == 0) throw new ArgumentException("Telnet 主机地址不能为空");
            int port = config.Port > 0 ? config.Port : 23;

            ConnectionId = config.Id;
            Hostname = host;

            _tcp = new TcpClient();
            var ar = _tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(15)))
            {
                try { _tcp.Close(); } catch { }
                throw new TimeoutException("Telnet 连接超时: " + host + ":" + port);
            }
            try { _tcp.EndConnect(ar); } catch (Exception ex) { throw new InvalidOperationException("Telnet 连接失败: " + ex.Message, ex); }
            _tcp.ReceiveTimeout = 500;
            _stream = _tcp.GetStream();

            _connected = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "Telnet-" + host };
            _readThread.Start();
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, int rows = 24, int columns = 80)
        {
            if (tunnelEndpoint == null) throw new ArgumentNullException(nameof(tunnelEndpoint));
            // 跳板本地端口即明文 TCP 入口：把目标改写为本地转发端即可复用 Connect。
            var local = new ConnectionConfig
            {
                Id = config != null ? config.Id : null,
                Host = tunnelEndpoint.LocalHost,
                Port = tunnelEndpoint.LocalPort
            };
            Connect(local, credential, rows, columns);
        }

        public void SendInput(string text)
        {
            if (!IsConnected) throw new InvalidOperationException("Telnet 未连接");
            if (string.IsNullOrEmpty(text)) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (_replyLock)
            {
                try { _stream.Write(bytes, 0, bytes.Length); _stream.Flush(); }
                catch { }
            }
        }

        public void SendBytes(byte[] data)
        {
            if (!IsConnected) throw new InvalidOperationException("Telnet 未连接");
            if (data == null || data.Length == 0) return;
            lock (_replyLock)
            {
                try { _stream.Write(data, 0, data.Length); _stream.Flush(); }
                catch { }
            }
        }

        public void Resize(int columns, int rows) { /* NVT 无 window-change */ }

        public bool IsZmodemReceiving { get { return false; } }

        public void StartZmodemReceive(string saveDirectory)
        {
            throw new NotSupportedException("Zmodem 接收仅支持 SSH 会话（当前：Telnet）。");
        }

        public IList<string> GetRecentOutput(int lineCount)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _outputBuffer.Count - lineCount);
                var result = new List<string>();
                for (int i = start; i < _outputBuffer.Count; i++) result.Add(_outputBuffer[i]);
                return result;
            }
        }

        public string GetSelection() { return string.Empty; }

        public void Dispose()
        {
            _connected = false;
            RaiseDisconnected();
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }

        private void ReadLoop()
        {
            var buffer = new byte[4096];
            while (_connected && _tcp != null && _tcp.Connected)
            {
                int bytesRead = 0;
                try { bytesRead = _stream.Read(buffer, 0, buffer.Length); }
                catch (System.IO.IOException) { continue; } // ReceiveTimeout 正常抖动
                catch { break; }
                if (bytesRead <= 0) break;
                string text = FilterIac(buffer, bytesRead);
                FlushReply();
                if (!string.IsNullOrEmpty(text)) ProcessOutput(text);
            }
            if (_connected) { _connected = false; RaiseDisconnected(); }
            else RaiseDisconnected();
        }

        /// <summary>
        /// IAC 过滤：IAC DO opt → 回 IAC WONT opt；IAC WILL opt → 回 IAC DONT opt；
        /// IAC IAC → 转义为单个 0xFF 数据字节；其余协商/SB 整段吞掉。
        /// </summary>
        private string FilterIac(byte[] buf, int count)
        {
            var sb = new StringBuilder(count);
            int i = 0;
            while (i < count)
            {
                byte b = buf[i];
                if (b != IAC) { sb.Append((char)b); i++; continue; }
                if (i + 1 >= count) break; // 截断的 IAC 丢弃（下包重起，协商可丢）
                byte cmd = buf[i + 1];
                if (cmd == IAC) { sb.Append((char)255); i += 2; continue; }
                if ((cmd == DO || cmd == DONT || cmd == WILL || cmd == WONT) && i + 2 < count)
                {
                    byte opt = buf[i + 2];
                    lock (_replyLock)
                    {
                        // DO → WONT（我方不做任何选项）；WILL → DONT（对端别做）
                        _pendingReply.Add(IAC);
                        _pendingReply.Add(cmd == DO ? WONT : DONT);
                        _pendingReply.Add(opt);
                    }
                    i += 3;
                    continue;
                }
                // SB…SE 或未知命令：吞到下一个 IAC SE 为止（SB 段内 IAC 转义暂不处理，网络设备极少发 SB）
                i += 2;
                while (i + 1 < count && !(buf[i] == IAC && buf[i + 1] == 0xF0 /*SE*/)) i++;
                i += 2;
            }
            // Latin1 逐字节→char 再按 UTF-8/GB 双解码由上层渲染负责；此处保持字节透明
            return sb.ToString();
        }

        private void FlushReply()
        {
            byte[] reply = null;
            lock (_replyLock)
            {
                if (_pendingReply.Count == 0) return;
                reply = _pendingReply.ToArray();
                _pendingReply.Clear();
            }
            try { _stream.Write(reply, 0, reply.Length); _stream.Flush(); } catch { }
        }

        private void ProcessOutput(string text)
        {
            lock (_lock)
            {
                foreach (char ch in text)
                {
                    if (ch == '\n')
                    {
                        _outputBuffer.Add(_currentLine.ToString());
                        _currentLine.Clear();
                        while (_outputBuffer.Count > MaxBufferLines) _outputBuffer.RemoveAt(0);
                    }
                    else if (ch != '\r' && ch != '\0') _currentLine.Append(ch);
                }
                try { OutputReceived?.Invoke(this, new TerminalOutputEventArgs { Text = text, Timestamp = DateTime.UtcNow }); }
                catch { }
            }
        }

        private void RaiseDisconnected()
        {
            if (System.Threading.Interlocked.Exchange(ref _disconnectRaised, 1) != 0) return;
            try { Disconnected?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }
}
