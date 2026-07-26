using System;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.Security;
using Gdterm.Security.Models;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 锁屏/解锁协调：遮罩、KeePass 锁库/解锁（finding-10）。
    /// </summary>
    public sealed class LockStateCoordinator
    {
        private readonly Control _uiSync;
        private readonly ISecurityManager _security;
        private readonly IKeePassService _keepass;
        private readonly LockOverlayControl _overlay;

        public LockStateCoordinator(
            Control uiSync,
            ISecurityManager security,
            IKeePassService keepass,
            LockOverlayControl overlay)
        {
            _uiSync = uiSync ?? throw new ArgumentNullException(nameof(uiSync));
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _keepass = keepass;
            _overlay = overlay;
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
                if (_overlay != null)
                    _overlay.Visible = false;
            }
        }
    }
}
