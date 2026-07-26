using System;
using System.Reflection;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Rdp.Models;

namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 客户端——运行时反射加载 AxMsRdpClient8，编译期不依赖 AxMsTscLib DLL，
    /// 以便 AppVeyor / 无 ActiveX interop 的环境能编译；Windows 本机有 mstscax 时再连接。
    /// </summary>
    public class RdpClient : IRdpClient
    {
        private Control _rdpControl;
        private UserControl _container;
        private bool _disposed;
        private bool _isViaTunnel;
        private static readonly Type AxType = ResolveAxType();

        public bool IsConnected
        {
            get
            {
                if (_rdpControl == null) return false;
                try
                {
                    var v = GetProp(_rdpControl, "Connected");
                    if (v is short s) return s == 1;
                    if (v is int i) return i == 1;
                    if (v is bool b) return b;
                }
                catch { }
                return false;
            }
        }

        public UserControl Control => _container;

        public RdpOptions CurrentOptions { get; private set; }

        public event EventHandler<RdpStateChangedEventArgs> StateChanged;
        public event EventHandler<FileTransferEventArgs> FileTransferred;

        public RdpClient()
        {
            _container = new UserControl { Dock = DockStyle.Fill };

            try
            {
                if (AxType == null)
                    throw new InvalidOperationException("AxMsRdpClient8 type not found");

                _rdpControl = (Control)Activator.CreateInstance(AxType);
                var beginInit = _rdpControl as System.ComponentModel.ISupportInitialize;
                beginInit?.BeginInit();
                _rdpControl.Dock = DockStyle.Fill;
                _container.Controls.Add(_rdpControl);
                beginInit?.EndInit();

                TryHookEvent("OnConnected", nameof(OnRdpConnected));
                TryHookEvent("OnDisconnected", nameof(OnRdpDisconnected));
                TryHookEvent("OnLoginComplete", nameof(OnRdpLoginComplete));
            }
            catch
            {
                _rdpControl = null;
                var label = new Label
                {
                    Text = "RDP 控件不可用（需要 Windows 环境 + mstscax）",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter
                };
                _container.Controls.Add(label);
            }
        }

        public void Connect(ConnectionConfig config, CredentialPayload credential, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (_rdpControl == null) throw new InvalidOperationException("RDP 控件不可用");

            _isViaTunnel = false;
            CurrentOptions = options ?? new RdpOptions();
            ApplyOptions(config, credential);
            Invoke(_rdpControl, "Connect");
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (tunnelEndpoint == null) throw new ArgumentNullException(nameof(tunnelEndpoint));
            if (_rdpControl == null) throw new InvalidOperationException("RDP 控件不可用");

            _isViaTunnel = true;
            CurrentOptions = options ?? new RdpOptions();

            SetProp(_rdpControl, "Server", tunnelEndpoint.LocalHost ?? "127.0.0.1");
            SetProp(_rdpControl, "UserName", credential?.Username ?? config.Username ?? "");
            var adv = GetProp(_rdpControl, "AdvancedSettings7");
            if (adv != null)
            {
                if (!string.IsNullOrEmpty(credential?.Password))
                    SetProp(adv, "ClearTextPassword", credential.Password);
                SetProp(adv, "RDPPort", tunnelEndpoint.LocalPort);
            }
            if (!string.IsNullOrEmpty(config.Domain))
                SetProp(_rdpControl, "Domain", config.Domain);

            ApplyOptions(config, credential);
            Invoke(_rdpControl, "Connect");
        }

        public void Disconnect()
        {
            if (_rdpControl != null && IsConnected)
            {
                try { Invoke(_rdpControl, "Disconnect"); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            try { _rdpControl?.Dispose(); } catch { }
            try { _container?.Dispose(); } catch { }
        }

        private void ApplyOptions(ConnectionConfig config, CredentialPayload credential)
        {
            var opts = CurrentOptions;
            if (_rdpControl == null || opts == null) return;

            if (!_isViaTunnel)
            {
                SetProp(_rdpControl, "Server", config.Host);
                SetProp(_rdpControl, "UserName", credential?.Username ?? config.Username ?? "");
                var adv0 = GetProp(_rdpControl, "AdvancedSettings7");
                if (adv0 != null)
                {
                    if (!string.IsNullOrEmpty(credential?.Password))
                        SetProp(adv0, "ClearTextPassword", credential.Password);
                    if (config.Port > 0 && config.Port != 3389)
                        SetProp(adv0, "RDPPort", config.Port);
                }
                if (!string.IsNullOrEmpty(config.Domain))
                    SetProp(_rdpControl, "Domain", config.Domain);
            }

            var adv = GetProp(_rdpControl, "AdvancedSettings7");
            if (adv == null) return;

            SetProp(adv, "RedirectDrives", opts.RedirectDrives);
            SetProp(adv, "RedirectClipboard", opts.RedirectClipboard);
            SetProp(adv, "RedirectPrinters", opts.RedirectPrinters);
            SetProp(adv, "RedirectPorts", opts.RedirectPorts);
            SetProp(adv, "RedirectSmartCards", opts.RedirectSmartCards);
            SetProp(adv, "RedirectDevices", opts.RedirectDevices);
            SetProp(adv, "AudioRedirectionMode", (uint)opts.AudioMode);
            SetProp(_rdpControl, "ColorDepth", opts.ColorDepth);
            SetProp(adv, "UseMultimon", opts.UseMultimon);
            if (opts.FullScreen)
                SetProp(_rdpControl, "FullScreen", true);

            SetProp(adv, "BandwidthDetection", true);
            SetProp(adv, "EnableAutoReconnect", opts.AutoReconnectCount > 0);
            SetProp(adv, "MaxReconnectAttempts", opts.AutoReconnectCount);
            SetProp(adv, "EnableFontSmoothing", opts.EnableFontSmoothing);
            SetProp(adv, "EnableDesktopComposition", opts.EnableDesktopComposition);
            SetProp(adv, "singleConnectionTimeout", opts.ConnectionTimeout);
            SetProp(adv, "EnableNLA", opts.EnableNLA);
            if (opts.EnableCredSSP)
                SetProp(adv, "AuthenticationLevel", 2);
        }

        private void OnRdpConnected(object sender, EventArgs e)
        {
            OnStateChanged(new RdpStateChangedEventArgs(true, "connected"));
        }

        private void OnRdpDisconnected(object sender, EventArgs e)
        {
            int reason = 0;
            try
            {
                // COM 事件参数通常带 discReason
                if (e != null)
                {
                    var p = e.GetType().GetProperty("discReason")
                            ?? e.GetType().GetProperty("DiscReason");
                    if (p != null)
                        reason = Convert.ToInt32(p.GetValue(e, null));
                }
            }
            catch { }
            OnStateChanged(new RdpStateChangedEventArgs(false, "disconnected", GetDisconnectReasonMessage(reason)));
        }

        private void OnRdpLoginComplete(object sender, EventArgs e)
        {
            OnStateChanged(new RdpStateChangedEventArgs(true, "connected"));
        }

        private void OnStateChanged(RdpStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        private void TryHookEvent(string eventName, string handlerName)
        {
            if (_rdpControl == null) return;
            var evt = _rdpControl.GetType().GetEvent(eventName);
            if (evt == null) return;
            var method = GetType().GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) return;
            try
            {
                var handlerType = evt.EventHandlerType;
                // 尝试直接 EventHandler；COM 事件可能是自定义 delegate
                Delegate d;
                try
                {
                    d = Delegate.CreateDelegate(handlerType, this, method);
                }
                catch
                {
                    // 参数不匹配时包一层
                    d = CreateFlexibleHandler(handlerType, method);
                }
                if (d != null)
                    evt.AddEventHandler(_rdpControl, d);
            }
            catch { }
        }

        private Delegate CreateFlexibleHandler(Type handlerType, MethodInfo target)
        {
            // 仅支持 (object, EventArgs) 兼容包装
            var invoke = handlerType.GetMethod("Invoke");
            if (invoke == null) return null;
            var ps = invoke.GetParameters();
            if (ps.Length != 2) return null;

            // 用 DynamicMethod 太重；对常见 OnConnected/OnLoginComplete 用 EventHandler 适配
            EventHandler bridge = (s, e) =>
            {
                try { target.Invoke(this, new object[] { s, e ?? EventArgs.Empty }); }
                catch
                {
                    try { target.Invoke(this, new object[] { s, EventArgs.Empty }); }
                    catch { }
                }
            };

            try
            {
                return Delegate.CreateDelegate(handlerType, bridge.Target, bridge.Method);
            }
            catch
            {
                return null;
            }
        }

        private static Type ResolveAxType()
        {
            // 1) 已加载程序集
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType("AxMsTscLib.AxMsRdpClient8", false)
                            ?? asm.GetType("AxMSTSCLib.AxMsRdpClient8", false);
                    if (t != null) return t;
                }
                catch { }
            }

            // 2) 常见 interop DLL 旁路加载（绿色目录 / GAC 旁）
            var candidates = new[]
            {
                "AxMsTscLib.dll",
                "AxMSTSCLib.dll",
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "AxMsTscLib.dll"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "lib", "AxMsTscLib.dll"),
            };
            foreach (var path in candidates)
            {
                try
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var asm = Assembly.LoadFrom(path);
                    var t = asm.GetType("AxMsTscLib.AxMsRdpClient8")
                            ?? asm.GetType("AxMSTSCLib.AxMsRdpClient8");
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static object GetProp(object target, string name)
        {
            if (target == null) return null;
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return p?.GetValue(target, null);
        }

        private static void SetProp(object target, string name, object value)
        {
            if (target == null) return;
            try
            {
                var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (p == null || !p.CanWrite) return;
                var dest = p.PropertyType;
                if (value != null && !dest.IsInstanceOfType(value))
                {
                    if (dest.IsEnum)
                        value = Enum.ToObject(dest, value);
                    else
                        value = Convert.ChangeType(value, Nullable.GetUnderlyingType(dest) ?? dest);
                }
                p.SetValue(target, value, null);
            }
            catch { }
        }

        private static void Invoke(object target, string method)
        {
            if (target == null) return;
            var m = target.GetType().GetMethod(method, Type.EmptyTypes);
            m?.Invoke(target, null);
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
    }
}
