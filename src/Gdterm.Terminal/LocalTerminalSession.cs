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
    /// 本地终端：三层后端，按优先级回退。
    /// 1) ConPTY — Win10 1809+ 自带，真 PTY，最佳体验
    /// 2) winpty — Win7/Server2008 上唯一真 PTY；依赖 lib/winpty/winpty.dll + winpty-agent.exe
    /// 3) 重定向 Process — 兜底，无交互式 prompt，仅作为最后保险
    /// 只有真 PTY 后端（ConPTY/winpty）才视为 VT 可用。
    /// </summary>
    public class LocalTerminalSession : ITerminalSession
    {
        private enum LocalBackend { None, ConPty, WinPty, Redirect }

        private Process _process;
        private readonly object _lock = new object();
        private readonly List<string> _outputBuffer = new List<string>();
        private string _shellPath;
        private string _workingDirectory;
        private bool _disposed;
        private CancellationTokenSource _readCts;
        private const int MaxBufferLines = 500;

        private ConPTY _conpty;
        private WinPty _winpty;
        private LocalBackend _backend = LocalBackend.None;
        private int _cols = 80, _rows = 24;

        /// <summary>本会话是否走真 PTY（VtCell/TUI 真彩可坐）。</summary>
        public bool IsVtCapable => _backend == LocalBackend.ConPty || _backend == LocalBackend.WinPty;
        /// <summary>本会话使用的后端名（诊断用）。</summary>
        public string BackendName => _backend.ToString();
        /// <summary>当前 OS 是否支持 ConPTY（Win10 1809+）。</summary>
        public static bool IsConPTYAvailableOnThisOS() => ConPTY.IsAvailable;
        /// <summary>当前进程是否成功加载 winpty.dll（lib/winpty/ 已随包发布且 OS 兼容）。</summary>
        public static bool IsWinPtyAvailableOnThisOS() => WinPty.IsAvailable;
        /// <summary>UI 选 Renderer 的依据：任何真 PTY 后端可用即可走 VtCell。</summary>
        public static bool IsAnyPtyAvailableOnThisOS() => ConPTY.IsAvailable || WinPty.IsAvailable;

        public string ConnectionId { get; private set; }
        public string Hostname { get { return "localhost"; } }
        public string OsType { get; private set; }
        public bool IsConnected
        {
            get
            {
                try
                {
                    switch (_backend)
                    {
                        case LocalBackend.ConPty: return _conpty != null && _conpty.IsRunning;
                        case LocalBackend.WinPty: return _winpty != null && _winpty.IsRunning;
                        case LocalBackend.Redirect: return _process != null && !_process.HasExited;
                        default: return false;
                    }
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

                if (ConPTY.IsAvailable && TryStartConPTY())
                {
                    _backend = LocalBackend.ConPty;
                    RaiseOutput("\r\n[gdterm] 本地终端已启动 (ConPTY): " + _shellPath + "\r\n");
                    return;
                }

                if (WinPty.IsAvailable && TryStartWinPty())
                {
                    _backend = LocalBackend.WinPty;
                    RaiseOutput("\r\n[gdterm] 本地终端已启动 (winpty): " + _shellPath + "\r\n");
                    return;
                }

                _backend = LocalBackend.Redirect;
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

        // ===== ConPTY 路径 =====
        private bool TryStartConPTY()
        {
            try
            {
                string cwd = ResolveCwd();
                _conpty = new ConPTY();
                _conpty.OnOutput += OnPtyOutput;
                _conpty.OnExited += OnPtyExited;
                return _conpty.Start(BuildCommandLineForPty(), cwd, (short)_cols, (short)_rows);
            }
            catch
            {
                try { _conpty?.Dispose(); } catch { }
                _conpty = null;
                return false;
            }
        }

        // ===== winpty 路径 =====
        private bool TryStartWinPty()
        {
            try
            {
                string cwd = ResolveCwd();
                _winpty = new WinPty();
                _winpty.OnOutput += OnPtyOutput;
                _winpty.OnExited += OnPtyExited;
                return _winpty.Start(BuildCommandLineForPty(), cwd, (short)_cols, (short)_rows);
            }
            catch
            {
                try { _winpty?.Dispose(); } catch { }
                _winpty = null;
                return false;
            }
        }

        private void OnPtyOutput(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            string text;
            try { text = Encoding.UTF8.GetString(bytes); }
            catch { text = Encoding.Default.GetString(bytes); }
            RaiseOutput(text);
        }

        private void OnPtyExited(object sender, EventArgs e)
        {
            RaiseOutput("\r\n[本地终端已退出]\r\n");
            RaiseDisconnected();
        }

        private string ResolveCwd()
        {
            return !string.IsNullOrEmpty(_workingDirectory)
                ? _workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private string BuildCommandLineForPty()
        {
            // PTY 不需要重定向参数；shell 自己启动交互式。
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

        // ===== 重定向回退 =====
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
            startInfo.WorkingDirectory = ResolveCwd();

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

                RaiseOutput("\r\n[gdterm] 本地终端已启动 (重定向): " + _shellPath + "\r\n");
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
                if (text == "\n") text = "\r\n";
                else if (text == "\r") text = "\r\n";
                switch (_backend)
                {
                    case LocalBackend.ConPty:
                        try { _conpty?.Write(Encoding.UTF8.GetBytes(text)); } catch { }
                        return;
                    case LocalBackend.WinPty:
                        try { _winpty?.Write(Encoding.UTF8.GetBytes(text)); } catch { }
                        return;
                    case LocalBackend.Redirect:
                        try { _process?.StandardInput.Write(text); _process?.StandardInput.Flush(); } catch { }
                        return;
                }
            }
        }

        public void SendBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            switch (_backend)
            {
                case LocalBackend.ConPty:
                    try { _conpty?.Write(data); } catch { }
                    return;
                case LocalBackend.WinPty:
                    try { _winpty?.Write(data); } catch { }
                    return;
                case LocalBackend.Redirect:
                    try { SendInput(Encoding.Default.GetString(data)); }
                    catch { try { SendInput(Encoding.UTF8.GetString(data)); } catch { } }
                    return;
            }
        }

        public void Resize(int columns, int rows)
        {
            _cols = Math.Max(1, columns);
            _rows = Math.Max(1, rows);
            switch (_backend)
            {
                case LocalBackend.ConPty:
                    try { _conpty?.Resize((short)_cols, (short)_rows); } catch { }
                    return;
                case LocalBackend.WinPty:
                    try { _winpty?.Resize((short)_cols, (short)_rows); } catch { }
                    return;
            }
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
                switch (_backend)
                {
                    case LocalBackend.ConPty:
                        try { _conpty?.Stop(); } catch { }
                        try { _conpty?.Dispose(); } catch { }
                        _conpty = null;
                        return;
                    case LocalBackend.WinPty:
                        try { _winpty?.Stop(); } catch { }
                        try { _winpty?.Dispose(); } catch { }
                        _winpty = null;
                        return;
                    case LocalBackend.Redirect:
                        if (_process == null) return;
                        try
                        {
                            if (!_process.HasExited)
                            {
                                try { _process.StandardInput.Write("exit\r\n"); _process.StandardInput.Flush(); }
                                catch { }
                                if (!_process.WaitForExit(2000))
                                {
                                    try { _process.Kill(); } catch { }
                                }
                            }
                        }
                        catch { }
                        CleanupProcess();
                        return;
                }
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
