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

        // 自动重连上下文：首次连接被 LB 踢掉后，捕获 LB_LOAD_BALANCE_INFO token 并带它重启
        private string _startHost;
        private int _startPort;
        private CredentialPayload _startCredential;
        private string _startUsername;
        private string _startDomain;
        private ConnectionConfig _startConfig;
        private volatile string _detectedLoadBalanceInfo;
        // 首段会话实际协商出的协议（nego done: sel=0x0 = 网关只接受 legacy RDP）。
        // token 进程级重启（TryAutoReconnectWithToken）的新进程不知道协商历史，
        // 若照默认 TLS|NLA(0x3) 请求会被网关 DPU 踢（v0.1.161 实证），
        // 故重启时携带 /sec:rdp 重放同样的 0x0 请求（mstsc 重连行为）。
        private int _negotiatedProtocol = -1;
        // LB 预协商进程级禁用：本网关 CC 从不下发 token（恒 cc:no-token），
        // 首次确认后本进程不再探测，避免每连多一条 X.224 噪音连接（加重限流）。
        private static bool _lbProbeDisabled;
        private int _lbRetried;
        // 跨重启累计的 LB 重连次数：_lbRetried 会在 Start() 中重置，但 NetScaler
        // 每轮下发不同 token，若只看 _lbRetried 会形成无限重连循环。
        private int _lbRetryTotal;
        private const int MaxLbRetryTotal = 2;
        // 排障开关：默认丢弃 wfreerdp 的 [DEBUG] 行；rdp_debug_log=true 时保留全量。
        private bool _debugLogEnabled;
        // 「未解析 PDU」hex dump 采集状态：见到 "not properly parsed" 后开启，遇非 hex 行关闭。
        private bool _unhandledPduDumping;

        // hex dump 形式的 routing token 累加器：FreeRDP 把 `Cookie: msts=...` 的 ASCII
        // 拆到每行 dump 末尾，需跨行拼接直到遇到 CR/LF 结束。
        private bool _hexTokenAccumulating;
        private string _hexTokenBuffer;

        private const int MaxLoggedLines = 400;
        private const int ConnectedProbeDelayMs = 2500;

        public event EventHandler<RdpStateChangedEventArgs> StateChanged;
#pragma warning disable 0067 // 接口 IRdpClient.FileTransferred 契约要求声明，但 FreeRDP 驱动器重定向由系统完成，无逐文件事件
        public event EventHandler<FileTransferEventArgs> FileTransferred;
#pragma warning restore 0067

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
            _startConfig = config;
            Start(config.Host, config.Port > 0 ? config.Port : 3389, credential, options,
                credential != null ? credential.Username : config.Username, config.Domain);
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (tunnelEndpoint == null) throw new ArgumentNullException("tunnelEndpoint");
            _startConfig = config;
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

        /// <summary>探测 wfreerdp.exe 路径；绿色包在 freerdp\，源码运行在 vendor\freerdp\。</summary>
        public static string FindExecutable()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                // 新布局：lib\freerdp\（业界 bin/lib 分类后引擎集中放 lib\）
                Path.Combine(baseDir, "lib", "freerdp", "wfreerdp.exe"),
                // 兑容旧发行包布局
                Path.Combine(baseDir, "freerdp", "wfreerdp.exe"),
                Path.Combine(baseDir, "vendor", "freerdp", "wfreerdp.exe")
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
            _startHost = host;
            _startPort = port;
            _startCredential = credential;
            _startUsername = username;
            _startDomain = domain;
            _detectedLoadBalanceInfo = null;
            _lbRetried = 0;
            _hexTokenAccumulating = false;
            _hexTokenBuffer = null;

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

            // 连接流程第一步：负载均衡预协商。若用户/元数据未显式提供 routing token，
            // 则先做一次 X.224 预协商从网关的 Connection Confirm 变长部分取回 token，
            // 首连即带 /load-balance-info，避免「首次被踢再重连」的中间状态。
            // 预协商失败（非 LB 环境 / 超时 / 网络拒绝）返回 null，不影响普通连接。
            // 进程级禁用：本网关 CC 从不下发 token（恒 cc:no-token），probe 纯属多余连接，
            // 还会加重网关限流。首次 no-token 后本进程不再探测（重启后重试一次）。
            if (string.IsNullOrEmpty(CurrentOptions.LoadBalanceInfo) && !_lbProbeDisabled)
            {
                var probed = RdpLoadBalanceProbe.Probe(host, port);
                RdpLog.Info("FreeRdp.LB", "probe result=" + (probed == null ? "<null>" : "token")
                    + " detail=" + RdpLoadBalanceProbe.LastProbeDetail);
                if (!string.IsNullOrEmpty(probed))
                {
                    CurrentOptions.LoadBalanceInfo = probed;
                    // 会话级内存即可：token 是网关会话产物（NSFVERIFYHASH 每 redirect 换新值、
                    // 会话结束即死），绝不能写回 _startConfig.Metadata —— 那是用户存储的共享
                    // 配置对象，写入会随连接库落盘，下次开新连接重放死 token。
                    // 用户手填的 rdp_loadbalance（ConnectionDialog）仍走持久化路径。
                    RdpLog.Info("FreeRdp.LB", "pre-negotiation captured token, target=" + host + ":" + port);
                }
                else if (RdpLoadBalanceProbe.LastProbeDetail == "cc:no-token")
                {
                    _lbProbeDisabled = true;
                    RdpLog.Info("FreeRdp.LB", "probe disabled for this process (gateway never issues CC token)");
                }
            }

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
            // 日志级别：进程始终开 debug（redirect token 的 hex dump 捕获依赖 DEBUG 级输出），
            // 但默认只在落盘时丢弃 [DEBUG] 行（见 LogStreamLine）；连接元数据 rdp_debug_log=true
            // 时全量落盘，供堡垒机踢线等问题排障。
            bool debugLog = false;
            try
            {
                debugLog = _startConfig != null && _startConfig.Metadata != null
                    && _startConfig.Metadata.ContainsKey("rdp_debug_log")
                    && _startConfig.Metadata["rdp_debug_log"] == "true";
            }
            catch { }
            _debugLogEnabled = debugLog;
            AddArg(args, logArgs, "/log-level:debug");
            // 负载均衡：预协商阶段已把 routing token 写入 CurrentOptions，这里作为协议字段传给 wfreerdp
            if (!string.IsNullOrEmpty(CurrentOptions.LoadBalanceInfo))
                AddArg(args, logArgs, "/load-balance-info:" + Q(CurrentOptions.LoadBalanceInfo));
            // 凭据始终随首连传递（keepass 自动登录）；FreeRDP 内部转发重连
            // （rdp_client_redirect，见 appveyor.yml 补丁）会无条件清空未被网关
            // LB_USERNAME/LB_DOMAIN/LB_PASSWORD 标志确认的 Username/Domain/Password
            // 并关闭 AutoLogonEnabled，目标服务器自协商显示登录界面——与 mstsc 行为
            // 一致，不再把保存的旧凭据带到转发目标（v0.1.149 实测：LB token 重连
            // 若仍带旧自动登录凭据，0.1s 内即被 LOGOFF_BY_USER 踢线）。
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
            // 安全层决策（核心）：
            // 仅当用户显式勾选「强制 NLA」时才硬传 /sec:nla 禁止降级（现代服务器默认）。
            // 其余一律不传 /sec:xxx，由 wfreerdp 自由协商（NLA/TLS/RDP 三路）。
            //   NetScaler/LB 网关首连与 redirect 重连均只回 li==6（no rdpNegData，仅 legacy RDP security），
            //   legacy RDP 才能连上；redirect 重连只需回传新 token（/load-balance-info），
            //   绝不能再 /sec:nla（否则关 RDP 通路 → NEGO_STATE_FAIL 0x2000C）。
            // 「NLA 认证」仅是偏好，「已有 LB token / 重连中」也不该触发 /sec:nla。
            if (CurrentOptions.ForceNLA)
                AddArg(args, logArgs, "/sec:nla");
            // token 重启且首段会话协商为 legacy RDP（sel=0x0）时重放同样的请求。
            // 实证（v0.1.161 抓包）：网关 NSFVERIFYHASH token 续会只接受首段协商
            // 模式的重连——token CR 若请求 TLS|NLA(0x3) 会在 client info 后被 DPU 踢；
            // 请求 legacy RDP(0x0) 则整段会话存活（mstsc 黄金样本 c57179）。
            // 进程内 redirect/auto-reconnect 重连由 FreeRDP 补丁锁定（见 appveyor.yml），
            // 这里覆盖进程级重启（TryAutoReconnectWithToken 新起 wfreerdp）：
            // _negotiatedProtocol 在本客户端实例生命周期内已捕获首段协商结果，
            // 跨会话不重放（协商结果是网关会话级产物，不做持久化记忆）。
            else if (_negotiatedProtocol == 0 && !string.IsNullOrEmpty(CurrentOptions.LoadBalanceInfo))
                AddArg(args, logArgs, "/sec:rdp");
            // 否则不传任何 /sec:xxx，由 wfreerdp 自动协商（含 legacy RDP）
            if (CurrentOptions.AutoReconnectCount > 0)
            {
                // 显式传次数，避免 FreeRDP 默认重试次数对堡垒机形成连接风暴（触发限流/拉黑）。
                // 注意 FreeRDP 2.x CLI 语法：/auto-reconnect:3 是非法的（Invalid sigil），
                // 必须用 +auto-reconnect 启用 + /auto-reconnect-max-retries:<n> 限次。
                AddArg(args, logArgs, "+auto-reconnect");
                AddArg(args, logArgs, "/auto-reconnect-max-retries:" + CurrentOptions.AutoReconnectCount);
            }
            // 旧版堡垒机/RDP 代理常发送未在能力协商中声明的绘图指令（Cache Bitmap V2 等），
            // wfreerdp 会直接断链："SERVER BUG: The support for this feature was not announced!"
            // relax-order-checks 跳过严格校验，bitmap-cache 按 FreeRDP 提示缓解同类问题。
            AddArg(args, logArgs, "/relax-order-checks");
            AddArg(args, logArgs, "+bitmap-cache");

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
            // 连接意图诊断：NLA/LB 决策集中打点，配合 wfreerdp DEBUG 日志定位堡垒机踢线
            RdpLog.Info("FreeRdp.Start", "security intent: nla=" + CurrentOptions.EnableNLA
                + " forceNla=" + CurrentOptions.ForceNLA
                + " loadBalanceInfo=" + (string.IsNullOrEmpty(CurrentOptions.LoadBalanceInfo) ? "<none>" : CurrentOptions.LoadBalanceInfo)
                + " autoReconnect=" + CurrentOptions.AutoReconnectCount
                + " sec=" + (CurrentOptions.ForceNLA ? "nla-forced" : "negotiate"));
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

            // 未解析 PDU 专项落盘（FreeRdp.pdu 标签）：服务器（堡垒机）发送了 FreeRDP 2.x
            // 不认识的 PDU 时，“not properly parsed, N bytes remaining” 是唯一线索，
            // 其内容很可能就是踢出原因（如 logonErrorInfo 变体）。该行及其随后的 hex dump
            // 行只走 pdu 通道，避免与下方常规 interesting 过滤重复落盘。hex dump 极少且短。
            // The gdterm hexdump line (FreeRDP patch diagnostic) is followed by winpr_HexDump output,
            // which likewise goes through the pdu channel for complete persistence.
            bool unhandledPdu = lower.Contains("not properly parsed") || lower.Contains("gdterm hexdump");
            bool isPduHexDump = _unhandledPduDumping && IsHexDumpLine(line);
            bool routedToPdu = unhandledPdu || isPduHexDump;
            if (unhandledPdu)
            {
                RdpLog.Info("FreeRdp.pdu", line.Trim());
                _unhandledPduDumping = true;
            }
            else if (_unhandledPduDumping)
            {
                if (isPduHexDump)
                    RdpLog.Info("FreeRdp.pdu", line.Trim());
                else
                    _unhandledPduDumping = false; // hex dump 区结束
            }

            // 日志瘦身：默认丢弃 [DEBUG] 行不落盘（nego 状态机逐行、hex dump 等）；
            // 连接元数据 rdp_debug_log=true 时全量落盘供排障。
            // 注意：下方 token 捕获逻辑对包括 DEBUG 行在内的所有行都要执行。
            bool isDebugLevel = lower.Contains("[debug]");
            if ((!isDebugLevel || _debugLogEnabled) && !routedToPdu)
            {
                var interesting = lower.Contains("error") || lower.Contains("warn") || lower.Contains("fail")
                    || lower.Contains("connected") || lower.Contains("disconnect") || lower.Contains("certificate")
                    || lower.Contains("authentication") || lower.Contains("license") || lower.Contains("transport")
                    || lower.Contains("order") || lower.Contains("bitmap") || lower.Contains("redirect")
                    || lower.Contains("nego") || lower.Contains("tls") || lower.Contains("security")
                    || lower.Contains("load-balance") || lower.Contains("loadbalance") || lower.Contains("routing")
                    || lower.Contains("logoff") || lower.Contains("server bug") || lower.Contains("capability");
                if (n <= MaxLoggedLines || interesting)
                    RdpLog.Info(isOut ? "FreeRdp.out" : "FreeRdp.err", line.Trim());
            }

            // 捕获负载均衡路由 token：NetScaler 等 LB 网关在首次握手时下发
            // LB_LOAD_BALANCE_INFO '<token>'（或 hex dump 形式），客户端必须回传 token
            // 才能通过 redirect 重连。两种格式都兼容：
            // 1) 单引号直接包裹：LB_LOAD_BALANCE_INFO 'Cookie: msts=...'
            // 2) hex dump 行：`0048 62 32 37 35 ...         b2757a90da1..`（ASCII 列拼接）
            if (_detectedLoadBalanceInfo == null)
            {
                var idx = lower.IndexOf("lb_load_balance_info");
                if (idx >= 0)
                {
                    var rest = line.Substring(idx + "lb_load_balance_info".Length);
                    var q1 = rest.IndexOf('\'');
                    if (q1 >= 0)
                    {
                        var q2 = rest.IndexOf('\'', q1 + 1);
                        if (q2 > q1)
                        {
                            _detectedLoadBalanceInfo = rest.Substring(q1 + 1, q2 - q1 - 1).Trim();
                            RdpLog.Info("FreeRdp.LB", "detected load-balance token: " + _detectedLoadBalanceInfo);
                        }
                    }
                }
            }

            // 捕获首段会话协商出的协议（gdterm FreeRDP 补丁输出）:
            //   "gdterm redirect nego done: req=0x0 sel=0x0"
            // sel=0x0 表示网关只接受 legacy RDP；token 进程级重启时需 /sec:rdp 重放。
            if (_negotiatedProtocol < 0 && lower.Contains("nego done"))
            {
                var si = lower.IndexOf("sel=0x");
                if (si >= 0)
                {
                    int sel;
                    var hex = lower.Substring(si + 6, Math.Min(8, lower.Length - si - 6)).Split(' ', '\t')[0];
                    if (int.TryParse(hex.TrimEnd(';'), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out sel))
                    {
                        if (sel == 0)
                        {
                            _negotiatedProtocol = sel;
                            RdpLog.Info("FreeRdp.nego", "negotiated protocol: legacy RDP (0x0) — token restart will use /sec:rdp");
                            // 不写 rdp_negotiated_proto 到元数据：/sec:rdp 属本网关会话级协商
                            // 结果，持久化后配置若指向 NLA 网关会直接 0x2000C 连不上。
                            // 进程内状态 _negotiatedProtocol 已覆盖同客户端重启场景。
                        }
                    }
                }
            }

            // redirect 后 FreeRDP 输出的 hex dump 会把 routing token 的 ASCII 拼到每行末尾。
            // 累加 `Cookie: msts=...` 直到遇到 CR/LF 结束的 token，再完整回传。
            AccumulateHexRoutingToken(line);
        }

        /// <summary>
        /// 从 redirect hex dump 行中累加 routing token。
        /// FreeRDP 用 winpr_HexLogDump 输出，格式固定：
        ///   %04zx + 空格 + 每字节 "%02x "（3 字符）+ ASCII 列（不可打印为 '.'）
        /// 例：
        ///   0000 43 6f 6f 6b 69 65 3a 20 6d 73 74 73 3d 4e 53 46  Cookie: msts=NSF
        ///   0048 64 33 31 35 61 31 61 36 63 39 61 0d 0a          d315a1a6c9a..
        /// 直接解析 hex 字节区（避开 ASCII 列对齐空格不可靠的问题），
        /// 解码为 ASCII 拼接，直到遇到 0d0a (CR LF) 结束，得到完整 token。
        /// </summary>
        private void AccumulateHexRoutingToken(string line)
        {
            if (_detectedLoadBalanceInfo != null) return;

            // 只识别 hex dump 行：前 4 字符是 16 进制 offset，第 5 字符是空格。
            if (!IsHexLine(line)) return;

            // 先解码本行 hex 字节，看解码后的 ASCII 是否包含 "Cookie: msts="。
            var decoded = DecodeHexLineAscii(line);
            if (decoded == null) return;

            bool isTokenStart = decoded.IndexOf("Cookie: msts=", StringComparison.OrdinalIgnoreCase) >= 0
                || decoded.IndexOf("Cookie: mstshash=", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!_hexTokenAccumulating && !isTokenStart)
                return; // 尚未开始且本行不是 token 开头，忽略

            _hexTokenAccumulating = true;
            _hexTokenBuffer += decoded;

            // token 以 CR/LF (0d0a) 结束，解码时已保留为实际 CR/LF，遇之即可收尾。
            if (decoded.IndexOf('\r') >= 0 || decoded.IndexOf('\n') >= 0)
                TrimHexTokenAndCommit();
        }

        private static bool IsHexLine(string line)
        {
            return line.Length >= 5 && IsHex(line[0]) && IsHex(line[1]) && IsHex(line[2])
                && IsHex(line[3]) && line[4] == ' ';
        }

        /// <summary>FreeRDP winpr_HexDump 行：经 WLog 输出，带「[time] [pid:tid] [LEVEL][comp] - 」前缀，
        /// message 部分形如「0048 62 32 37 35 ...  ascii」。识别前缀后的 hex 内容。</summary>
        private static bool IsHexDumpLine(string line)
        {
            // WLog 行格式：... [LEVEL][component] - <message>，用最后一个 "] - " 切分
            int sep = line.LastIndexOf("] - ");
            if (sep < 0) return false;
            int m = sep + 4; // message 起点
            if (m + 7 > line.Length) return false;
            // offset 4 hex + 空格 + 至少 1 字节（2 hex）
            return IsHex(line[m]) && IsHex(line[m + 1]) && IsHex(line[m + 2]) && IsHex(line[m + 3])
                && line[m + 4] == ' ' && IsHex(line[m + 5]) && IsHex(line[m + 6]);
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        /// <summary>
        /// 解析 hex dump 行的 hex 字节区并解码为 ASCII。
        /// 格式：`0000 43 6f ... 0d 0a          Cookie: ...`，offset 后每 3 字符一个字节（"%02x "）。
        /// 不可打印字节（<0x20 或 >=0x7f）用 '.' 表示，0d/0a 保留为实际 CR/LF。
        /// </summary>
        private static string DecodeHexLineAscii(string line)
        {
            var sb = new System.Text.StringBuilder();
            int pos = 5; // 跳过 `%04x `
            while (pos + 2 < line.Length)
            {
                char h = line[pos], l = line[pos + 1];
                if (!IsHex(h) || !IsHex(l))
                    break; // 读不到完整字节即止（可能已到 ASCII 列区域）
                int b = Convert.ToByte(line.Substring(pos, 2), 16);
                pos += 3; // "%02x " 每个字节占 3 字符
                if (b == 0x0d) sb.Append('\r');
                else if (b == 0x0a) sb.Append('\n');
                else if (b >= 0x20 && b < 0x7f) sb.Append((char)b);
                else sb.Append('.');
            }
            if (sb.Length == 0) return null;
            return sb.ToString();
        }

        private void TrimHexTokenAndCommit()
        {
            string token = _hexTokenBuffer;
            _hexTokenBuffer = null;
            _hexTokenAccumulating = false;
            if (string.IsNullOrEmpty(token)) return;
            // 去掉末尾的 CR/LF 及其后部分（0d0a 是 token 的终止符）
            int d = token.IndexOf('\r');
            if (d < 0) d = token.IndexOf('\n');
            if (d >= 0) token = token.Substring(0, d);
            token = token.Trim();
            if (token.Length == 0) return;
            _detectedLoadBalanceInfo = token;
            RdpLog.Info("FreeRdp.LB", "detected load-balance token (hex): " + _detectedLoadBalanceInfo);
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

                if (!_disposed && Interlocked.Exchange(ref _disconnectedRaised, 1) == 0
                    && !TryAutoReconnectWithToken(code))
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

        /// <summary>
        /// 负载均衡自动重连：首次连接被 LB 网关踢掉（未回传 token）时，
        /// 用从 wfreerdp 输出捕获的 LB_LOAD_BALANCE_INFO token 带上 /load-balance-info 重启，
        /// 并把 token 回写配置元数据。用户无需手动操作。
        /// 返回 true 表示已接管并触发重连（不再上报 disconnected）。
        /// </summary>
        private bool TryAutoReconnectWithToken(int exitCode)
        {
            if (exitCode == 0 || _userInitiatedDisconnect || _disposed) return false;
            if (string.IsNullOrEmpty(_detectedLoadBalanceInfo)) return false;
            if (string.Equals(_detectedLoadBalanceInfo, CurrentOptions.LoadBalanceInfo, StringComparison.Ordinal)) return false;
            if (Interlocked.Exchange(ref _lbRetried, 1) != 0) return false;

            // 凭据说明：堡垒机多为带内认证（密码在堡垒机自己的登录页输入），
            // 连接配置里没有密码是正常形态，不作为禁止重连的理由。
            // 总次数守卫：_lbRetried 每次 Start() 会复位，且 LB 网关每轮下发不同 token
            // 会让「新 token != 当前 token」恒成立。用独立的累计计数封顶，防无限循环。
            if (Interlocked.Increment(ref _lbRetryTotal) > MaxLbRetryTotal)
            {
                RdpLog.Info("FreeRdp.LB", "lb auto-reconnect budget exhausted, total=" + _lbRetryTotal);
                return false;
            }

            CurrentOptions.LoadBalanceInfo = _detectedLoadBalanceInfo;
            // 不写 _startConfig.Metadata（rdp_loadbalance）：token 会话级产物，跨会话重放
            // 必然死 token（见上方 probe 处注释）。客户端内重启由 CurrentOptions 携带。
            RdpLog.Info("FreeRdp.LB", "auto-reconnect with token, retried=" + _lbRetried);
            // 重连带上新 token（/load-balance-info），但不强制 /sec:nla：
            // NetScaler redirect 重连仍回 li==6（仅 legacy RDP），继续走自由协商。

            // 回到 UI 线程重启（Start 会访问控件句柄）
            Action restart = () =>
            {
                try { Start(_startHost, _startPort, _startCredential, CurrentOptions, _startUsername, _startDomain); }
                catch (Exception ex) { RdpLog.Swallowed("FreeRdp.LB", ex); }
            };
            try
            {
                if (_surface.IsHandleCreated && _surface.InvokeRequired) _surface.BeginInvoke(restart);
                else restart();
            }
            catch (Exception ex) { RdpLog.Swallowed("FreeRdp.LB", ex); }
            return true;
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
                    // ERRINFO_*（0x10000 段，MS-RDPBCGR 2.2.5.1.1）：服务器在会话中主动上报的错误信息
                    if (code == 0x1000C) return "服务器主动注销了此会话（LOGOFF_BY_USER）：常见于堡垒机会话超时/被顶号/管理员强制注销；若在堡垒机登录页输入密码后仍断开，请检查堡垒机单会话限制或账号在其它客户端登录";
                    if (code == 0x10004) return "服务器强制断开（策略限制或会话被接管）";
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
