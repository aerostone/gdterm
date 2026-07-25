using System;
using System.IO;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
using Gdterm.Connections;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.UI.Forms;

namespace Gdterm.UI
{
    static class Program
    {
        /// <summary>
        /// 应用程序入口点
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 获取应用目录
            var appDir = AppDomain.CurrentDomain.BaseDirectory;

            // 初始化各模块服务
            var connectionStore = new ConnectionStoreJson(Path.Combine(appDir, "connections.json"));
            var tunnelManager = new TunnelManager();
            var terminalFactory = new TerminalSessionFactory();
            var sftpFactory = new SftpServiceFactory();
            var keepassService = new KeePassService(Path.Combine(appDir, "gdterm.kdbx"));
            var auditLogger = new AuditLogger(Path.Combine(appDir, "logs"));
            var aiConfig = new AiConfiguration
            {
                ApiEndpoint = "https://api.openai.com/v1",
                Model = "gpt-4",
                MaxTokens = 2048,
                Temperature = 0.7
            };
            var aiService = new AiAssistantService(aiConfig);
            var securityManager = new SecurityManager(TimeSpan.FromMinutes(5));
            var dangerousCmdDetector = new DangerousCommandDetector(
                Path.Combine(appDir, "config", "dangerous-commands.json"));

            // 创建主窗口
            var mainForm = new MainForm(
                connectionStore,
                tunnelManager,
                terminalFactory,
                sftpFactory,
                keepassService,
                auditLogger,
                aiService,
                securityManager,
                dangerousCmdDetector);

            Application.Run(mainForm);
        }
    }
}
