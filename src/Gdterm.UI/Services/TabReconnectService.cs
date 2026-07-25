using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签重连协调——等待懒连接真正成功，避免 Watchdog 假成功（finding-07 / finding-10）。
    /// TabContainer 负责关签/开签；本类只处理凭据回填与连接就绪轮询。
    /// </summary>
    public sealed class TabReconnectService
    {
        public const int DefaultTimeoutSeconds = 20;
        public const int PollIntervalMs = 200;

        /// <summary>
        /// 在 OpenConnection 之后：回填凭据，强制终端连接并等待就绪。
        /// </summary>
        /// <param name="session">新打开的标签会话</param>
        /// <param name="credential">重连前缓存的凭据（可空）</param>
        /// <param name="onTerminalConnected">终端已连上时回调（通常 WireHealthAndReconnect）</param>
        /// <param name="timeoutSeconds">轮询上限秒数</param>
        /// <returns>是否确认连接成功</returns>
        public bool CompleteAfterOpen(
            TabSessionState session,
            CredentialPayload credential,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null) return false;

            if (credential != null)
            {
                session.Credential = credential;
                var tcCred = session.Control as TerminalControl;
                if (tcCred != null)
                    tcCred.Credentials = credential;
            }

            try
            {
                var tc = session.Control as TerminalControl;
                if (tc != null)
                {
                    return WaitForTerminalConnected(session, tc, onTerminalConnected, timeoutSeconds);
                }

                if (session.PendingConnect != null)
                {
                    var connect = session.PendingConnect;
                    session.PendingConnect = null;
                    connect();
                    return session.IsConnected;
                }
            }
            catch
            {
                return false;
            }

            // 非终端/非 RDP 延迟连接：仅表示标签已重建，不算连接成功
            return false;
        }

        /// <summary>强制 ResumeRendering 并轮询 IsConnected。</summary>
        public bool WaitForTerminalConnected(
            TabSessionState session,
            TerminalControl terminal,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null || terminal == null) return false;

            terminal.ResumeRendering();
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (terminal.IsConnected)
                {
                    session.IsConnected = true;
                    if (onTerminalConnected != null)
                        onTerminalConnected(session, terminal.Session);
                    return true;
                }
                System.Threading.Thread.Sleep(PollIntervalMs);
                Application.DoEvents();
            }

            if (terminal.IsConnected)
            {
                session.IsConnected = true;
                if (onTerminalConnected != null)
                    onTerminalConnected(session, terminal.Session);
                return true;
            }
            return false;
        }
    }
}
