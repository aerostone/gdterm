using System;
using System.Collections.Generic;
using System.Text;
using Gdterm.Core.Models;
using Gdterm.Terminal.Models;
using Renci.SshNet;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端会话实现——管理 SSH 连接 + ShellStream 的生命周期
    /// </summary>
    public class TerminalSession : ITerminalSession
    {
        private SshClient _sshClient;
        private ShellStream _shellStream;
        private readonly List<string> _outputBuffer = new List<string>();
        private readonly StringBuilder _lineBuilder = new StringBuilder();
        private readonly object _lock = new object();
        private const int MaxBufferLines = 500;
        private bool _disposed;

        public string ConnectionId { get; private set; }
        public string Hostname { get; private set; }
        public string OsType { get; private set; }
        public bool IsConnected => _sshClient?.IsConnected == true && _shellStream != null;

        /// <summary>
        /// 底层 SSH.NET 客户端（端口转发/远程工具注入用）。
        /// 仅在已连接时有效；调用方不得 Disconnect/Dispose。
        /// </summary>
        public SshClient UnderlyingClient =>
            _sshClient != null && _sshClient.IsConnected ? _sshClient : null;

        /// <inheritdoc />
        public object TryGetSshClient() => UnderlyingClient;

        public event EventHandler<TerminalOutputEventArgs> OutputReceived;

        /// <inheritdoc />
        public event EventHandler Disconnected;

        private int _disconnectRaised;

        /// <summary>
        /// 直连模式：直接连接 config.Host:config.Port
        /// </summary>
        public void Connect(ConnectionConfig config, CredentialPayload credential, int rows = 24, int columns = 80)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            ConnectionId = config.Id;
            Hostname = config.Host;

            var connInfo = SshConnectionInfoFactory.Create(
                config.Host,
                config.Port,
                credential.Username ?? config.Username,
                credential);

            _sshClient = new SshClient(connInfo);
            _sshClient.Connect();

            CreateShellStream(rows, columns);
        }

        /// <summary>
        /// 跳板模式：通过隧道接入点连接
        /// </summary>
        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, int rows = 24, int columns = 80)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (tunnelEndpoint == null) throw new ArgumentNullException(nameof(tunnelEndpoint));

            ConnectionId = config.Id;
            Hostname = config.Host;

            // 通过隧道的本地端口连接（目标主机认证仍用 credential，含私钥）
            var connInfo = SshConnectionInfoFactory.Create(
                tunnelEndpoint.LocalHost,
                tunnelEndpoint.LocalPort,
                credential.Username ?? config.Username,
                credential);

            _sshClient = new SshClient(connInfo);
            _sshClient.Connect();

            CreateShellStream(rows, columns);
        }

        public IList<string> GetRecentOutput(int lineCount)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _outputBuffer.Count - lineCount);
                var result = new List<string>();
                for (int i = start; i < _outputBuffer.Count; i++)
                {
                    result.Add(_outputBuffer[i]);
                }
                return result;
            }
        }

        public string GetSelection()
        {
            // 终端选中文本由渲染器管理，此处返回空
            // UI 层通过 TerminalControl.GetSelection() 获取
            return string.Empty;
        }

        public void SendInput(string text)
        {
            if (!IsConnected) throw new InvalidOperationException("终端未连接");
            if (string.IsNullOrEmpty(text)) return;

            _shellStream.Write(text);
            _shellStream.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RaiseDisconnected();

            try
            {
                _shellStream?.Close();
                _shellStream?.Dispose();
            }
            catch { /* best-effort */ }

            try
            {
                if (_sshClient?.IsConnected == true)
                    _sshClient.Disconnect();
                _sshClient?.Dispose();
            }
            catch { /* best-effort */ }

            _shellStream = null;
            _sshClient = null;
        }

        private void CreateShellStream(int rows, int columns)
        {
            _shellStream = _sshClient.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 800, 600, 1024);

            // 检测 OS 类型（异步，不阻塞连接）
            DetectOsType();

            // 启动数据接收循环
            StartReading();
        }

        private void StartReading()
        {
            var buffer = new byte[4096];

            _shellStream.DataReceived += (sender, e) =>
            {
                try
                {
                    var text = Encoding.UTF8.GetString(e.Data);
                    ProcessOutput(text);
                }
                catch
                {
                    // 数据解析错误不中断会话
                }
            };

            _shellStream.ErrorOccurred += (sender, e) =>
            {
                try
                {
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs
                    {
                        Text = $"\r\n[终端错误: {e.Exception?.Message}]\r\n",
                        Timestamp = DateTime.UtcNow
                    });
                }
                catch { /* 避免事件处理异常传播 */ }
                RaiseDisconnected();
            };

            try
            {
                if (_sshClient != null)
                {
                    _sshClient.ErrorOccurred += (s, e) => RaiseDisconnected();
                }
            }
            catch { }
        }

        private void RaiseDisconnected()
        {
            if (System.Threading.Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
                return;
            try { Disconnected?.Invoke(this, EventArgs.Empty); }
            catch { }
        }

        private void ProcessOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                // 按行分割并缓存
                foreach (char ch in text)
                {
                    if (ch == '\n')
                    {
                        var line = _lineBuilder.ToString();
                        _outputBuffer.Add(line);
                        _lineBuilder.Clear();

                        // Trim buffer
                        while (_outputBuffer.Count > MaxBufferLines)
                        {
                            _outputBuffer.RemoveAt(0);
                        }
                    }
                    else if (ch != '\r')
                    {
                        _lineBuilder.Append(ch);
                    }
                }
            }

            // 触发输出事件
            OutputReceived?.Invoke(this, new TerminalOutputEventArgs
            {
                Text = text,
                Timestamp = DateTime.UtcNow
            });
        }

        private void DetectOsType()
        {
            try
            {
                // 尝试通过 uname 检测（异步发送，不影响用户）
                _shellStream.WriteLine("uname -s 2>/dev/null || echo Windows");
                // 结果会通过 DataReceived 事件回来
                // v1 不做实时解析，用户可手动标记
                OsType = "Unknown";
            }
            catch
            {
                OsType = "Unknown";
            }
        }
    }
}
