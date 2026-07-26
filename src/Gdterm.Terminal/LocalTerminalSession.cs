using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Gdterm.Core.Models;
using Gdterm.Terminal.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 本地终端会话——启动本地 CMD/PowerShell/Bash 进程
    /// </summary>
    public class LocalTerminalSession : ITerminalSession
    {
        private Process _process;
        private readonly object _lock = new object();
        private readonly List<string> _outputBuffer = new List<string>();
        private string _shellPath;
        private string _workingDirectory;
        private bool _disposed;
        private const int MaxBufferLines = 500;

        public string ConnectionId { get; private set; }
        public string Hostname { get { return "localhost"; } }
        public string OsType { get; private set; }
        public bool IsConnected
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        public event EventHandler<TerminalOutputEventArgs> OutputReceived;

        public event EventHandler Disconnected;

        private int _disconnectRaised;

        public object TryGetSshClient() => null;

        public LocalTerminalSession(string shellPath = null, string workingDirectory = null)
        {
            _shellPath = shellPath;
            _workingDirectory = workingDirectory;
            OsType = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Windows" : "Linux";
        }

        public void Connect(ConnectionConfig config, CredentialPayload credential, int rows = 24, int columns = 80)
        {
            ConnectionId = config != null ? config.Id : Guid.NewGuid().ToString("N");
            ConnectLocal();
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, int rows = 24, int columns = 80)
        {
            throw new NotSupportedException("本地终端不支持隧道连接");
        }

        /// <summary>启动本地 shell</summary>
        public void ConnectLocal()
        {
            lock (_lock)
            {
                if (IsConnected) return;

                if (string.IsNullOrEmpty(_shellPath))
                {
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        _shellPath = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                        OsType = "Windows";
                    }
                    else
                    {
                        _shellPath = "/bin/bash";
                        OsType = "Linux";
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
                    // Windows 控制台默认代码页；强制 UTF-8 会导致乱码/看似无输出
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };

                if (!string.IsNullOrEmpty(_workingDirectory))
                    startInfo.WorkingDirectory = _workingDirectory;
                else
                    startInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (_shellPath.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    _shellPath.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    OsType = "Windows";
                    startInfo.Arguments = "-NoLogo -NoProfile";
                }

                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnOutputData;
                _process.ErrorDataReceived += OnErrorData;
                _process.Exited += OnProcessExited;

                try
                {
                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                    // 给 UI 一个可见就绪提示（重定向 shell 往往没有交互式 prompt）
                    try
                    {
                        var banner = "\r\n[gdterm] 本地终端已启动: " + _shellPath + "\r\n";
                        RaiseOutput(banner);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    CleanupProcess();
                    throw new InvalidOperationException("无法启动本地终端: " + ex.Message, ex);
                }
            }
        }

        public void SendInput(string text)
        {
            lock (_lock)
            {
                if (!IsConnected || _process == null) return;
                try
                {
                    _process.StandardInput.Write(text);
                    _process.StandardInput.Flush();
                }
                catch { }
            }
        }

        public void SendBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            try
            {
                var text = Encoding.UTF8.GetString(data);
                SendInput(text);
            }
            catch { }
        }

        /// <summary>本地进程无 PTY window-change。</summary>
        public void Resize(int columns, int rows)
        {
            // no-op
        }

        public void SendBreak(int durationMs = 100)
        {
            SendInput("\x03");
        }

        public IList<string> GetRecentOutput(int lineCount)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _outputBuffer.Count - lineCount);
                return _outputBuffer.GetRange(start, _outputBuffer.Count - start);
            }
        }

        public string GetSelection()
        {
            return string.Empty;
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
                        try
                        {
                            _process.StandardInput.Write("exit\r\n");
                            _process.StandardInput.Flush();
                        }
                        catch { }

                        if (!_process.WaitForExit(3000))
                        {
                            try { _process.Kill(); } catch { }
                        }
                    }
                }
                catch { }
                CleanupProcess();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            RaiseDisconnected();
        }

        private void RaiseOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                _outputBuffer.Add(text);
                while (_outputBuffer.Count > MaxBufferLines)
                    _outputBuffer.RemoveAt(0);
            }
            try { OutputReceived?.Invoke(this, new TerminalOutputEventArgs { Text = text }); } catch { }
        }

        private void OnOutputData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            AppendAndRaise(e.Data + "\r\n");
        }

        private void OnErrorData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            AppendAndRaise(e.Data + "\r\n");
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            AppendAndRaise("\r\n[本地终端已退出]\r\n");
            RaiseDisconnected();
        }

        private void RaiseDisconnected()
        {
            if (System.Threading.Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
                return;
            try { Disconnected?.Invoke(this, EventArgs.Empty); }
            catch { }
        }

        private void AppendAndRaise(string text)
        {
            lock (_lock)
            {
                _outputBuffer.Add(text);
                while (_outputBuffer.Count > MaxBufferLines)
                    _outputBuffer.RemoveAt(0);
            }

            try
            {
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs { Text = text });
            }
            catch { }
        }

        private void CleanupProcess()
        {
            if (_process == null) return;
            try
            {
                _process.OutputDataReceived -= OnOutputData;
                _process.ErrorDataReceived -= OnErrorData;
                _process.Exited -= OnProcessExited;
                _process.Dispose();
            }
            catch { }
            _process = null;
        }
    }
}
