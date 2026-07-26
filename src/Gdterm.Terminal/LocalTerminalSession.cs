using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Terminal.Interop;
using Gdterm.Terminal.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 本地终端：重定向 CMD/PowerShell。
    /// 用后台线程按块读 stdout/stderr（非 OutputDataReceived 行模式），
    /// 否则交互式 prompt 永远不刷新，界面像“进不去”。
    /// </summary>
    public class LocalTerminalSession : ITerminalSession
    {
        private Process _process;
        private readonly object _lock = new object();
        private readonly List<string> _outputBuffer = new List<string>();
        private string _shellPath;
        private string _workingDirectory;
        private bool _disposed;
        private CancellationTokenSource _readCts;
        private const int MaxBufferLines = 500;

        // ConPTY 模式：Win10 1809+ 可用；其他 OS 走旧重定向回退。
        private ConPTY _conpty;
        private bool _usingConPTY;
        private int _cols = 80, _rows = 24;

        /// <summary>当前是否运行在 ConPTY 模式（VtCell/TUI 真彩可坐）。</summary>
        public bool IsConPTYMode => _usingConPTY;
        /// <summary>本会话是否可坐 VT/TUI 渲染：ConPTY 可用且本会话已进入 ConPTY 模式。</summary>
        public bool IsVtCapable => _usingConPTY;
        /// <summary>当前 OS 是否支持 ConPTY（静态检查；Win7 false）。</summary>
        public static bool IsConPTYAvailableOnThisOS() => ConPTY.IsAvailable;

        public string ConnectionId { get; private set; }
        public string Hostname { get { return "localhost"; } }
        public string OsType { get; private set; }
        public bool IsConnected
        {
            get
            {
                try
                {
                    if (_usingConPTY) return _conpty != null && _conpty.IsRunning;
                    return _process != null && !_process.HasExited;
                }
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

        public void ConnectLocal()
        {
            lock (_lock)
            {
                if (IsConnected) return;

                ResolveShell();

                // 优先 ConPTY（Win10 1809+）：真 PTY，交互式 prompt / TUI / 真彩全部可用。
                if (ConPTY.IsAvailable && TryStartConPTY())
                {
                    _usingConPTY = true;
                    RaiseOutput("\r\n[gdterm] 本地终端已启动 (ConPTY): " + _shellPath + "\r\n");
                    return;
                }

                // 回退：重定向 Process（Win7/Server2008 或 ConPTY 创建失败）。
                _usingConPTY = false;
                StartRedirected();
            }
        }

        private void ResolveShell()
        {
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
        }

        /// <summary>尝试以 ConPTY 启动子进程；成功返回 true。</summary>
        private bool TryStartConPTY()
        {
            try
            {
                string commandLine = BuildCommandLineForConPTY(out string _);
                string cwd = !string.IsNullOrEmpty(_workingDirectory)
                    ? _workingDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                _conpty = new ConPTY();
                _conpty.OnOutput += bytes =>
                {
                    if (bytes == null || bytes.Length == 0) return;
                    string text;
                    try { text = Encoding.UTF8.GetString(bytes); }
                    catch { text = Encoding.Default.GetString(bytes); }
                    RaiseOutput(text);
                };
                _conpty.OnExited += (s, e) =>
                {
                    RaiseOutput("\r\n[本地终端已退出]\r\n");
                    RaiseDisconnected();
                };
                if (!_conpty.Start(commandLine, cwd, (short)_cols, (short)_rows))
                    return false;
                return true;
            }
            catch
            {
                try { _conpty?.Dispose(); } catch { }
                _conpty = null;
                return false;
            }
        }

        private string BuildCommandLineForConPTY(out string workingDirectory)
        {
            workingDirectory = !string.IsNullOrEmpty(_workingDirectory)
                ? _workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // ConPTY 不需重定向参数；shell 自己启动交互式。
            if (_shellPath.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0
                || _shellPath.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0)
                return "\"" + _shellPath + "\" -NoLogo";
            if (_shellPath.IndexOf("cmd", StringComparison.OrdinalIgnoreCase) >= 0
                || _shellPath.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
                return "\"" + _shellPath + "\" /K";
            if (_shellPath.IndexOf("bash", StringComparison.OrdinalIgnoreCase) >= 0)
                return "\"" + _shellPath + "\" -i";
            return "\"" + _shellPath + "\"";
        }

        /// <summary>回退重定向实现（Win7 或 ConPTY 不可用）。</summary>
        private void StartRedirected()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _shellPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            };
            startInfo.EnvironmentVariables["TERM"] = "xterm";
            if (!string.IsNullOrEmpty(_workingDirectory))
                startInfo.WorkingDirectory = _workingDirectory;
            else
                startInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (_shellPath.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0
                || _shellPath.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                OsType = "Windows";
                startInfo.Arguments = "-NoLogo -NoExit";
            }
            else if (_shellPath.IndexOf("cmd", StringComparison.OrdinalIgnoreCase) >= 0
                  || string.Equals(Path.GetFileName(_shellPath), "cmd.exe", StringComparison.OrdinalIgnoreCase)
                  || _shellPath.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                OsType = "Windows";
                startInfo.Arguments = "/K prompt $P$G";
            }
            else if (_shellPath.IndexOf("bash", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                startInfo.Arguments = "-i";
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;

            try
            {
                if (!_process.Start())
                    throw new InvalidOperationException("Process.Start 返回 false");

                _readCts = new CancellationTokenSource();
                var token = _readCts.Token;
                var stdout = _process.StandardOutput.BaseStream;
                var stderr = _process.StandardError.BaseStream;
                Task.Factory.StartNew(() => ReadLoop(stdout, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.Factory.StartNew(() => ReadLoop(stderr, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                RaiseOutput("\r\n[gdterm] 本地终端已启动: " + _shellPath + "\r\n");
                try
                {
                    _process.StandardInput.WriteLine();
                    _process.StandardInput.Flush();
                }
                catch { }
            }
            catch (Exception ex)
            {
                CleanupProcess();
                throw new InvalidOperationException("无法启动本地终端: " + ex.Message, ex);
            }
        }

        private void ReadLoop(Stream stream, CancellationToken token)
        {
            var buf = new byte[1024];
            try
            {
                while (!token.IsCancellationRequested && stream != null)
                {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    string text;
                    try { text = Encoding.Default.GetString(buf, 0, n); }
                    catch { text = Encoding.UTF8.GetString(buf, 0, n); }
                    if (!string.IsNullOrEmpty(text))
                        RaiseOutput(text);
                }
            }
            catch { }
        }

        public void SendInput(string text)
        {
            lock (_lock)
            {
                if (!IsConnected) return;
                if (_usingConPTY)
                {
                    if (_conpty == null) return;
                    try
                    {
                        if (text == "\n") text = "\r\n";
                        else if (text == "\r") text = "\r\n";
                        _conpty.Write(Encoding.UTF8.GetBytes(text));
                    }
                    catch { }
                    return;
                }
                if (_process == null) return;
                try
                {
                    if (text == "\n") text = "\r\n";
                    else if (text == "\r") text = "\r\n";
                    _process.StandardInput.Write(text);
                    _process.StandardInput.Flush();
                }
                catch { }
            }
        }

        public void SendBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            if (_usingConPTY)
            {
                try { _conpty?.Write(data); } catch { }
                return;
            }
            try
            {
                var text = Encoding.Default.GetString(data);
                SendInput(text);
            }
            catch
            {
                try { SendInput(Encoding.UTF8.GetString(data)); } catch { }
            }
        }

        public void Resize(int columns, int rows)
        {
            _cols = Math.Max(1, columns);
            _rows = Math.Max(1, rows);
            if (_usingConPTY) { try { _conpty?.Resize((short)_cols, (short)_rows); } catch { } }
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

        public string GetSelection() { return string.Empty; }

        public void Disconnect()
        {
            lock (_lock)
            {
                if (_usingConPTY)
                {
                    try { _conpty?.Stop(); } catch { }
                    try { _conpty?.Dispose(); } catch { }
                    _conpty = null;
                    return;
                }
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
                        if (!_process.WaitForExit(2000))
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

        private void OnProcessExited(object sender, EventArgs e)
        {
            RaiseOutput("\r\n[本地终端已退出]\r\n");
            RaiseDisconnected();
        }

        private void RaiseDisconnected()
        {
            if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
                return;
            try { Disconnected?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void CleanupProcess()
        {
            try { if (_readCts != null) _readCts.Cancel(); } catch { }
            try { if (_readCts != null) _readCts.Dispose(); } catch { }
            _readCts = null;
            if (_process == null) return;
            try
            {
                _process.Exited -= OnProcessExited;
                _process.Dispose();
            }
            catch { }
            _process = null;
        }
    }
}
