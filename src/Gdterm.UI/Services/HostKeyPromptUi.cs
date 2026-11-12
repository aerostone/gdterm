using System;
using System.Windows.Forms;
using Gdterm.Core.Security;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 主机密钥确认的 UI 实现（唯一）：把后台 SSH 线程上的询问搬回 UI 线程。
    ///
    /// 生命周期：组合根在 MainForm 创建后调用 <see cref="Attach"/>；之后隧道/SFTP/采样线程
    /// 触发 HostKeyReceived 时经 <see cref="Ask"/> 弹窗。
    ///
    /// 无 owner / 无 UI 线程（例如启动期、服务化使用）= Reject（fail-closed），
    /// 绝不"问不到就放行"。
    /// </summary>
    public sealed class HostKeyPromptUi : IHostKeyPrompt
    {
        private static Form _owner;

        /// <summary>绑定弹窗归属窗口（可重复调用，后一次覆盖）。</summary>
        public static void Attach(Form owner)
        {
            _owner = owner;
        }

        /// <inheritdoc />
        public HostKeyDecision Ask(HostKeyPromptInfo info)
        {
            if (info == null) return HostKeyDecision.Reject;
            var owner = ResolveOwner();
            if (owner == null) return HostKeyDecision.Reject;

            try
            {
                if (owner.IsHandleCreated && owner.InvokeRequired)
                {
                    var result = HostKeyDecision.Reject;
                    owner.Invoke(new Action(() => { result = AskOnUiThread(owner, info); }));
                    return result;
                }
                return AskOnUiThread(owner, info);
            }
            catch (Exception ex)
            {
                // 窗口正在销毁/句柄已释放：问不到就不放行
                try { Diagnostics.DiagLog.Swallowed("HostKeyPromptUi.Ask", ex); } catch { }
                return HostKeyDecision.Reject;
            }
        }

        private static HostKeyDecision AskOnUiThread(Form owner, HostKeyPromptInfo info)
        {
            try
            {
                using (var dlg = new HostKeyDialog(info))
                {
                    dlg.ShowDialog(owner);
                    return dlg.Decision;
                }
            }
            catch (Exception ex)
            {
                try { Diagnostics.DiagLog.Swallowed("HostKeyPromptUi.AskOnUiThread", ex); } catch { }
                return HostKeyDecision.Reject;
            }
        }

        private static Form ResolveOwner()
        {
            var owner = _owner;
            if (owner != null && owner.IsHandleCreated && !owner.IsDisposed) return owner;
            // 兜底：主窗体尚未 Attach 时（极少见）用第一个可见窗体
            for (int i = 0; i < Application.OpenForms.Count; i++)
            {
                var f = Application.OpenForms[i];
                if (f != null && f.IsHandleCreated && !f.IsDisposed) return f;
            }
            return null;
        }
    }
}
