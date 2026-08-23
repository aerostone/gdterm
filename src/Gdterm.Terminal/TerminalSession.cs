using System;
using System.Collections.Generic;
using System.Text;
using Gdterm.Core.Models;
using Gdterm.Terminal.Diagnostics;
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
        private string _termType = "xterm-256color";
        private int _rows = 24;
        private int _columns = 80;

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
            ApplyTerminalProfile(config, rows, columns);

            var connInfo = SshConnectionInfoFactory.Create(
                config.Host,
                config.Port,
                credential.Username ?? config.Username,
                credential);

            _sshClient = new SshClient(connInfo);
            LogConnect("direct", connInfo, credential);
            try { _sshClient.Connect(); }
            catch (Exception ex)
            {
                // SSH.NET 报错原文（SocketException/PermissionDenied/SshException 等）是排查对接问题的第一证据
                TerminalLog.Swallowed("SshSession.Connect", ex);
                throw;
            }

            CreateShellStream(_rows, _columns);
            TerminalLog.Info("SshSession.Connected",
                "mode=direct host=" + config.Host + ":" + config.Port + " term=" + _termType);
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
            ApplyTerminalProfile(config, rows, columns);

            // 通过隧道的本地端口连接（目标主机认证仍用 credential，含私钥）
            var connInfo = SshConnectionInfoFactory.Create(
                tunnelEndpoint.LocalHost,
                tunnelEndpoint.LocalPort,
                credential.Username ?? config.Username,
                credential);

            _sshClient = new SshClient(connInfo);
            LogConnect("tunnel->" + tunnelEndpoint.LocalHost + ":" + tunnelEndpoint.LocalPort, connInfo, credential);
            try { _sshClient.Connect(); }
            catch (Exception ex)
            {
                TerminalLog.Swallowed("SshSession.Connect", ex);
                throw;
            }

            CreateShellStream(_rows, _columns);
            TerminalLog.Info("SshSession.Connected",
                "mode=tunnel target=" + config.Host + ":" + config.Port +
                " via=" + tunnelEndpoint.LocalHost + ":" + tunnelEndpoint.LocalPort + " term=" + _termType);
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

        public void SendBytes(byte[] data)
        {
            if (!IsConnected) throw new InvalidOperationException("终端未连接");
            if (data == null || data.Length == 0) return;
            _shellStream.Write(data, 0, data.Length);
            _shellStream.Flush();
        }

        /// <summary>
        /// SSH.NET 2024 ShellStream 无公开 window-change API；
        /// 尽力用反射调用 ChannelSession.SendWindowChangeRequest；失败则静默（本地 cell 仍会 resize）。
        /// </summary>
        public void Resize(int columns, int rows)
        {
            if (columns < 2) columns = 2;
            if (rows < 1) rows = 1;
            _columns = columns;
            _rows = rows;
            if (!IsConnected) return;

            try
            {
                // 私有字段 _channel : IChannelSession
                var field = typeof(ShellStream).GetField("_channel",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var channel = field != null ? field.GetValue(_shellStream) : null;
                if (channel == null) return;

                var method = channel.GetType().GetMethod("SendWindowChangeRequest",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method == null) return;

                // (columns, rows, width, height)
                method.Invoke(channel, new object[] { (uint)columns, (uint)rows, (uint)(columns * 8), (uint)(rows * 16) });
            }
            catch
            {
                // best-effort：远端可能保持旧尺寸，本地引擎仍按新尺寸渲染
            }
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

        private void ApplyTerminalProfile(ConnectionConfig config, int rows, int columns)
        {
            _rows = rows > 0 ? rows : 24;
            _columns = columns > 0 ? columns : 80;
            try
            {
                var profile = TerminalProfile.FromMetadata(config?.Metadata);
                if (profile != null && !string.IsNullOrWhiteSpace(profile.TerminalType))
                    _termType = profile.TerminalType.Trim();
            }
            catch
            {
                _termType = "xterm-256color";
            }
        }

        private void CreateShellStream(int rows, int columns)
        {
            _rows = rows;
            _columns = columns;
            // TERM 来自 TerminalProfile；默认 xterm-256color（真彩/TUI 友好）
            var term = string.IsNullOrWhiteSpace(_termType) ? "xterm-256color" : _termType;
            _shellStream = _sshClient.CreateShellStream(
                term,
                (uint)columns,
                (uint)rows,
                (uint)Math.Max(80, columns * 8),
                (uint)Math.Max(24, rows * 16),
                8192);

            // 不再自动 uname：会污染 TUI/真彩会话首屏
            OsType = "Unknown";

            // 启动数据接收循环
            StartReading();
        }

        /// <summary>连接前记录对接参数（不含任何机密值；authMethod 只记类型不记内容）。</summary>
        private static void LogConnect(string mode, Renci.SshNet.ConnectionInfo connInfo, CredentialPayload credential)
        {
            try
            {
                bool hasKey = credential != null && credential.SshPrivateKey != null && credential.SshPrivateKey.Length > 0;
                bool hasPwd = credential != null && !string.IsNullOrWhiteSpace(credential.Password);
                string auth = hasKey ? (hasPwd ? "key+password" : "key") : (hasPwd ? "password" : "none");
                var methodNames = new System.Text.StringBuilder();
                if (connInfo != null && connInfo.AuthenticationMethods != null)
                    foreach (var am in connInfo.AuthenticationMethods)
                    {
                        if (methodNames.Length > 0) methodNames.Append(',');
                        methodNames.Append(am != null ? am.Name : "?");
                    }
                TerminalLog.Info("SshSession.Connect",
                    "mode=" + mode + " auth=" + auth + " sshnetMethods=" + methodNames +
                    " timeoutMs=" + (connInfo != null ? connInfo.Timeout.TotalMilliseconds.ToString("0") : "?"));
            }
            catch { }
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
                // SSH.NET ShellStream 错误原文——断线排查第一证据
                TerminalLog.Swallowed("SshSession.ShellError", e.Exception);
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
                    _sshClient.ErrorOccurred += (s2, e2) =>
                    {
                        TerminalLog.Swallowed("SshSession.ClientError", e2.Exception);
                        RaiseDisconnected();
                    };
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

    }
}
