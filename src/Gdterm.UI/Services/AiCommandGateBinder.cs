using System;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Terminal;
using Gdterm.UI.Controls;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.Security;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 将 AI「Run this」命令门控接到终端危险命令确认（finding-10 / finding-01 收口）。
    /// </summary>
    public sealed class AiCommandGateBinder
    {
        /// <summary>
        /// 绑定 AiAssistantService.CommandGate。非 AiAssistantService 时 no-op。
        /// </summary>
        public static void Bind(
            IAiAssistantService aiService,
            Func<TerminalControl> getActiveTerminal,
            DangerousCommandDetector detector,
            IWin32Window owner)
        {
            var aiSvc = aiService as AiAssistantService;
            if (aiSvc == null) return;

            aiSvc.CommandGate = cmd =>
            {
                var tc = getActiveTerminal != null ? getActiveTerminal() : null;
                if (tc != null)
                    return tc.ConfirmDangerousCommand(cmd);

                if (detector == null) return true;
                try
                {
                    var check = detector.Check(cmd);
                    if (check == null || !check.IsDangerous) return true;
                    using (var dlg = new DangerousCommandDialog(cmd, check))
                    {
                        dlg.ShowDialog(owner);
                        if (!dlg.IsConfirmed) return false;
                        if (dlg.RememberChoice)
                        {
                            try { detector.AddToWhitelist(cmd); } catch { }
                        }
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // finding-02：fail-closed
                    try
                    {
                        MessageBox.Show(
                            owner,
                            "危险命令检测失败，已阻止 AI 执行该命令。\n" + ex.Message,
                            "安全拦截",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    catch { }
                    return false;
                }
            };
        }
    }
}
