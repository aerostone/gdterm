using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 本地终端会话——启动本地 CMD/PowerShell/Bash 进程
    /// </summary>
    public class LocalTerminalSession : ITerminalSession, IDisposable
    {
        private Process _process;
        private readonly object _lock = new object();
        private string _shellPath;
        private string _workingDirectory;
        private bool _disposed;

        public bool IsConnected => _process != null && !_process.HasExited;
        public OsType OsType { get; private set; }
        public string HostName => "localhost";
        public int BufferSize { get; set; } = 1000;

        public event Action<string> OutputReceived;
        public event Action<string> ErrorReceived;
        public event Action Disconnected;

        public LocalTerminalSession(string shellPath = null, string workingDirectory = null)
        {
            _shellPath = shellPath;
            _workingDirectory = workingDirectory;
        }

        /// <summary>连接（启动本地 shell 进程）</summary>
        public void Connect(string host, int port, string username, string password)
        {
            // 本地终端忽略远程参数
            ConnectLocal();
        }

        /// <summary>启动本地终端</summary>
        public void ConnectLocal()
        {
            lock (_lock)
            {
                if (IsConnected) return;

                // 自动检测 shell
                if (string.IsNullOrEmpty(_shellPath))
                {
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        _shellPath = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                        OsType = OsType.Windows;
                    }
                    else
                    {
                        _shellPath = "/bin/bash";
                        OsType = OsType.Linux;
                    }
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = _shellPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(_workingDirectory))
                    startInfo.WorkingDirectory = _workingDirectory;

                // PowerShell 特殊处理
                if (_shellPath.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    _shellPath.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    OsType = OsType.Windows;
                    startInfo.Arguments = "-NoLogo -NoProfile";
                }

                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                _process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        OutputReceived?.Invoke(e.Data);
                };

                _process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        ErrorReceived?.Invoke(e.Data);
                };

                _process.Exited += (s, e) =>
                {
                    Disconnected?.Invoke();
                };

                try
                {
                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("无法启动本地终端: " + ex.Message, ex);
                }
            }
        }

        /// <summary>连接到隧道（本地终端不支持）</summary>
        public void ConnectViaTunnel(int localPort, string username, string password)
        {
            throw new NotSupportedException("本地终端不支持隧道连接");
        }

        /// <summary>发送输入到本地终端</summary>
        public void SendInput(string input)
        {
            lock (_lock)
            {
                if (!IsConnected || _process == null) return;
                try
                {
                    _process.StandardInput.Write(input);
                    _process.StandardInput.Flush();
                }
                catch { }
            }
        }

        /// <summary>发送原始字节</summary>
        public void SendBytes(byte[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (!IsConnected || _process == null) return;
                try
                {
                    _process.StandardInput.BaseStream.Write(data, offset, count);
                    _process.StandardInput.BaseStream.Flush();
                }
                catch { }
            }
        }

        /// <summary>发送中断信号（Ctrl+C）</summary>
        public void SendBreak()
        {
            SendInput("\x03");
        }

        public string GetRecentOutput(int lineCount = 50)
        {
            return ""; // 本地终端没有内置缓冲区
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                if (_process == null) return;
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.StandardInput.Write("exit\r");
                        _process.StandardInput.Flush();
                        if (!_process.WaitForExit(3000))
                            _process.Kill();
                    }
                    _process.Close();
                }
                catch { }
                _process = null;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
                lock (_lock)
                {
                    if (_process != null)
                    {
                        _process.OutputDataReceived -= null;
                        _process.ErrorDataReceived -= null;
                        _process.Exited -= null;
                        try { _process.Dispose(); } catch { }
                        _process = null;
                    }
                }
            }
        }
    }
}
