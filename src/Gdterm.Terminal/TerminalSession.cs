using System;
using System.Collections.Generic;
using System.Text;
using Gdterm.Core.Models;
using Gdterm.Terminal.Diagnostics;
using Gdterm.Terminal.Models;
using Renci.SshNet;
using Gdterm.Core;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端会话实现——管理 SSH 连接 + ShellStream 的生命周期
    /// </summary>
    public class TerminalSession : ITerminalSession
    {
        private SshClient _sshClient;
        private ShellStream _shellStream;
        private readonly Transfer.ZmodemHandler _zmodem = new Transfer.ZmodemHandler();
        private bool _zmodemHinted;
        private Transfer.ZmodemReceiver _zmodemRx;
        private bool _zmodemEventsWired;
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

            var connInfo = Gdterm.Tunnel.SshConnectionInfoFactory.Create(
                config.Host,
                config.Port,
                credential.Username ?? config.Username,
                credential,
                30,
                config.Proxy);

            _sshClient = Gdterm.Tunnel.SshHostKeyGuard.CreateClient(connInfo, config.Host, config.Port);
            Gdterm.Tunnel.SshKeepAlive.Apply(_sshClient, config);
            LogConnect("direct", connInfo, credential);
            try { _sshClient.Connect(); }
            catch (Exception ex)
            {
                // SSH.NET 报错原文（SocketException/PermissionDenied/SshException 等）是排查对接问题的第一证据
                TerminalLog.Swallowed("SshSession.Connect", ex);
                throw Gdterm.Tunnel.SshHostKeyGuard.Describe(_sshClient, ex);
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
            var connInfo = Gdterm.Tunnel.SshConnectionInfoFactory.Create(
                tunnelEndpoint.LocalHost,
                tunnelEndpoint.LocalPort,
                credential.Username ?? config.Username,
                credential);

            // 隧道里握手的是**目标机**：钉住的必须是 config.Host/config.Port，
            // 否则同一台堡垒机后面的所有目标共用 127.0.0.1:localPort 这一条记录。
            _sshClient = Gdterm.Tunnel.SshHostKeyGuard.CreateClient(connInfo, config.Host, config.Port);
            Gdterm.Tunnel.SshKeepAlive.Apply(_sshClient, config);
            LogConnect("tunnel->" + tunnelEndpoint.LocalHost + ":" + tunnelEndpoint.LocalPort, connInfo, credential);
            try { _sshClient.Connect(); }
            catch (Exception ex)
            {
                TerminalLog.Swallowed("SshSession.Connect", ex);
                throw Gdterm.Tunnel.SshHostKeyGuard.Describe(_sshClient, ex);
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
            if (!IsConnected) throw new InvalidOperationException(L10n.T("终端未连接"));
            if (string.IsNullOrEmpty(text)) return;

            _shellStream.Write(text);
            _shellStream.Flush();
        }

        public void SendBytes(byte[] data)
        {
            if (!IsConnected) throw new InvalidOperationException(L10n.T("终端未连接"));
            if (data == null || data.Length == 0) return;
            _shellStream.Write(data, 0, data.Length);
            _shellStream.Flush();
        }

        public bool IsZmodemReceiving
        {
            get { return _zmodemRx != null; }
        }

        /// <summary>开始 Zmodem 接收（用户确认后调用）：后续远端字节进 Receiver，含帧包抑制渲染。</summary>
        public void StartZmodemReceive(string saveDirectory)
        {
            if (!IsConnected) throw new InvalidOperationException(L10n.T("终端未连接"));
            if (string.IsNullOrEmpty(saveDirectory)) throw new ArgumentNullException("saveDirectory");
            if (_zmodemRx != null) throw new InvalidOperationException(L10n.T("已有接收在进行中"));
            var rx = new Transfer.ZmodemReceiver();
            rx.Start(saveDirectory);
            if (!_zmodemEventsWired)
            {
                _zmodemEventsWired = true;
            }
            rx.FileStarted += (s, fe) =>
            {
                try
                {
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs
                    {
                        Text = L10n.T("\r\n[Zmodem] 开始接收 ") + (fe != null ? fe.FileName : "") + "（" + (fe != null ? fe.FileSize : 0) + L10n.T(" 字节）…\r\n"),
                        Timestamp = DateTime.UtcNow
                    });
                }
                catch { }
            };
            rx.FileCompleted += (s, fe) =>
            {
                try
                {
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs
                    {
                        Text = L10n.T("\r\n[Zmodem] 接收完成 ") + (fe != null ? fe.FileName : "") + "（" + (fe != null ? fe.FileSize : 0) + L10n.T(" 字节）。\r\n"),
                        Timestamp = DateTime.UtcNow
                    });
                }
                catch { }
            };
            rx.Error += (s, ee) =>
            {
                try
                {
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs
                    {
                        Text = L10n.T("\r\n[Zmodem] 接收失败：") + (ee != null ? ee.Error : "?") + "\r\n",
                        Timestamp = DateTime.UtcNow
                    });
                }
                catch { }
            };
            _zmodemRx = rx;
            // 立即把 Start 排队的 ZRINIT 发出去
            try
            {
                byte[] reply;
                while ((reply = rx.TakeReply()) != null) SendBytes(reply);
            }
            catch { }
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

        // UTF-8 流式解码状态：远端一包 4096 字节会把 3 字节汉字（或 emoji）切在两包之间，
        // 之前逐包 Encoding.UTF8.GetString 解 → 半个字符变 U+FFFD，中文文件名/中文提示乱码。
        private readonly Decoder _utf8Decoder = new UTF8Encoding(false).GetDecoder();
        private char[] _utf8Chars = new char[4096 + 8];

        /// <summary>把原始字节解成文本（跨包保留不完整的多字节序列）。</summary>
        private string DecodeUtf8(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            if (_utf8Chars.Length < data.Length + 8)
                _utf8Chars = new char[data.Length + 8];
            int n;
            try
            {
                n = _utf8Decoder.GetChars(data, 0, data.Length, _utf8Chars, 0, false);
            }
            catch
            {
                // 极端脏数据：丢弃解码状态，退回一次性解码（保证输出不断流）
                _utf8Decoder.Reset();
                return Encoding.UTF8.GetString(data);
            }
            return n <= 0 ? string.Empty : new string(_utf8Chars, 0, n);
        }

        private void StartReading()
        {
            var buffer = new byte[4096];
            try { _utf8Decoder.Reset(); } catch { }

            _shellStream.DataReceived += (sender, e) =>
            {
                try
                {
                    // Zmodem 接收中：字节喂 Receiver，应答回写，含帧的包抑制渲染
                    var rx = _zmodemRx;
                    if (rx != null && e != null && e.Data != null && e.Data.Length > 0)
                    {
                        try
                        {
                            bool consumed = rx.Feed(e.Data, 0, e.Data.Length);
                            byte[] reply;
                            while ((reply = rx.TakeReply()) != null)
                            {
                                try { SendBytes(reply); } catch { break; }
                            }
                            if (rx.State == Transfer.ZmodemReceiver.RecvState.Done
                                || rx.State == Transfer.ZmodemReceiver.RecvState.Aborted)
                            {
                                _zmodemRx = null;
                                try { rx.Dispose(); } catch { }
                            }
                            if (consumed) return;
                        }
                        catch { }
                    }
                    // Zmodem 检测：远端 sz 起传时提示（接收子集已实现，不再转 SFTP）
                    try
                    {
                        if (!_zmodemHinted && e != null && e.Data != null
                            && _zmodem.DetectZmodemStart(e.Data, 0, e.Data.Length))
                        {
                            _zmodemHinted = true;
                            OutputReceived?.Invoke(this, new TerminalOutputEventArgs
                            {
                                Text = L10n.T("\r\n[Zmodem] 检测到远端 sz 发送请求。按 Ctrl+Shift+Z 接收（或用 SFTP 浏览器传文件）。\r\n"),
                                Timestamp = DateTime.UtcNow
                            });
                        }
                    }
                    catch { }
                    var text = DecodeUtf8(e.Data);
                    if (text.Length == 0) return;
                    ProcessOutput(text);
                }
                catch
                {
                    // 数据解析错误不中断会话
                }
            };

            // 远端礼貌关闭通道（用户输 exit / sshd 踢会话 / 空闲超时）时 ShellStream 只发 Closed，
            // 之前只订了 ErrorOccurred 与 sshClient 的 ErrorOccurred —— 这条最常见路径上
            // Disconnected 永不触发：终端窗口停在最后一帧、看起来还活着，按键开始静默丢弃，
            // 自动重连看门狗（订阅 SessionDisconnected）也就永不接手。
            // 2026-11-11 端到端实证：假服务器 EXITNOW → IsConnected 已变 false，Disconnected 8s 内未触发。
            _shellStream.Closed += (sender, e) =>
            {
                TerminalLog.Info("SshSession.ShellClosed", "远端关闭 shell 通道");
                RaiseDisconnected();
            };

            _shellStream.ErrorOccurred += (sender, e) =>
            {
                // SSH.NET ShellStream 错误原文——断线排查第一证据
                TerminalLog.Swallowed("SshSession.ShellError", e.Exception);
                try
                {
                    OutputReceived?.Invoke(this, new TerminalOutputEventArgs
                    {
                        Text = "\r\n" + L10n.Tf("[终端错误: {0}]", e.Exception?.Message) + "\r\n",
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
