using System;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Terminal;
using Gdterm.UI.Controls;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.Security;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

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
            CommandGate gate,
            IWin32Window owner)
        {
            var aiSvc = aiService as AiAssistantService;
            if (aiSvc == null) return;

            aiSvc.CommandGate = cmd =>
            {
                // 活动终端存在就用它的闸门（同一实例，会话豁免一致）；否则用组合根的共享闸门
                var tc = getActiveTerminal != null ? getActiveTerminal() : null;
                var effective = tc != null && tc.Gate != null ? tc.Gate : gate;
                if (effective == null) return true;

                var host = tc != null ? tc.HostForAudit : "ai";
                var audit = tc != null ? tc.Audit : null;
                // fail-closed 由 DangerousCommandPrompt 统一保证（判定异常/弹窗失败都不放行）
                return DangerousCommandPrompt.Confirm(owner, effective, cmd, InputSource.Ai, audit, host);
            };
        }
    }
}
