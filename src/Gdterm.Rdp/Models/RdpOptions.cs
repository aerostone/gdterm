namespace Gdterm.Rdp.Models
{
    /// <summary>
    /// RDP 连接选项——控制设备重定向和性能
    /// </summary>
    public class RdpOptions
    {
        // ===== 设备重定向 =====

        /// <summary>
        /// 启用本地磁盘重定向（挂载本地驱动器到远端桌面）
        /// 远端桌面中可通过 \\tsclient\C 访问本地 C 盘
        /// </summary>
        public bool RedirectDrives { get; set; } = false;

        /// <summary>
        /// 启用剪贴板共享（本地 ↔ 远端双向复制粘贴）
        /// </summary>
        public bool RedirectClipboard { get; set; } = true;

        /// <summary>
        /// 启用打印机重定向（远端可使用本地打印机）
        /// </summary>
        public bool RedirectPrinters { get; set; } = false;

        /// <summary>
        /// 启用串口/COM 重定向
        /// </summary>
        public bool RedirectPorts { get; set; } = false;

        /// <summary>
        /// 启用智能卡重定向
        /// </summary>
        public bool RedirectSmartCards { get; set; } = false;

        /// <summary>
        /// 启用 USB 设备重定向
        /// </summary>
        public bool RedirectDevices { get; set; } = false;

        // ===== 音频 =====

        /// <summary>
        /// 音频重定向模式：Local（本地播放）、Remote（远端播放）、None（不播放）
        /// </summary>
        public AudioRedirectionMode AudioMode { get; set; } = AudioRedirectionMode.Local;

        // ===== 显示 =====

        /// <summary>
        /// 颜色深度：8, 15, 16, 24, 32
        /// </summary>
        public int ColorDepth { get; set; } = 32;

        /// <summary>
        /// 启用多显示器支持
        /// </summary>
        public bool UseMultimon { get; set; } = false;

        /// <summary>
        /// 全屏模式
        /// </summary>
        public bool FullScreen { get; set; } = false;

        // ===== 性能 =====

        /// <summary>
        /// 连接带宽类型：Modem(28.8k), BroadbandLow(2Mbps), BroadbandHigh(10Mbps), WAN, LAN, Satellite, Wireless
        /// 影响桌面主题、动画、壁纸等是否自动禁用
        /// </summary>
        public BandwidthType Bandwidth { get; set; } = BandwidthType.BroadbandHigh;

        /// <summary>
        /// 启用桌面壁纸显示（低带宽下建议关闭）
        /// </summary>
        public bool EnableWallpaper { get; set; } = true;

        /// <summary>
        /// 启用菜单动画（低带宽下建议关闭）
        /// </summary>
        public bool EnableMenuAnimations { get; set; } = false;

        /// <summary>
        /// 启用字体平滑
        /// </summary>
        public bool EnableFontSmoothing { get; set; } = true;

        /// <summary>
        /// 启用桌面合成（Aero）
        /// </summary>
        public bool EnableDesktopComposition { get; set; } = true;

        // ===== 连接 =====

        /// <summary>
        /// 连接超时（秒）
        /// </summary>
        public int ConnectionTimeout { get; set; } = 30;

        /// <summary>
        /// 自动重连次数（0=不重连）
        /// </summary>
        public int AutoReconnectCount { get; set; } = 3;

        /// <summary>
        /// 偏好启用网络级别认证（NLA）。
        /// 仅表示“愿意用 NLA”，不硬性强制；是否硬传 /sec:nla 由 ForceNLA 决定。
        /// 取消本勾选 = 不尝试 NLA（针对只认 legacy RDP security 的旧堡垒机）。
        /// </summary>
        public bool EnableNLA { get; set; } = true;

        /// <summary>
        /// 强制 NLA（进程外 wfreerdp 硬传 /sec:nla，禁止降级 legacy RDP security）。
        /// 默认开启：现代服务器（Win2008+/Vista+）均支持 NLA，强制后更安全且避免协商降级。
        /// 若目标为不支持 NLA 的旧堡垒机/NetScaler（只回 li==6，无 rdpNegData），
        /// 取消“强制 NLA”勾选即可让 wfreerdp 自由协商，自动降级 legacy RDP 连上。
        /// </summary>
        public bool ForceNLA { get; set; } = true;

        /// <summary>
        /// 负载均衡路由 token（/load-balance-info）。
        /// 堡垒机/NetScaler 首次下发 LB_LOAD_BALANCE_INFO Cookie（如 tsv://... 或 Cookie: msts=NSFVERIFYHASH=...），
        /// 客户端需在重连时回传该 token，否则 LB 反复踢线。可在连接设置的“负载均衡 token”填入。
        /// </summary>
        public string LoadBalanceInfo { get; set; }

        /// <summary>
        /// 启用 CredSSP 支持
        /// </summary>
        public bool EnableCredSSP { get; set; } = false;
    }

    /// <summary>
    /// 音频重定向模式
    /// </summary>
    public enum AudioRedirectionMode
    {
        /// <summary>
        /// 在本地计算机播放
        /// </summary>
        Local = 0,

        /// <summary>
        /// 在远端计算机播放
        /// </summary>
        Remote = 1,

        /// <summary>
        /// 不播放
        /// </summary>
        None = 2
    }

    /// <summary>
    /// 连接带宽类型
    /// </summary>
    public enum BandwidthType
    {
        Modem = 0,
        BroadbandLow = 1,
        BroadbandHigh = 2,
        WAN = 3,
        LAN = 4,
        Satellite = 5,
        Wireless = 6
    }
}
