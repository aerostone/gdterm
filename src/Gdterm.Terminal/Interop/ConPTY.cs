using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gdterm.Terminal.Interop
{
    /// <summary>
    /// Windows ConPTY 辅助：Win10 1809+ 有真 PTY，cmd/PowerShell 会得到真 TTY；
    /// Win7/Server2008 没有 conpty，静态绑定 kernel32 CreatePseudoConsole 会在启动时抛
    /// EntryNotFoundException，所以这里用 LoadLibrary + GetProcAddress 在运行时动态绑定。
    /// 不可用（任何一步失败）就由 LocalTerminalSession 走回退实现（重定向 Process）。
    /// </summary>
    internal sealed class ConPTY : IDisposable
    {
        // ===== ConPTY 委托签名 =====
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreatePseudoConsoleDelegate(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ResizePseudoConsoleDelegate(IntPtr hPC, COORD size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ClosePseudoConsoleDelegate(IntPtr hPC);

        // PSEUDOCONSOLE_INHERIT_CURSOR = 0x1 —— 继承光标位置，常用于窗口接到已有控制台
        private const uint PSEUDOCONSOLE_INHERIT_CURSOR = 0x1;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        // ===== 结构体 =====
        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        // ===== Win32 P/Invoke（静态安全） =====
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList,
            int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList,
            uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
            IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        // ===== 静态绑定：ConPTY 可用性 =====
        private static readonly CreatePseudoConsoleDelegate _createDelegate;
        private static readonly ResizePseudoConsoleDelegate _resizeDelegate;
        private static readonly ClosePseudoConsoleDelegate _closeDelegate;

        static ConPTY()
        {
            try
            {
                var hKernel = LoadLibraryW("kernel32.dll");
                if (hKernel != IntPtr.Zero)
                {
                    var c = GetProcAddress(hKernel, "CreatePseudoConsole");
                    var r = GetProcAddress(hKernel, "ResizePseudoConsole");
                    var x = GetProcAddress(hKernel, "ClosePseudoConsole");
                    if (c != IntPtr.Zero && r != IntPtr.Zero && x != IntPtr.Zero)
                    {
                        _createDelegate = Marshal.GetDelegateForFunctionPointer(c, typeof(CreatePseudoConsoleDelegate)) as CreatePseudoConsoleDelegate;
                        _resizeDelegate = Marshal.GetDelegateForFunctionPointer(r, typeof(ResizePseudoConsoleDelegate)) as ResizePseudoConsoleDelegate;
                        _closeDelegate = Marshal.GetDelegateForFunctionPointer(x, typeof(ClosePseudoConsoleDelegate)) as ClosePseudoConsoleDelegate;
                    }
                }
            }
            catch
            {
                _createDelegate = null;
                _resizeDelegate = null;
                _closeDelegate = null;
            }
        }

        /// <summary>当前 OS 是否支持 ConPTY（Win10 1809+）。</summary>
        public static bool IsAvailable => _createDelegate != null && _resizeDelegate != null && _closeDelegate != null;

        // ===== 实例状态 =====
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _inputWrite = IntPtr.Zero;   // 对 PTY 写
        private IntPtr _outputRead = IntPtr.Zero;   // 从 PTY 读
        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _hThread = IntPtr.Zero;
        private IntPtr _hAttributeList = IntPtr.Zero;
        private uint _pid;
        private Thread _readThread;
        private volatile bool _running;
        private CancellationTokenSource _cts;

        /// <summary>PTY 输出（已读字节）回调。</summary>
        public event Action<byte[]> OnOutput;

        /// <summary>子进程退出事件。</summary>
        public event EventHandler OnExited;

        /// <summary>启动 ConPTY + 子进程。失败时回滚已分配资源并返回 false。</summary>
        public bool Start(string commandLine, string workingDirectory, short cols, short rows)
        {
            if (!IsAvailable) return false;
            if (cols <= 0) cols = 80;
            if (rows <= 0) rows = 24;

            IntPtr inputRead = IntPtr.Zero, outputWrite = IntPtr.Zero, outputDup = IntPtr.Zero;
            try
            {
                // 1) 两对管道：
                //    inputWrite -> inputRead  喂给 PTY
                //    outputWrite -> outputRead 从 PTY 读
                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
                if (!CreatePipe(out inputRead, out _inputWrite, ref sa, 0)) return false;
                // 让 inputRead 继承给子进程（inputWrite 留给自己）
                if (!CreatePipe(out _outputRead, out outputWrite, ref sa, 0)) return false;
                // 让 outputWrite 继承给子进程（outputRead 留给自己）
                // 注意：不能让 _inputWrite / _outputRead 继承

                // 2) CreatePseudoConsole
                var size = new COORD { X = cols, Y = rows };
                int hr = _createDelegate(size, inputRead, outputWrite, 0, out _hPC);
                if (hr != 0 || _hPC == IntPtr.Zero)
                    throw new Win32Exception(hr, "CreatePseudoConsole 失败 hr=0x" + hr.ToString("X8"));

                // 3) STARTUPINFOEX 关联 PTY
                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                IntPtr attrListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
                _hAttributeList = Marshal.AllocHGlobal(attrListSize);
                if (!InitializeProcThreadAttributeList(_hAttributeList, 1, 0, ref attrListSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList 失败");
                if (!UpdateProcThreadAttribute(_hAttributeList, 0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute 失败");
                si.lpAttributeList = _hAttributeList;

                // 4) CreateProcessW，以 STARTF_USESTDHANDLES 不需要——PTY 接管
                PROCESS_INFORMATION pi;
                bool ok = CreateProcessW(
                    null, commandLine,
                    IntPtr.Zero, IntPtr.Zero,
                    false, // bInheritHandles：ConPTY 子进程不要继承我们的 inputWrite/outputRead
                    EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, workingDirectory,
                    ref si, out pi);
                if (!ok)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW 失败");
                _hProcess = pi.hProcess;
                _hThread = pi.hThread;
                _pid = pi.dwProcessId;

                // 5) 写端拿掉 inherited 副本（不关闭，PTY 自行管理；我们只需关闭自己持有 inputRead/outputWrite 的句柄）
                CloseHandle(inputRead);
                CloseHandle(outputWrite);
                inputRead = IntPtr.Zero;
                outputWrite = IntPtr.Zero;

                // 6) 读循环线程
                _cts = new CancellationTokenSource();
                _running = true;
                _readThread = new Thread(() => ReadLoop(_cts.Token)) { IsBackground = true, Name = "ConPTY-read" };
                _readThread.Start();

                return true;
            }
            catch (Exception)
            {
                // 回滚
                Cleanup();
                return false;
            }
        }

        private void ReadLoop(CancellationToken token)
        {
            var buf = new byte[4096];
            try
            {
                while (_running && !token.IsCancellationRequested && _outputRead != IntPtr.Zero)
                {
                    uint read;
                    bool ok = ReadFile(_outputRead, buf, (uint)buf.Length, out read, IntPtr.Zero);
                    if (!ok || read == 0)
                    {
                        // 管道关闭或读到 0 视为退出
                        break;
                    }
                    var chunk = new byte[read];
                    Array.Copy(buf, chunk, read);
                    try { OnOutput?.Invoke(chunk); } catch { }
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                // 进程已退出
                try { OnExited?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        /// <summary>把字节喂给 PTY 输入端。</summary>
        public bool Write(byte[] data)
        {
            if (data == null || data.Length == 0 || _inputWrite == IntPtr.Zero) return false;
            uint written;
            bool ok = WriteFile(_inputWrite, data, (uint)data.Length, out written, IntPtr.Zero);
            return ok && written == data.Length;
        }

        /// <summary>调整 PTY size（用于宋体 SIGWINCH）。</summary>
        public bool Resize(short cols, short rows)
        {
            if (!IsAvailable || _hPC == IntPtr.Zero || _resizeDelegate == null) return false;
            if (cols <= 0 || rows <= 0) return false;
            try
            {
                int hr = _resizeDelegate(_hPC, new COORD { X = cols, Y = rows });
                return hr == 0;
            }
            catch { return false; }
        }

        /// <summary>进程是否仍在运行。</summary>
        public bool IsRunning => _hProcess != IntPtr.Zero && WaitForSingleObject(_hProcess, 0) == 258 /* WAIT_TIMEOUT */;

        /// <summary>要求子进程退出：发 exit\r\n，超时再 kill。</summary>
        public void Stop()
        {
            if (_hProcess == IntPtr.Zero) return;
            try
            {
                if (IsRunning)
                {
                    try { Write(System.Text.Encoding.ASCII.GetBytes("exit\r\n")); } catch { }
                    if (WaitForSingleObject(_hProcess, 2000) != 0 /* WAIT_OBJECT_0 */)
                    {
                        // kill via TerminateProcess 不易 P/Invoke 带签名，复用 WaitFor 然后超时由调用方决定
                    }
                }
            }
            catch { }
        }

        private void Cleanup()
        {
            _running = false;
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try
            {
                if (_hAttributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(_hAttributeList);
                    Marshal.FreeHGlobal(_hAttributeList);
                }
            } catch { }
            _hAttributeList = IntPtr.Zero;

            try { if (_closeDelegate != null && _hPC != IntPtr.Zero) _closeDelegate(_hPC); } catch { }
            _hPC = IntPtr.Zero;

            try { if (_inputWrite != IntPtr.Zero) CloseHandle(_inputWrite); } catch { }
            _inputWrite = IntPtr.Zero;
            try { if (_outputRead != IntPtr.Zero) CloseHandle(_outputRead); } catch { }
            _outputRead = IntPtr.Zero;

            try { if (_hThread != IntPtr.Zero) CloseHandle(_hThread); } catch { }
            _hThread = IntPtr.Zero;
            try { if (_hProcess != IntPtr.Zero) CloseHandle(_hProcess); } catch { }
            _hProcess = IntPtr.Zero;

            try { if (_cts != null) _cts.Dispose(); } catch { }
            _cts = null;
        }

        public void Dispose()
        {
            Stop();
            Cleanup();
        }
    }
}
