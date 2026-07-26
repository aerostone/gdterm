using System;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.Security;
using Gdterm.Security.Models;
using Gdterm.Terminal;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 锁屏/解锁协调：遮罩、KeePass 锁库/解锁、擦除会话明文凭据（finding-04）。
    /// </summary>
    public sealed class LockStateCoordinator
    {
        private readonly Control _uiSync;
        private readonly ISecurityManager _security;
        private readonly IKeePassService _keepass;
        private readonly LockOverlayControl _overlay;
        private readonly TabContainerControl _tabs;
        private readonly AutoReconnectWatchdog _watchdog;

        public LockStateCoordinator(
            Control uiSync,
            ISecurityManager security,
            IKeePassService keepass,
            LockOverlayControl overlay,
            TabContainerControl tabs = null,
            AutoReconnectWatchdog watchdog = null)
        {
            _uiSync = uiSync ?? throw new ArgumentNullException(nameof(uiSync));
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _keepass = keepass;
            _overlay = overlay;
            _tabs = tabs;
            _watchdog = watchdog;
        }

        public void Handle(object sender, LockStateChangedEventArgs e)
        {
            if (e == null) return;

            if (_uiSync.InvokeRequired)
            {
                try
                {
                    _uiSync.Invoke(new Action(() => Handle(sender, e)));
                }
                catch { }
                return;
            }

            if (e.IsLocked)
            {
                try { _keepass?.Lock(); } catch { }

                // finding-04：锁屏擦除会话缓存明文凭据，阻止无主密码重连
                try { _tabs?.ClearCachedCredentials(); } catch { }

                // 暂停自动重连，避免用已擦凭据重连
                try { _watchdog?.PauseAll(); } catch { }

                if (_overlay != null)
                {
                    _overlay.Visible = true;
                    _overlay.BringToFront();
                }
            }
            else
            {
                var masterPassword = _security.GetMasterPassword();
                if (!string.IsNullOrEmpty(masterPassword))
                {
                    try { _keepass?.UnlockAsync(masterPassword); } catch { }
                }

                try { _watchdog?.ResumeAll(); } catch { }

                if (_overlay != null)
                    _overlay.Visible = false;
            }
        }
    }
}
