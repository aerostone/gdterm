using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Rdp.Models;

namespace Gdterm.Rdp
{
    /// <summary>
    /// IMsTscAxEvents dispinterface（mstscax 事件源，IID 336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6）。
    /// 声明为 ComImport + InterfaceIsIDispatch，让 RdpEventSink 实现它后，连接点
    /// QI {336D5562-...} 能命中托管对象 → Advise 不再返回 0x80040202。
    /// dispid 顺序按 ReactOS mstsclib.idl（权威定义）。
    /// </summary>
    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IMsTscAxEvents
    {
        [DispId(1)]  void OnConnecting();
        [DispId(2)]  void OnConnected();
        [DispId(3)]  void OnLoginComplete();
        [DispId(4)]  void OnDisconnected(int discReason);
        [DispId(5)]  void OnEnterFullScreenMode();
        [DispId(6)]  void OnLeaveFullScreenMode();
        [DispId(7)]  void OnChannelReceivedData([MarshalAs(UnmanagedType.BStr)] string chanName, [MarshalAs(UnmanagedType.BStr)] string data);
        [DispId(8)]  void OnRequestGoFullScreen();
        [DispId(9)]  void OnRequestLeaveFullScreen();
        [DispId(10)] void OnFatalError(int errorCode);
        [DispId(11)] void OnWarning(int warningCode);
        [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
        [DispId(13)] void OnIdleTimeoutNotification();
        [DispId(14)] void OnRequestContainerMinimize();
        [DispId(15)] bool OnConfirmClose();
        [DispId(16)] bool OnReceivedTSPublicKey([MarshalAs(UnmanagedType.BStr)] string publicKey);
        [DispId(17)] int  OnAutoReconnecting(int disconnectReason, int attemptCount);
        [DispId(18)] void OnAuthenticationWarningDisplayed();
        [DispId(19)] void OnAuthenticationWarningDismissed();
    }

    /// <summary>
    /// RDP 客户端——AxHost 按 CLSID 直接承载 mstscax ActiveX，零 interop DLL 依赖。
    ///
    /// 背景：旧实现运行时反射加载 AxMsTscLib.dll（aximp 生成），但该 DLL 不在仓库
    /// 也不在 CI 产物里，ResolveAxType() 永远返回 null，导致“RDP 控件不可用”。
    ///
    /// 现方案：
    /// - CLSID 回落链 10→9→8→7→6→1，取注册表里实际存在的最新版本；
    /// - 属性读写走 IDispatch 后期绑定（__ComObject + InvokeMember）；
    /// - 事件走 ComImport dispinterface 汇类（QI 命中 + dispid 路由）；
    /// - 密码双通道：NotSafeForScripting CLSID 可 IDispatch 写 ClearTextPassword；
    ///   KeePass 预写 TERMSRV/host 到 Windows 凭据管理器（所有版本兜底）。
    /// </summary>
    public class RdpClient : IRdpClient
    {
        // NotSafeForScripting 版本允许写 ClearTextPassword；普通版本依赖凭据管理器
        private static readonly string[] ClsidChain =
        {
            "{A0C63C30-F08D-4AB4-907C-34905D770C7D}", // MsRdpClient10NotSafeForScripting (Win10+)
            "{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}", // MsRdpClient9NotSafeForScripting
            "{A3BC03A0-041D-42E3-AD22-882B7865C9C5}", // MsRdpClient8NotSafeForScripting (Win8+)
            "{54D38BF7-B1EF-4479-9674-1BD6EA465258}", // MsRdpClient7NotSafeForScripting (Win7 SP1+)
            "{7390F3D8-0439-4C05-91E3-CF5CB290C3D0}", // MsRdpClient6 (Vista+)
            "{791FA017-2DE3-492E-ACC5-53C67A2B94D0}", // MsRdpClient (base)
        };

        private RdpAxHost _ax;
        private UserControl _container;
        private object _ocx;          // ActiveX 控件（__ComObject，IDispatch 后期绑定）
        private object _adv;          // AdvancedSettings*（IDispatch 后期绑定）
        private RdpEventSink _sink;
        private IConnectionPoint _cp;
        private int _eventCookie;
        private bool _disposed;
        private bool _isViaTunnel;

        public bool IsConnected
        {
            get
            {
                var ocx = _ocx;
                if (ocx == null) return false;
                var v = GetProp(ocx, "Connected");
                if (v == null) return false;
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1; }
                catch { return false; }
            }
        }

        public UserControl Control => _container;

        public RdpOptions CurrentOptions { get; private set; }

        public event EventHandler<RdpStateChangedEventArgs> StateChanged;
        // 文件传输事件预留：ActiveX 剪贴板通道未挂钩，用显式 add/remove 避免 CS0067
        public event EventHandler<FileTransferEventArgs> FileTransferred
        {
            add { }
            remove { }
        }

        public RdpClient()
        {
            _container = new UserControl { Dock = DockStyle.Fill };

            try
            {
                var clsid = PickClsid();
                if (clsid == null)
                    throw new InvalidOperationException("mstscax ActiveX 未注册（需要 Windows 远程桌面组件）");
                RdpLog.Info("RdpClient.ctor", "clsid=" + clsid);

                _ax = new RdpAxHost(clsid) { Dock = DockStyle.Fill };
                _container.Controls.Add(_ax);
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("RdpClient.ctor", ex);
                _ax = null;
                var label = new Label
                {
                    Text = "RDP 控件不可用（需要 Windows 环境 + mstscax ActiveX）",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter
                };
                _container.Controls.Add(label);
            }
        }

        public void Connect(ConnectionConfig config, CredentialPayload credential, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var ocx = RequireOcx();

            _isViaTunnel = false;
            CurrentOptions = options ?? new RdpOptions();
            ApplyOptions(config, credential);
            RdpLog.Info("RdpClient.Connect", "host=" + config.Host + " port=" + config.Port + " user=" + (credential?.Username ?? config.Username ?? ""));
            InvokeMethod(ocx, "Connect");
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (tunnelEndpoint == null) throw new ArgumentNullException(nameof(tunnelEndpoint));
            var ocx = RequireOcx();

            _isViaTunnel = true;
            CurrentOptions = options ?? new RdpOptions();

            SetProp(ocx, "Server", tunnelEndpoint.LocalHost ?? "127.0.0.1");
            SetProp(ocx, "UserName", credential?.Username ?? config.Username ?? "");
            if (!string.IsNullOrEmpty(credential?.Password))
                SetProp(_adv, "ClearTextPassword", credential.Password);
            SetProp(_adv, "RDPPort", tunnelEndpoint.LocalPort);
            if (!string.IsNullOrEmpty(config.Domain))
                SetProp(ocx, "Domain", config.Domain);

            ApplyOptions(config, credential);
            RdpLog.Info("RdpClient.ConnectViaTunnel", "endpoint=" + tunnelEndpoint.LocalHost + ":" + tunnelEndpoint.LocalPort + " user=" + (credential?.Username ?? config.Username ?? ""));
            InvokeMethod(ocx, "Connect");
        }

        public void Disconnect()
        {
            var ocx = _ocx;
            if (ocx == null) return;
            if (IsConnected)
            {
                try { InvokeMethod(ocx, "Disconnect"); }
                catch (Exception ex) { RdpLog.Swallowed("RdpClient.Disconnect", ex); }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Disconnect(); } catch { }
            try
            {
                if (_eventCookie != 0 && _cp != null) _cp.Unadvise(_eventCookie);
            }
            catch { }
            _eventCookie = 0;
            _cp = null;
            try { _ax?.Dispose(); } catch { }
            try { _container?.Dispose(); } catch { }
        }

        // ===== 内部：OCX 解析 / 选项 / 事件 =====

        private object RequireOcx()
        {
            if (_ax == null)
                throw new InvalidOperationException("RDP 控件不可用（需要 Windows 环境 + mstscax ActiveX）");
            return EnsureOcx();
        }

        /// <summary>首次访问时解析底层 OCX 并挂事件。必须在 UI 线程调用（会强制创建句柄）。</summary>
        private object EnsureOcx()
        {
            if (_ocx != null) return _ocx;
            try { var dummy = _ax.Handle; } catch (Exception ex) { RdpLog.Swallowed("RdpClient.EnsureOcx:CreateHandle", ex); } // 强制创建句柄 → AxHost 实例化 OCX
            _ocx = _ax.GetOcx();
            if (_ocx == null)
            {
                RdpLog.Info("RdpClient.EnsureOcx", "GetOcx returned null");
                throw new InvalidOperationException("RDP ActiveX 实例化失败");
            }
            _adv = ResolveAdvancedSettings(_ocx);
            RdpLog.Info("RdpClient.EnsureOcx", "ocx=" + _ocx.GetType().Name + " adv=" + (_adv != null ? "ok" : "null"));
            HookEvents(_ocx);
            return _ocx;
        }

        private void ApplyOptions(ConnectionConfig config, CredentialPayload credential)
        {
            var ocx = _ocx;
            var opts = CurrentOptions;
            if (ocx == null || opts == null) return;

            if (!_isViaTunnel)
            {
                SetProp(ocx, "Server", config.Host);
                SetProp(ocx, "UserName", credential?.Username ?? config.Username ?? "");
                if (!string.IsNullOrEmpty(credential?.Password))
                    SetProp(_adv, "ClearTextPassword", credential.Password);
                if (config.Port > 0 && config.Port != 3389)
                    SetProp(_adv, "RDPPort", config.Port);
                if (!string.IsNullOrEmpty(config.Domain))
                    SetProp(ocx, "Domain", config.Domain);
            }

            var adv = _adv;
            if (adv == null) return;

            SetProp(adv, "RedirectDrives", opts.RedirectDrives);
            SetProp(adv, "RedirectClipboard", opts.RedirectClipboard);
            SetProp(adv, "RedirectPrinters", opts.RedirectPrinters);
            SetProp(adv, "RedirectPorts", opts.RedirectPorts);
            SetProp(adv, "RedirectSmartCards", opts.RedirectSmartCards);
            SetProp(adv, "RedirectDevices", opts.RedirectDevices);
            SetProp(adv, "AudioRedirectionMode", (int)opts.AudioMode);
            SetProp(ocx, "ColorDepth", opts.ColorDepth);
            // UseMultimon 在 IMsRdpClientNonScriptable5（IUnknown 接口，IDispatch 后期绑定不通），
            // 无法通过 AdvancedSettings 设置；多屏支持留待需要时走 NonScriptable5 专属通道
            if (opts.FullScreen)
                SetProp(ocx, "FullScreen", true);

            SetProp(adv, "BandwidthDetection", true);
            SetProp(adv, "EnableAutoReconnect", opts.AutoReconnectCount > 0);
            SetProp(adv, "MaxReconnectAttempts", opts.AutoReconnectCount);
            // 字体平滑/桌面合成不是独立属性，走 PerformanceFlags 位掩码：
            // TS_PERF_ENABLE_FONT_SMOOTHING=0x80, TS_PERF_ENABLE_DESKTOP_COMPOSITION=0x100
            int perf = 0;
            if (opts.EnableFontSmoothing) perf |= 0x80;
            if (opts.EnableDesktopComposition) perf |= 0x100;
            SetProp(adv, "PerformanceFlags", perf);
            SetProp(adv, "singleConnectionTimeout", opts.ConnectionTimeout);
            // NLA：typelib 属性名是 EnableCredSspSupport（旧代码写 "EnableNLA" 是无效名，一直被吞掉）
            if (opts.EnableNLA)
                SetProp(adv, "EnableCredSspSupport", true);
            if (opts.EnableCredSSP)
                SetProp(adv, "AuthenticationLevel", 2);
        }

        /// <summary>注册表探测可用的最新 mstscax CLSID；找不到返回 null。</summary>
        private static string PickClsid()
        {
            foreach (var id in ClsidChain)
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("CLSID\\" + id))
                    {
                        if (k != null) return id;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>新控件暴露 AdvancedSettingsN（N 越高接口越全）；回落取第一个可用的。</summary>
        private static object ResolveAdvancedSettings(object ocx)
        {
            var names = new[]
            {
                "AdvancedSettings7", "AdvancedSettings6", "AdvancedSettings5", "AdvancedSettings4",
                "AdvancedSettings3", "AdvancedSettings2", "AdvancedSettings"
            };
            foreach (var name in names)
            {
                var adv = GetProp(ocx, name);
                if (adv != null) return adv;
            }
            return null;
        }

        private void HookEvents(object ocx)
        {
            try
            {
                var cpc = (IConnectionPointContainer)ocx;
                IConnectionPoint cp;
                var iid = typeof(IMsTscAxEvents).GUID;
                cpc.FindConnectionPoint(ref iid, out cp);
                if (cp == null) { RdpLog.Info("RdpClient.HookEvents", "connection point not found"); return; }

                _sink = new RdpEventSink(this);
                // sink 实现 ComImport dispinterface → CCW 对 QI {336D5562-...} 返回自身 → Advise 成功
                // （旧实现用 AutoDispatch 类接口，QI 命中不了事件 IID → 0x80040202）
                int cookie;
                cp.Advise(_sink, out cookie);
                _eventCookie = cookie;
                _cp = cp;
                RdpLog.Info("RdpClient.HookEvents", "advised, cookie=" + cookie);
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("RdpClient.HookEvents", ex);
            }
        }

        internal void RaiseConnected()
        {
            RdpLog.Info("RdpClient.Event", "OnConnected/OnLoginComplete");
            StateChanged?.Invoke(this, new RdpStateChangedEventArgs(true, "connected"));
        }

        internal void RaiseDisconnected(int reason)
        {
            RdpLog.Info("RdpClient.Event", "OnDisconnected reason=" + reason + " msg=" + GetDisconnectReasonMessage(reason));
            StateChanged?.Invoke(this, new RdpStateChangedEventArgs(false, "disconnected", GetDisconnectReasonMessage(reason)));
        }

        internal void RaiseFatalError(int errorCode)
        {
            RdpLog.Info("RdpClient.Event", "OnFatalError code=" + errorCode);
            StateChanged?.Invoke(this, new RdpStateChangedEventArgs(false, "fatal-error", "RDP 致命错误 (代码 " + errorCode + ")"));
        }

        // ===== IDispatch 后期绑定辅助 =====

        private static object GetProp(object target, string name)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.GetProperty, null, target, null);
            }
            catch { return null; }
        }

        private static void SetProp(object target, string name, object value)
        {
            if (target == null) return;
            try
            {
                target.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.SetProperty, null, target, new[] { value });
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("RdpClient.SetProp:" + name, ex);
            }
        }

        private static void InvokeMethod(object target, string name)
        {
            if (target == null) return;
            try
            {
                target.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.InvokeMethod, null, target, null);
            }
            catch (Exception ex)
            {
                RdpLog.Swallowed("RdpClient.Invoke:" + name, ex);
                // Connect 失败必须向上抛出，让 ProtocolTabOpener 显示“RDP 连接失败” MessageBox；
                // 其他方法（Disconnect 等）吞掉即可。
                throw;
            }
        }

        private static string GetDisconnectReasonMessage(int reasonCode)
        {
            switch (reasonCode)
            {
                case 0: return "本地初始化断开";
                case 1: return "远程桌面已关闭";
                case 2: return "用户断开连接";
                case 3: return "空闲超时";
                case 4: return "会话超时";
                case 5: return "另一用户连接";
                case 6: return "服务器拒绝连接";
                case 10: return "网络连接丢失";
                default: return "断开连接 (原因代码: " + reasonCode + ")";
            }
        }

        /// <summary>直接按 CLSID 承载 mstscax（无需 aximp interop）。</summary>
        private sealed class RdpAxHost : AxHost
        {
            public RdpAxHost(string clsid) : base(clsid) { }
        }

        /// <summary>
        /// IMsTscAxEvents 事件汇——实现 ComImport dispinterface，CCW 的 QI 能命中
        /// 事件 IID，Advise 成功；事件按 [DispId] 路由到对应方法。
        /// </summary>
        [ComVisible(true)]
        private sealed class RdpEventSink : IMsTscAxEvents
        {
            private readonly RdpClient _owner;

            public RdpEventSink(RdpClient owner)
            {
                _owner = owner;
            }

            public void OnConnecting() { RdpLog.Info("RdpClient.Event", "OnConnecting"); }
            public void OnConnected() { RdpLog.Info("RdpClient.Event", "OnConnected"); _owner.RaiseConnected(); }
            public void OnLoginComplete() { RdpLog.Info("RdpClient.Event", "OnLoginComplete"); _owner.RaiseConnected(); }
            public void OnDisconnected(int discReason) { _owner.RaiseDisconnected(discReason); }
            public void OnEnterFullScreenMode() { }
            public void OnLeaveFullScreenMode() { }
            public void OnChannelReceivedData(string chanName, string data) { }
            public void OnRequestGoFullScreen() { }
            public void OnRequestLeaveFullScreen() { }
            public void OnFatalError(int errorCode) { _owner.RaiseFatalError(errorCode); }
            public void OnWarning(int warningCode) { RdpLog.Info("RdpClient.Event", "OnWarning code=" + warningCode); }
            public void OnRemoteDesktopSizeChange(int width, int height) { }
            public void OnIdleTimeoutNotification() { }
            public void OnRequestContainerMinimize() { }
            public bool OnConfirmClose() { return true; }
            public bool OnReceivedTSPublicKey(string publicKey) { return true; }
            public int OnAutoReconnecting(int disconnectReason, int attemptCount) { return 0; /* autoReconnectContinueAutomatic */ }
            public void OnAuthenticationWarningDisplayed() { RdpLog.Info("RdpClient.Event", "OnAuthenticationWarningDisplayed"); }
            public void OnAuthenticationWarningDismissed() { }
        }
    }
}
