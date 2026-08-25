using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
// 双命名空间均有 Timer：显式绑定到线程定时器，WinForms 定时器用嵌套别名
using Timer = System.Threading.Timer;
using Gdterm.Core.Models;
using Gdterm.Rdp.Models;

namespace Gdterm.Rdp
{
    /// <summary>
    /// 基于 FreeRDP（wfreerdp.exe 2.x）的 RDP 客户端——进程嵌入实现。
    ///
    /// 相比 mstscax ActiveX（RdpClient）：
    ///   1) 许可证存储为用户目录下的文件，完全不碰 HKLM\SOFTWARE\Microsoft\MSLicensing，
    ///      彻底规避 reason=2056/ext=267「许可存储创建被拒绝」的提权问题；
    ///   2) 无 COM 注册 / CLSID / 进程位数依赖；
    ///   3) 断开原因来自进程退出码（ERRCONNECT_*）与 stderr 日志。
    ///
    /// 嵌入方式：wfreerdp /parent-window:&lt;面板 HWND&gt; 把远程画面渲染进我们的 Panel。
    /// 键盘输入依赖 FreeRDP ≥ 2.7（PR #7790 修复 parent-window 模式键盘事件）。
    /// 注意：凭据经命令行传给子进程（同桌面会话内可见），KeePass CredWrite 通道继续保留。
    /// </summary>
    public sealed class FreeRdpClient : IRdpClient
    {
        private readonly UserControl _container;
        private readonly Panel _surface;
        private readonly WinFormsTimer _resizeDebounce;

        private Process _proc;
        private volatile bool _userInitiatedDisconnect;
        private bool _disposed;
        private int _connectedRaised;
        private int _disconnectedRaised;
        private int _outLines;
        private int _errLines;
        private Timer _connectedTimer;

        private const int MaxLoggedLines = 120;
        private const int ConnectedProbeDelayMs = 2500;

        public event EventHandler<RdpStateChangedEventArgs> StateChanged;
        public event EventHandler<FileTransferEventArgs> FileTransferred; // 驱动器重定向由系统完成，无逐文件事件

        public FreeRdpClient()
        {
            _container = new UserControl { Dock = DockStyle.Fill };
            _surface = new Panel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            _surface.Resize += OnSurfaceResize;
            _container.Controls.Add(_surface);
            _resizeDebounce = new WinFormsTimer { Interval = 300 };
            _resizeDebounce.Tick += OnResizeDebounceTick;
        }

        public bool IsConnected
        {
            get
            {
                var p = _proc;
                if (p == null) return false;
                try { return !p.HasExited; }
                catch { return false; }
            }
        }

        public UserControl Control => _container;

        public RdpOptions CurrentOptions { get; private set; }

        public void Connect(ConnectionConfig config, CredentialPayload credential, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            Start(config.Host, config.Port > 0 ? config.Port : 3389, credential, options,
                credential != null ? credential.Username : config.Username, config.Domain);
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (tunnelEndpoint == null) throw new ArgumentNullException("tunnelEndpoint");
            Start(tunnelEndpoint.LocalHost ?? "127.0.0.1", tunnelEndpoint.LocalPort > 0 ? tunnelEndpoint.LocalPort : 3389,
                credential, options,
                credential != null ? credential.Username : config.Username, config.Domain);
        }

        public void Disconnect()
        {
            if (_proc == null) return;
            _userInitiatedDisconnect = true;
            try
            {
                if (_proc.HasExited) return;
                // 先尝试优雅关闭（WM_CLOSE 给 wfreerdp 的子窗口），超时再强杀
                var child = FindFreeRdpChildWindow();
                if (child != IntPtr.Zero)
                    PostMessage(child, WmClose, IntPtr.Zero, IntPtr.Zero);
                if (!_proc.WaitForExit(2500))
                {
                    RdpLog.Info("FreeRdp.Disconnect", "graceful timeout, killing pid=" + _proc.Id);
                    try { _proc.Kill(); } catch { }
                }
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("FreeRdp.Disconnect", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Disconnect(); } catch { }
            DisposeTimer(ref _connectedTimer);
            try
            {
                var p = _proc;
                if (p != null && !p.HasExited)
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
            _resizeDebounce?.Dispose();
            try { _container?.Dispose(); } catch { }
        }

        // ===== 引擎探测 =====

        /// <summary>wfreerdp.exe 是否可用（决定工厂选 FreeRDP 还是回退 mstscax）。</summary>
        public static bool IsAvailable() => FindExecutable() != null;

        /// <summary>
        /// 启动前依赖预检（PE 导入表）：解析 wfreerdp.exe 实际导入的 DLL，逐一确认
        /// Windows 加载器能找到（exe 目录 / PATH / System32 任一）。静态构建无
        /// freerdp/winpr DLL 属正常——只检查导入表里真实列出的依赖。
        /// 返回第一个缺失的 DLL 文件名；全部就位或探测异常时返回 null（交由进程退出码兑底）。
        /// </summary>
        public static string FindMissingRuntimeDll(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
                var dir = Path.GetDirectoryName(exePath);
                var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                foreach (var dll in ReadImportTable(exePath))
                {
                    // API Set 虚拟名由加载器内部解析，磁盘上无对应文件
                    if (dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) ||
                        dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (HasFile(dir, dll) || ExistsOnPath(dll) || HasFile(sysDir, dll)) continue;
                    return dll;
                }
            }
            catch { return null; }
            return null;
        }

        /// <summary>解析 PE32/PE32+ 导入表中的 DLL 文件名（不含延迟导入）。</summary>
        private static IEnumerable<string> ReadImportTable(string exePath)
        {
            using (var fs = File.OpenRead(exePath))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadUInt16() != 0x5A4D) yield break;                  // 'MZ'
                fs.Position = 0x3C;
                int pe = br.ReadInt32();
                fs.Position = pe;
                if (br.ReadUInt32() != 0x00004550) yield break;              // 'PE\0\0'
                br.ReadUInt16();                                             // Machine
                ushort sections = br.ReadUInt16();                           // NumberOfSections
                fs.Position += 12;                                           // TimeDateStamp/SymTable/SymCount
                ushort optSize = br.ReadUInt16();                            // SizeOfOptionalHeader
                fs.Position += 2;                                            // Characteristics
                long optStart = fs.Position;
                int ddOffset = br.ReadUInt16() == 0x20B ? 112 : 96;          // Magic: PE32+ 为 112
                if (optSize < ddOffset + 16) yield break;
                fs.Position = optStart + ddOffset + 8;                       // DataDirectory[1]=Import Table
                int importRva = br.ReadInt32();

                // 节表 → RVA→文件偏移映射
                var secVaddr = new int[sections];
                var secVsize = new int[sections];
                var secRaw = new long[sections];
                fs.Position = optStart + optSize;
                for (int i = 0; i < sections; i++)
                {
                    fs.Position += 8;                                        // Name
                    secVsize[i] = br.ReadInt32();
                    secVaddr[i] = br.ReadInt32();
                    br.ReadInt32();                                          // SizeOfRawData
                    secRaw[i] = br.ReadInt32();                              // PointerToRawData
                    fs.Position += 16;
                }

                long Off(int rva)
                {
                    for (int i = 0; i < sections; i++)
                        if (rva >= secVaddr[i] && rva < secVaddr[i] + Math.Max(secVsize[i], 1))
                            return rva - secVaddr[i] + secRaw[i];
                    return -1L;
                }

                long desc = Off(importRva);
                if (desc < 0) yield break;
                for (int i = 0; i < 4096; i++)                               // 上限防损坏文件死循环
                {
                    fs.Position = desc + i * 20L + 12;                       // IMAGE_IMPORT_DESCRIPTOR.Name
                    int nameRva = br.ReadInt32();
                    if (nameRva == 0) yield break;                           // 全零终止项
                    long nameOff = Off(nameRva);
                    if (nameOff < 0) continue;
                    fs.Position = nameOff;
                    var sb = new System.Text.StringBuilder();
                    for (int k = 0; k < 256; k++)
                    {
                        byte b = br.ReadByte();
                        if (b == 0) break;
                        sb.Append((char)b);
                    }
                    if (sb.Length > 0) yield return sb.ToString();
                }
            }
        }

        private static bool HasFile(string dir, string fileName)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                foreach (var _ in Directory.EnumerateFiles(dir, fileName)) return true;
            }
            catch { }
            return false;
        }

        private static bool ExistsOnPath(string pattern)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return false;
            foreach (var d in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try { if (HasFile(d.Trim(), pattern)) return true; } catch { }
            }
            return false;
        }

        /// <summary>探测 wfreerdp.exe 路径；绿色包在 freerdp\，源码运行在 lib\freerdp\。</summary>
        public static string FindExecutable()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "freerdp", "wfreerdp.exe"),
                Path.Combine(baseDir, "lib", "freerdp", "wfreerdp.exe")
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        // ===== 启动 =====

        private void Start(string host, int port, CredentialPayload credential, RdpOptions options, string username, string domain)
        {
            var exe = FindExecutable();
            if (exe == null)
                throw new InvalidOperationException(
                    "未找到 RDP 引擎 wfreerdp.exe。发行包 freerdp\\ 目录应自带该引擎"
                    + "（官方已停发 2.x Windows 二进制，由本项目 CI 从源码构建打包，请勿单独下载 3.x 替换——"
                    + "3.x 已移除 /parent-window 嵌入）。请用完整发行包重新解压；"
                    + "或在连接元数据设置 rdp_engine=mstscax 改用 ActiveX。");

            // 启动前依赖预检：缺 DLL 时 Windows 会弹「找不到 xxx.dll」错误框且进程假活，
            // 导致误报 connected 后再掉线（退出码 0xC0000135）。快速失败并给出可操作提示。
            var missingDll = FindMissingRuntimeDll(exe);
            if (missingDll != null)
            {
                RdpLog.Info("FreeRdp.Precheck", "missing dll=" + missingDll + " exe=" + exe);
                throw new InvalidOperationException(
                    "FreeRDP 运行库不完整：缺少 " + missingDll
                    + "（目录：" + Path.GetDirectoryName(exe) + "）。"
                    + "正常情况下发行包已自带全部运行库——请重新解压完整发行包覆盖；若被杀毒软件隔离请恢复并加白名单。");
            }
            try { RdpLog.Info("FreeRdp.Precheck", "ok exe=" + exe); } catch { }

            CurrentOptions = options ?? new RdpOptions();

            // 面板句柄必须先创建（PendingConnect 在 tab 可见后触发，通常已就绪）
            if (!_surface.IsHandleCreated)
            {
                // 读取 Handle 属性会强制创建句柄（PendingConnect 在 tab 可见后触发，通常已就绪）
                IntPtr forced = _surface.Handle;
                RdpLog.Info("FreeRdp.Start", "forced handle creation hwnd=" + forced);
            }
            int w = _surface.ClientSize.Width;
            int h = _surface.ClientSize.Height;

            var args = new List<string>();
            var logArgs = new List<string>();
            AddArg(args, logArgs, "/v:" + host + ":" + port);
            if (w >= 200 && h >= 200)
                AddArg(args, logArgs, "/size:" + w + "x" + h);
            AddArg(args, logArgs, "/bpp:" + MapBpp(CurrentOptions.ColorDepth));
            AddArg(args, logArgs, "/cert-ignore"); // 免交互证书确认（隐藏控制台下 TOFU 提示会挂起）
            if (!string.IsNullOrEmpty(username)) AddArg(args, logArgs, "/u:" + Q(username));
            if (!string.IsNullOrEmpty(credential != null ? credential.Password : null))
            {
                args.Add("/p:" + Q(credential.Password));
                logArgs.Add("/p:***");
            }
            if (!string.IsNullOrEmpty(domain)) AddArg(args, logArgs, "/d:" + Q(domain));
            if (CurrentOptions.RedirectClipboard) AddArg(args, logArgs, "/clipboard");
            if (CurrentOptions.RedirectDrives)
            {
                // 文件传输通道：远端出现 \\tsclient\gdterm 网络盘，双向复制粘贴
                var shareRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                AddArg(args, logArgs, "/drive:gdterm," + Q(shareRoot));
            }
            if (CurrentOptions.AudioMode == AudioRedirectionMode.Local) AddArg(args, logArgs, "/sound");
            if (CurrentOptions.AudioMode == AudioRedirectionMode.Remote)
            {
                AddArg(args, logArgs, "/sound");
                AddArg(args, logArgs, "/microphone");
            }
            AddArg(args, logArgs, CurrentOptions.EnableFontSmoothing ? "+fonts" : "-fonts");
            AddArg(args, logArgs, CurrentOptions.EnableDesktopComposition ? "+aero" : "-aero");
            AddArg(args, logArgs, CurrentOptions.EnableWallpaper ? "+wallpaper" : "-wallpaper");
            AddArg(args, logArgs, CurrentOptions.EnableMenuAnimations ? "+menu-anims" : "-menu-anims");
            if (!CurrentOptions.EnableNLA) AddArg(args, logArgs, "/sec:tls"); // 默认自动协商含 NLA
            if (CurrentOptions.AutoReconnectCount > 0) AddArg(args, logArgs, "/auto-reconnect");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                Arguments = string.Join(" ", args.ToArray()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _userInitiatedDisconnect = false;
            _connectedRaised = 0;
            _disconnectedRaised = 0;
            _outLines = 0;
            _errLines = 0;
            RdpLog.Info("FreeRdp.Start", "exe=" + exe + " target=" + host + ":" + port
                + " user=" + (username ?? "") + (domain != null ? " domain=" + domain : ""));
            RdpLog.Info("FreeRdp.Start", "args=" + string.Join(" ", logArgs.ToArray()));

            _proc = Process.Start(psi);
            try { _proc.EnableRaisingEvents = true; } catch { }
            _proc.Exited += OnProcExited;
            _proc.OutputDataReceived += OnStdout;
            _proc.ErrorDataReceived += OnStderr;
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // 连接成功无显式事件：延迟探测进程存活后上报 connected
            DisposeTimer(ref _connectedTimer);
            _connectedTimer = new Timer(OnConnectedProbe, null, ConnectedProbeDelayMs, Timeout.Infinite);
        }

        private void AddArg(List<string> args, List<string> logArgs, string arg)
        {
            args.Add(arg);
            logArgs.Add(arg);
        }

        private static int MapBpp(int depth)
        {
            switch (depth)
            {
                case 32: return 32;
                case 24: return 24;
                default: return 16; // 8/15/16 统一按 16
            }
        }

        private static string Q(string v)
        {
            if (v.IndexOf(' ') >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\t') >= 0)
                return "\"" + v.Replace("\"", "\\\"") + "\"";
            return v;
        }

        // ===== 进程事件 =====

        private void OnStdout(object sender, DataReceivedEventArgs ev)
        {
            LogStreamLine(true, ev.Data);
        }

        private void OnStderr(object sender, DataReceivedEventArgs ev)
        {
            LogStreamLine(false, ev.Data);
        }

        private void LogStreamLine(bool isOut, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var n = isOut ? Interlocked.Increment(ref _outLines) : Interlocked.Increment(ref _errLines);
            var lower = line.ToLowerInvariant();
            var interesting = lower.Contains("error") || lower.Contains("warn") || lower.Contains("fail")
                || lower.Contains("connected") || lower.Contains("disconnect") || lower.Contains("certificate")
                || lower.Contains("authentication") || lower.Contains("license");
            if (n <= MaxLoggedLines || interesting)
                RdpLog.Info(isOut ? "FreeRdp.out" : "FreeRdp.err", line.Trim());
        }

        private void OnConnectedProbe(object state)
        {
            try
            {
                DisposeTimer(ref _connectedTimer);
                var p = _proc;
                if (_disposed || p == null || p.HasExited) return;
                if (Interlocked.Exchange(ref _connectedRaised, 1) == 1) return;
                RdpLog.Info("FreeRdp.Event", "connected (process alive after probe)");
                var handler = StateChanged;
                if (handler != null) handler(this, new RdpStateChangedEventArgs(true, "connected"));
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("FreeRdp.OnConnectedProbe", ex);
            }
        }

        private void OnProcExited(object sender, EventArgs ev)
        {
            try
            {
                DisposeTimer(ref _connectedTimer);
                int code = -1;
                try { code = _proc.ExitCode; } catch { }
                RdpLog.Info("FreeRdp.Exit", "exitcode=" + code + "(0x" + code.ToString("X") + ")"
                    + " userInitiated=" + _userInitiatedDisconnect
                    + " outLines=" + _outLines + " errLines=" + _errLines);

                if (!_disposed && Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
                {
                    var normal = code == 0 || _userInitiatedDisconnect;
                    var msg = normal ? "连接已关闭" : DescribeExitCode(code);
                    var handler = StateChanged;
                    if (handler != null)
                        handler(this, new RdpStateChangedEventArgs(false, normal ? "closed" : "disconnected", msg));
                }
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("FreeRdp.OnProcExited", ex);
            }
        }

        /// <summary>FreeRDP ERRCONNECT_* 退出码翻译（freerdp/error.h）。</summary>
        private static string DescribeExitCode(int code)
        {
            switch (code)
            {
                case 0x00: return "正常退出";
                case 0x01: return "已断开";
                case 0x02: return "已处于连接中";
                case 0x03: return "连接失败（网络不可达或端口拒绝）";
                case 0x04: return "调用顺序错误";
                case 0x05: return "安全协商失败（NLA/TLS 不匹配）";
                case 0x06: return "TLS 连接失败";
                case 0x07: return "连接被取消";
                case 0x08: return "预连接失败";
                case 0x09: return "连接错误";
                case 0x0A: return "DNS 解析失败";
                case 0x0B: return "主机名未找到";
                case 0x0C: return "传输层连接失败";
                case 0x0D: return "认证失败（用户名或密码错误）";
                case 0x0E: return "通用认证失败";
                case 0x0F: return "KDC 不可达（域认证）";
                default:
                    // NTSTATUS：加载器/运行库级失败，与 ERRCONNECT_* 区分
                    if (code == unchecked((int)0xC0000135)) return "缺少依赖 DLL（如 libcrypto-3-x.dll），请补全 freerdp\\ 目录后重试";
                    if (code == unchecked((int)0xC000007B)) return "DLL 架构不匹配（x64/x86 混用），请换用与程序位数一致的 FreeRDP 包";
                    if (code == unchecked((int)0xC0000142)) return "DLL 初始化失败（运行库损坏或版本不匹配）";
                    if (code == unchecked((int)0x8000FFFF)) return "未知内部错误";
                    return "连接断开（退出码 " + code + "/0x" + code.ToString("X") + "，详见日志）";
            }
        }

        // ===== 尺寸同步（parent-window 下 wfreerdp 不跟随宿主缩放，需手动 MoveWindow 子窗） =====

        private void OnSurfaceResize(object sender, EventArgs ev)
        {
            if (_disposed || !IsConnected) return;
            try { _resizeDebounce.Stop(); _resizeDebounce.Start(); } catch { }
        }

        private void OnResizeDebounceTick(object sender, EventArgs ev)
        {
            try
            {
                _resizeDebounce.Stop();
                if (_disposed || !IsConnected || !_surface.IsHandleCreated) return;
                var child = FindFreeRdpChildWindow();
                if (child == IntPtr.Zero) return;
                MoveWindow(child, 0, 0, _surface.ClientSize.Width, _surface.ClientSize.Height, true);
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("FreeRdp.Resize", ex);
            }
        }

        private IntPtr FindFreeRdpChildWindow()
        {
            if (!_surface.IsHandleCreated) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(_surface.Handle, (child, lParam) =>
            {
                found = child;
                return false; // 取第一个子窗口即停
            }, IntPtr.Zero);
            return found;
        }

        private static void DisposeTimer(ref Timer t)
        {
            var old = t;
            t = null;
            if (old != null)
            {
                try { old.Dispose(); } catch { }
            }
        }

        // ===== Win32 =====

        private const uint WmClose = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc proc, IntPtr lParam);

        // WinForms.Timer 与 System.Threading.Timer 同名冲突，取别名以明语义
        private sealed class WinFormsTimer : System.Windows.Forms.Timer { }
    }
}
