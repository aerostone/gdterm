using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using Gdterm.Core;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// “进行中”统一语义（UI/UX 审计 2026-10-07 C2）。
    ///
    /// 背景：全仓 0 处 <c>Cursors.WaitCursor</c>；忙碌表达靠各写各的（私有 bool /
    /// <c>Enabled=false</c> / 干脆同步阻塞 UI 线程）。用户点击后没有立即反馈 →
    /// 二次点击 → 要么重复提交，要么被 <c>if (_busy) return;</c> 静默丢弃。
    ///
    /// 契约（进入即生效，Dispose 必复位，含异常路径）：
    ///   1. 光标 = WaitCursor（最省事、最普适的“我在干活”信号）；
    ///   2. 指定的主操作控件 Enabled=false（防重入）；
    ///   3. 提供 CancellationToken 供取消（面板关闭/用户点停止）；
    ///   4. 重入时给出可见反馈（光标 Cursors.No 闪 400ms + tooltip），而不是静默 return。
    ///
    /// 用法：
    /// <code>
    /// using (var busy = BusyScope.Begin(this, "正在分析密码健康…", true, _btnScan))
    /// {
    ///     var report = await Task.Run(() => _service.Analyze(), busy.Token);
    /// }
    /// </code>
    /// 注意：必须在 UI 线程 Dispose（WinForms 控件访问要求）；await 后回到 UI 上下文即满足。
    /// </summary>
    public sealed class BusyScope : IDisposable
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, BusyScope> Active =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Control, BusyScope>();

        private readonly Control _owner;
        private readonly Cursor _prevCursor;
        private readonly List<Control> _disabled;
        private readonly CancellationTokenSource _cts;
        private readonly string _prevA11yDescription;
        private bool _disposed;

        /// <summary>
        /// 本作用域的用户可见文案（<c>Begin(owner, status:…)</c> 的入参）。
        /// 背景（L2/状态轴补测 2026-11-13）：<c>status</c> 此前是个<b>死参数</b>——7 个调用点
        /// 都传了“正在读取…”，但构造器把它丢了，用户看不到任何进度文案，只剩光标变沙漏。
        /// 现落到两处可观测的地方：AntdUI Tooltip（悬停可见）+ AccessibleDescription（读屏可见），
        /// 两者都不改布局（忙碌态不该引起重排），且能被冒烟断言读到。
        /// </summary>
        public string Status { get; private set; }

        /// <summary>重入时返回的空操作作用域（AlreadyBusy=true）。调用方可用它提前 return。</summary>
        public bool AlreadyBusy { get; private set; }

        /// <summary>取消令牌（非 cancellable 时也是有效的，只是不会有人 Cancel）。</summary>
        public CancellationToken Token
        {
            get { return _cts != null ? _cts.Token : CancellationToken.None; }
        }

        private BusyScope(Control owner, bool alreadyBusy, string status = null)
        {
            _owner = owner;
            AlreadyBusy = alreadyBusy;
            _disabled = new List<Control>();
            if (!alreadyBusy)
            {
                _cts = new CancellationTokenSource();
                Status = status;
                if (owner != null)
                {
                    _prevCursor = owner.Cursor;
                    try { owner.Cursor = Cursors.WaitCursor; } catch { }
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        // 状态文案必须真的可见（读屏 + 悬停），否则“正在读取…”就是死参数。
                        try { _prevA11yDescription = owner.AccessibleDescription; } catch { }
                        try { owner.AccessibleDescription = status; } catch { }
                        try { owner.ToolTipText2(status); } catch { }
                    }
                }
            }
        }

        /// <summary>宿主当前忙碌文案（无进行中的操作时为 null）——冒烟断言用。</summary>
        public static string StatusOf(Control owner)
        {
            if (owner == null) return null;
            BusyScope s;
            if (Active.TryGetValue(owner, out s) && s != null && !s._disposed) return s.Status;
            return null;
        }

        /// <summary>
        /// 开始一个忙碌作用域。<paramref name="owner"/> 是光标宿主（通常是面板/窗体），
        /// 同时作为重入判定键；<paramref name="disable"/> 是要临时禁用的主操作控件。
        /// </summary>
        public static BusyScope Begin(Control owner, string status = null, bool cancellable = false,
            params Control[] disable)
        {
            if (owner == null) return new BusyScope(null, false);

            BusyScope existing;
            if (Active.TryGetValue(owner, out existing) && existing != null && !existing._disposed)
            {
                // 重入：给出可见反馈后返回空作用域（AlreadyBusy=true）
                FlashBusy(owner);
                return new BusyScope(owner, true);
            }

            var scope = new BusyScope(owner, false, status);
            Active.Remove(owner);
            Active.Add(owner, scope);

            if (disable != null)
            {
                foreach (var c in disable)
                {
                    if (c == null || !c.Enabled) continue;
                    try { c.Enabled = false; scope._disabled.Add(c); } catch { }
                }
            }
            return scope;
        }

        /// <summary>主动取消（用户点“停止”/面板关闭时调用）。</summary>
        public void Cancel()
        {
            try { if (_cts != null && !_cts.IsCancellationRequested) _cts.Cancel(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (AlreadyBusy)
            {
                try { if (_cts != null) _cts.Dispose(); } catch { }
                return;
            }

            if (_owner != null)
            {
                try { Active.Remove(_owner); } catch { }
                try { if (!_owner.IsDisposed) _owner.Cursor = _prevCursor; } catch { }
            }
            for (int i = 0; i < _disabled.Count; i++)
            {
                try { if (!_disabled[i].IsDisposed) _disabled[i].Enabled = true; } catch { }
            }
            _disabled.Clear();
            // 状态文案随作用域结束一起收回（无障碍描述是“当前正在做什么”，不是常驻说明）
            if (_owner != null && !string.IsNullOrWhiteSpace(Status))
            {
                try { if (!_owner.IsDisposed) _owner.AccessibleDescription = _prevA11yDescription; } catch { }
            }
            try { if (_cts != null) _cts.Dispose(); } catch { }
        }

        /// <summary>
        /// 重入时的可见反馈（光标闪 No + tooltip），供仍在用私有 bool 守卫的旧代码接入，
        /// 不必先把整个流程迁到 <see cref="Begin"/> 就能去掉“静默 return”。
        /// </summary>
        public static void Nudge(Control owner, string text = null)
        {
            if (owner == null) return;
            try
            {
                var prev = owner.Cursor;
                owner.Cursor = Cursors.No;
                if (!string.IsNullOrEmpty(text)) { try { owner.ToolTipText2(text); } catch { } }
                var ctx = SynchronizationContext.Current;
                var timer = new System.Threading.Tasks.Task(() =>
                {
                    Thread.Sleep(400);
                    Action restore = () => { try { if (!owner.IsDisposed) owner.Cursor = prev; } catch { } };
                    try
                    {
                        if (ctx != null) ctx.Post(_ => restore(), null);
                        else restore();
                    }
                    catch { }
                });
                timer.Start();
            }
            catch { }
        }

        /// <summary>该宿主上是否已有进行中的操作。</summary>
        public static bool IsBusy(Control owner)
        {
            if (owner == null) return false;
            BusyScope s;
            return Active.TryGetValue(owner, out s) && s != null && !s._disposed;
        }

        /// <summary>重入可见反馈：光标变 No 并 400ms 后复位 + 一句 tooltip（不弹窗打断）。</summary>
        private static void FlashBusy(Control owner)
        {
            try
            {
                var prev = owner.Cursor;
                owner.Cursor = Cursors.No;
                try { owner.ToolTipText2(L10n.T("上一次操作还在进行，请稍候…")); } catch { }
                var ctx = SynchronizationContext.Current;
                var timer = new System.Threading.Tasks.Task(() =>
                {
                    Thread.Sleep(400);
                    Action restore = () =>
                    {
                        try { if (!owner.IsDisposed) owner.Cursor = prev; } catch { }
                    };
                    try
                    {
                        if (ctx != null) ctx.Post(_ => restore(), null);
                        else restore();
                    }
                    catch { }
                });
                timer.Start();
            }
            catch { }
        }
    }
}
