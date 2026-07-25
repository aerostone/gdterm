using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using Gdterm.Core.Models;
using Gdterm.Terminal.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 串口终端会话——使用 System.IO.Ports 实现串口通信
    /// 支持常见的串口设备：路由器/交换机控制台、嵌入式设备、工控设备等
    /// </summary>
    public class SerialSession : ITerminalSession
    {
        private SerialPort _port;
        private readonly List<string> _outputBuffer = new List<string>();
        private readonly StringBuilder _currentLine = new StringBuilder();
        private readonly object _lock = new object();
        private Thread _readThread;
        private volatile bool _connected;

        private const int MaxBufferLines = 500;

        public string ConnectionId { get; private set; }
        public string Hostname { get; private set; }
        public string OsType => "Serial";
        public bool IsConnected => _connected && _port?.IsOpen == true;

        public event EventHandler<TerminalOutputEventArgs> OutputReceived;

        /// <summary>
        /// 连接到串口设备
        /// </summary>
        /// <param name="config">连接配置（使用 Serial 属性）</param>
        public void Connect(ConnectionConfig config, CredentialPayload credential, int rows = 24, int columns = 80)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.Serial == null)
                throw new ArgumentException("串口配置不能为空，请设置 ConnectionConfig.Serial");

            ConnectionId = config.Id;
            Hostname = config.Serial.PortName;

            var serialConfig = config.Serial;

            _port = new SerialPort
            {
                PortName = serialConfig.PortName,
                BaudRate = serialConfig.BaudRate,
                DataBits = serialConfig.DataBits,
                StopBits = serialConfig.StopBits,
                Parity = serialConfig.Parity,
                Handshake = serialConfig.Handshake,
                ReadTimeout = serialConfig.ReadTimeout,
                WriteTimeout = serialConfig.WriteTimeout,
                DtrEnable = serialConfig.DtrEnable,
                RtsEnable = serialConfig.RtsEnable,
                Encoding = Encoding.UTF8,
                NewLine = serialConfig.NewLine
            };

            _port.Open();
            _connected = true;

            // 启动读取线程
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"Serial-{serialConfig.PortName}"
            };
            _readThread.Start();
        }

        /// <summary>
        /// 串口不需要隧道连接，调用 Connect 即可
        /// </summary>
        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, int rows = 24, int columns = 80)
        {
            throw new NotSupportedException("串口连接不支持隧道模式");
        }

        public void SendInput(string text)
        {
            if (!IsConnected) throw new InvalidOperationException("串口未连接");
            if (string.IsNullOrEmpty(text)) return;

            _port.Write(text);
        }

        /// <summary>
        /// 发送原始字节（用于特殊控制字符）
        /// </summary>
        public void SendBytes(byte[] data)
        {
            if (!IsConnected) throw new InvalidOperationException("串口未连接");
            if (data == null || data.Length == 0) return;

            _port.Write(data, 0, data.Length);
        }

        /// <summary>
        /// 发送 Break 信号（用于某些设备的中断）
        /// </summary>
        public void SendBreak(int durationMs = 100)
        {
            if (!IsConnected) throw new InvalidOperationException("串口未连接");
            _port.BreakState = true;
            Thread.Sleep(durationMs);
            _port.BreakState = false;
        }

        public IList<string> GetRecentOutput(int lineCount)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _outputBuffer.Count - lineCount);
                var result = new List<string>();
                for (int i = start; i < _outputBuffer.Count; i++)
                    result.Add(_outputBuffer[i]);
                return result;
            }
        }

        public string GetSelection()
        {
            return string.Empty;
        }

        public void Dispose()
        {
            _connected = false;

            try
            {
                _port?.Close();
                _port?.Dispose();
            }
            catch { }

            _port = null;
        }

        private void ReadLoop()
        {
            var buffer = new byte[4096];

            while (_connected && _port?.IsOpen == true)
            {
                try
                {
                    int bytesRead = _port.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        ProcessOutput(text);
                    }
                }
                catch (TimeoutException)
                {
                    // 超时是正常的，继续读取
                }
                catch
                {
                    if (_connected)
                    {
                        _connected = false;
                        break;
                    }
                }
            }
        }

        private void ProcessOutput(string text)
        {
            lock (_lock)
            {
                foreach (char ch in text)
                {
                    if (ch == '\n')
                    {
                        var line = _currentLine.ToString();
                        _outputBuffer.Add(line);
                        _currentLine.Clear();

                        while (_outputBuffer.Count > MaxBufferLines)
                            _outputBuffer.RemoveAt(0);
                    }
                    else if (ch != '\r')
                    {
                        _currentLine.Append(ch);
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
}
