using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
using Gdterm.Connections;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.UI.Forms;
using Gdterm.Rdp;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 主窗冒烟：用临时目录构造 MainForm 全依赖（不碰真实 data/），Show 后 dump 整棵树。
    /// 锁定时跳过会话恢复（_sessionRestored 逻辑不变）；只验 layout，不连任何主机。
    /// </summary>
    public static class MainFormSmoke
    {
        public static void Run(string outDir, Action<string> log, Action<bool, string> check)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gdterm-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var connectionStore = new ConnectionStoreJson(Path.Combine(tmp, "connections.json"));
            var tunnelManager = new TunnelManager();
            var terminalFactory = new TerminalSessionFactory();
            var sftpFactory = new SftpServiceFactory();
            var keepassService = new FakeKeePassService();
            var auditLogger = new AuditLogger(Path.Combine(tmp, "logs"));
            var aiService = new AiAssistantService(new AiConfiguration());
            var securityManager = new SecurityManager();
            try { securityManager.Unlock("smoke"); } catch { }
            var dangerousCmdDetector = new DangerousCommandDetector(Path.Combine(tmp, "dangerous.json"));
            var folderCredStore = new FolderCredentialStoreJson(Path.Combine(tmp, "folder.json"));
            var sessionStore = new SessionStateStore(Path.Combine(tmp, "session.json"));
            var rdpFactory = new RdpClientFactory();
            var bookmarkStore = new BookmarkStoreJson(tmp);
            var commandHistoryStore = new CommandHistoryStore(Path.Combine(tmp, "cmdhist"));
            var quickCommandStore = new QuickCommandStore(Path.Combine(tmp, "quick.json"));
            var keyBindingStore = new TerminalKeyBindingStore(Path.Combine(tmp, "keybind.json"));
            var highlightStore = new HighlightStore(Path.Combine(tmp, "highlight.json"));
            var reconnectWatchdog = new AutoReconnectWatchdog { MaxRetries = 0 };
            var multiChannelManager = new MultiChannelManager();
            var toolRegistry = new Gdterm.Tools.ToolRegistry();
            var secretScanner = new SecretScanner(SecretScanConfig.GetDefault());

            log("构造 MainForm...");
            using (var f = new MainForm(
                connectionStore, tunnelManager, terminalFactory, sftpFactory,
                keepassService, auditLogger, aiService, securityManager,
                dangerousCmdDetector, folderCredStore, sessionStore, rdpFactory,
                bookmarkStore, commandHistoryStore, quickCommandStore,
                keyBindingStore, highlightStore, reconnectWatchdog,
                multiChannelManager, toolRegistry, secretScanner))
            {
                log("构造完成，Show...");
                f.Show();
                f.BringToFront();
                f.Activate();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                Thread.Sleep(600);
                Application.DoEvents();

                log("Show 完成，开始断言...");
                // 主窗 layout 断言：菜单/树/Tab/底栏/状态齐备
                int count = CountControls(f);
                check(count > 40, "主窗控件总数=" + count + " (>40)");
                check(FindByType(f, "ConnectionTreeControl") != null, "连接树可定位");
                check(FindByType(f, "TabContainerControl") != null, "Tab容器可定位");
                check(FindByType(f, "BottomBarPanel") != null, "底栏可定位");
                check(FindByType(f, "StatusBarControl") != null || FindByName(f, "BottomBarPanel") != null, "状态条可定位");
                check(f.MainMenuStrip != null, "主菜单可定位");
                log("dump: mainform.json");
                File.WriteAllText(Path.Combine(outDir, "mainform.json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
            }

            try { Directory.Delete(tmp, true); } catch { }
            try { securityManager.Dispose(); } catch { }
            try { auditLogger.Dispose(); } catch { }
            try { reconnectWatchdog.Dispose(); } catch { }
            try { multiChannelManager.Dispose(); } catch { }
            try { toolRegistry.Dispose(); } catch { }
            try { secretScanner.Dispose(); } catch { }
            try { commandHistoryStore.Dispose(); } catch { }
            try { dangerousCmdDetector.Dispose(); } catch { }
            try { aiService.Dispose(); } catch { }
        }

        private static int CountControls(Control root)
        {
            int n = 1;
            foreach (Control c in root.Controls) n += CountControls(c);
            return n;
        }

        private static Control FindByType(Control root, string typeName)
        {
            if (root.GetType().Name == typeName) return root;
            foreach (Control c in root.Controls)
            {
                var hit = FindByType(c, typeName);
                if (hit != null) return hit;
            }
            return null;
        }

        private static Control FindByName(Control root, string name)
        {
            if (root.Name == name) return root;
            foreach (Control c in root.Controls)
            {
                var hit = FindByName(c, name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
