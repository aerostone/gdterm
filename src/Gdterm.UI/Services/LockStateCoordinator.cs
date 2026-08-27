using System;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Security;
using Gdterm.Security.Models;
using Gdterm.Terminal;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 锁屏/解锁协调：遮罩、KeePass 锁库/解锁、擦除会话明文凭据、审计（P1-08）、重武装健康监控（P1-03）。
    /// </summary>
    public sealed class LockStateCoordinator
    {
        private readonly Control _uiSync;
        private readonly ISecurityManager _security;
        private readonly IKeePassService _keepass;
        private readonly LockOverlayControl _overlay;
        private readonly TabContainerControl _tabs;
        private readonly AutoReconnectWatchdog _watchdog;
        private readonly IAuditLogger _audit;
        private readonly Control[] _lockableStrips;

        public LockStateCoordinator(
            Control uiSync,
            ISecurityManager security,
            IKeePassService keepass,
            LockOverlayControl overlay,
            TabContainerControl tabs = null,
            AutoReconnectWatchdog watchdog = null,
            IAuditLogger audit = null,
            params Control[] lockableStrips)
        {
            _uiSync = uiSync ?? throw new ArgumentNullException(nameof(uiSync));
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _keepass = keepass;
            _overlay = overlay;
            _tabs = tabs;
            _watchdog = watchdog;
            _audit = audit;
            _lockableStrips = lockableStrips ?? new Control[0];
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
                catch (Exception ex) { DiagLog.Swallowed("LockState.Invoke", ex); }
                return;
            }

            if (e.IsLocked)
            {
                SetStripsEnabled(false);

                DiagLog.Try("LockState.KeePassLock", () => _keepass?.Lock());
                DiagLog.Try("LockState.ClearCreds", () => _tabs?.ClearCachedCredentials());
                DiagLog.Try("LockState.PauseWatchdog", () => _watchdog?.PauseAll());

                try
                {
                    _audit?.LogSecurityEvent(SecurityEvent.IdleLock,
                        "reason=" + (e.Reason ?? "lock"));
                }
                catch (Exception ex) { DiagLog.Swallowed("LockState.AuditLock", ex); }

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
                    DiagLog.Try("LockState.KeePassUnlock", () =>
                    {
                        try { _keepass?.UnlockAsync(masterPassword); } catch (Exception ex) { DiagLog.Swallowed("LockState.UnlockAsync", ex); }
                    });
                }

                DiagLog.Try("LockState.ResumeWatchdog", () => _watchdog?.ResumeAll());
                // P1-03：解锁后重新武装所有健康监控，避免暂停期间 lost 边沿丢失
                DiagLog.Try("LockState.RearmHealth", () => _tabs?.RearmAllHealthMonitors());

                try
                {
                    _audit?.LogSecurityEvent(SecurityEvent.Unlock,
                        "reason=" + (e.Reason ?? "unlock"));
                }
                catch (Exception ex) { DiagLog.Swallowed("LockState.AuditUnlock", ex); }

                if (_overlay != null)
                    _overlay.Visible = false;

                SetStripsEnabled(true);
            }
        }

        /// <summary>锁定态禁用菜单栏/状态栏（遮罩只盖 ClientArea，ToolStrip 顶栏需单独禁用）。</summary>
        private void SetStripsEnabled(bool enabled)
        {
            foreach (var strip in _lockableStrips)
            {
                if (strip == null || strip.IsDisposed) continue;
                try { strip.Enabled = enabled; } catch (Exception ex) { DiagLog.Swallowed("LockState.Strip", ex); }
            }
        }
    }
}
