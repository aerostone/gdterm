using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gdterm.Terminal.Interop
{
    /// <summary>
    /// winpty.dll P/Invoke 封装：Win7/Server2008 没有 ConPTY 时的真 PTY 方案。
    /// winpty (MIT, rprichard) 通过启动 winpty-agent.exe 持有一个隐藏 console，
    /// 把 Windows console API 桥接成 ANSI 转义流。我们 P/Invoke winpty.dll 的 C API，
    /// 自己用命名管道收发字节，对外接口与 ConPTY 一致。
    ///
    /// 二进制来自 MSYS2 winpty 0.4.3-3，已 vendor 进 lib/winpty/，启动时复制到 bin 旁。
    /// DllImport("winpty.dll") 若找不到 dll（lib 未随包发布）会抛 DllNotFoundException，
    /// LocalTerminalSession.TryStartWinPty 捕获后回退到重定向 Process。
    /// </summary>
    internal sealed class WinPty : IDisposable
    {
        // ===== winpty.dll C API =====
        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int winpty_error_code(IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern string winpty_error_msg(IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void winpty_error_free(IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr winpty_config_new(ulong agentFlags, out IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void winpty_config_free(IntPtr cfg);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void winpty_config_set_initial_size(IntPtr cfg, int cols, int rows);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr winpty_open(IntPtr cfg, out IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern string winpty_conin_name(IntPtr wp);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern string winpty_conout_name(IntPtr wp);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern string winpty_conerr_name(IntPtr wp);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr winpty_spawn_config_new(ulong spawnFlags,
            IntPtr exe, IntPtr cmdline, IntPtr cwd, IntPtr env, out IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void winpty_spawn_config_free(IntPtr cfg);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool winpty_spawn(IntPtr wp, IntPtr cfg,
            out IntPtr processHandle, out IntPtr threadHandle, out uint dwProcessId,
            out IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool winpty_set_size(IntPtr wp, int cols, int rows, out IntPtr err);

        [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void winpty_free(IntPtr wp);

        // ===== Win32 用于打开命名管道 =====
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        // 常量
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 258;

        private const ulong WINPTY_FLAG_COLOR_ESCAPES = 0x4;
        private const ulong WINPTY_SPAWN_FLAG_AUTO_SHUTDOWN = 0x1;

        /// <summary>winpty.dll 是否能成功加载（DllNotFoundException 时为 false）。</summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    IntPtr err;
                    IntPtr cfg = winpty_config_new(0, out err);
                    if (cfg != IntPtr.Zero)
                    {
                        winpty_config_free(cfg);
                        return true;
                    }
                    if (err != IntPtr.Zero) winpty_error_free(err);
                    return false;
                }
                catch
                {
                    // DllNotFoundException / EntryPointNotFoundException — dll 不在搜索路径
                    return false;
                }
            }
        }

        // ===== 实例状态 =====
        private IntPtr _wp = IntPtr.Zero;
        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _conin = IntPtr.Zero;   // 写入端
        private IntPtr _conout = IntPtr.Zero;  // 读取端
        private Thread _readThread;
        private volatile bool _running;
        private CancellationTokenSource _cts;

        public event Action<byte[]> OnOutput;
        public event EventHandler OnExited;

        /// <summary>启动 winpty + 子进程。失败时清理已分配资源并返回 false。</summary>
        public bool Start(string commandLine, string workingDirectory, short cols, short rows)
        {
            if (!IsAvailable) return false;
            if (cols <= 0) cols = 80;
            if (rows <= 0) rows = 24;

            IntPtr cfg = IntPtr.Zero, spawnCfg = IntPtr.Zero;
            IntPtr err = IntPtr.Zero;
            try
            {
                cfg = winpty_config_new(WINPTY_FLAG_COLOR_ESCAPES, out err);
                if (cfg == IntPtr.Zero) throw NewWinPtyException(err, "winpty_config_new");
                winpty_config_set_initial_size(cfg, cols, rows);

                _wp = winpty_open(cfg, out err);
                if (_wp == IntPtr.Zero) throw NewWinPtyException(err, "winpty_open");

                // 打开命名管道
                string coninName = winpty_conin_name(_wp);
                string conoutName = winpty_conout_name(_wp);
                _conin = CreateFileW(coninName, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (_conin == (IntPtr)(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "打开 conin 失败");
                _conout = CreateFileW(conoutName, GENERIC_READ, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (_conout == (IntPtr)(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "打开 conout 失败");

                // 配置 spawn
                IntPtr exePtr = Marshal.StringToHGlobalUni(commandLine);
                IntPtr cmdPtr = Marshal.StringToHGlobalUni(commandLine);
                IntPtr cwdPtr = string.IsNullOrEmpty(workingDirectory)
                    ? IntPtr.Zero
                    : Marshal.StringToHGlobalUni(workingDirectory);
                try
                {
                    spawnCfg = winpty_spawn_config_new(
                        WINPTY_SPAWN_FLAG_AUTO_SHUTDOWN,
                        IntPtr.Zero, cmdPtr, cwdPtr, IntPtr.Zero, out err);
                    if (spawnCfg == IntPtr.Zero) throw NewWinPtyException(err, "winpty_spawn_config_new");

                    IntPtr processHandle, threadHandle;
                    uint pid;
                    if (!winpty_spawn(_wp, spawnCfg, out processHandle, out threadHandle, out pid, out err))
                        throw NewWinPtyException(err, "winpty_spawn");

                    _hProcess = processHandle;
                    // threadHandle 立即关掉
                    if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
                }
                finally
                {
                    Marshal.FreeHGlobal(exePtr);
                    Marshal.FreeHGlobal(cmdPtr);
                    if (cwdPtr != IntPtr.Zero) Marshal.FreeHGlobal(cwdPtr);
                }

                // 读循环
                _cts = new CancellationTokenSource();
                _running = true;
                _readThread = new Thread(() => ReadLoop(_cts.Token)) { IsBackground = true, Name = "winpty-read" };
                _readThread.Start();

                return true;
            }
            catch (Exception)
            {
                if (err != IntPtr.Zero) winpty_error_free(err);
                Cleanup();
                return false;
            }
            finally
            {
                if (cfg != IntPtr.Zero) winpty_config_free(cfg);
                if (spawnCfg != IntPtr.Zero) winpty_spawn_config_free(spawnCfg);
            }
        }

        private void ReadLoop(CancellationToken token)
        {
            var buf = new byte[4096];
            try
            {
                while (_running && !token.IsCancellationRequested && _conout != IntPtr.Zero)
                {
                    uint read;
                    bool ok = ReadFile(_conout, buf, (uint)buf.Length, out read, IntPtr.Zero);
                    if (!ok || read == 0) break;
                    var chunk = new byte[read];
                    Array.Copy(buf, chunk, read);
                    try { OnOutput?.Invoke(chunk); } catch { }
                }
            }
            catch { }
            finally
            {
                try { OnExited?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        public bool Write(byte[] data)
        {
            if (data == null || data.Length == 0 || _conin == IntPtr.Zero || _conin == (IntPtr)(-1)) return false;
            uint written;
            return WriteFile(_conin, data, (uint)data.Length, out written, IntPtr.Zero) && written == data.Length;
        }

        public bool Resize(short cols, short rows)
        {
            if (_wp == IntPtr.Zero) return false;
            if (cols <= 0 || rows <= 0) return false;
            try
            {
                IntPtr err;
                bool ok = winpty_set_size(_wp, cols, rows, out err);
                if (err != IntPtr.Zero) winpty_error_free(err);
                return ok;
            }
            catch { return false; }
        }

        public bool IsRunning
        {
            get
            {
                if (_hProcess == IntPtr.Zero) return false;
                return WaitForSingleObject(_hProcess, 0) == WAIT_TIMEOUT;
            }
        }

        public void Stop()
        {
            if (_hProcess == IntPtr.Zero) return;
            try
            {
                if (IsRunning)
                {
                    // 先尝试温柔退出，再 kill
                    try { Write(System.Text.Encoding.ASCII.GetBytes("exit\r\n")); } catch { }
                    if (WaitForSingleObject(_hProcess, 2000) != WAIT_OBJECT_0)
                    {
                        try { TerminateProcess(_hProcess, 1); } catch { }
                    }
                }
            }
            catch { }
        }

        private static Exception NewWinPtyException(IntPtr err, string where)
        {
            if (err == IntPtr.Zero) return new Win32Exception(where + " 失败");
            int code = winpty_error_code(err);
            string msg;
            try { msg = winpty_error_msg(err); } catch { msg = null; }
            winpty_error_free(err);
            return new Win32Exception(code, where + " 失败: " + (msg ?? ("code=" + code)));
        }

        private void Cleanup()
        {
            _running = false;
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_conin != IntPtr.Zero && _conin != (IntPtr)(-1)) CloseHandle(_conin); } catch { }
            _conin = IntPtr.Zero;
            try { if (_conout != IntPtr.Zero && _conout != (IntPtr)(-1)) CloseHandle(_conout); } catch { }
            _conout = IntPtr.Zero;
            try { if (_hProcess != IntPtr.Zero) CloseHandle(_hProcess); } catch { }
            _hProcess = IntPtr.Zero;
            try { if (_wp != IntPtr.Zero) winpty_free(_wp); } catch { }
            _wp = IntPtr.Zero;
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
