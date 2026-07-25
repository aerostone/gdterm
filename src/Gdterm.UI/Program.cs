using System;
using System.IO;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
using Gdterm.Connections;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Security.Models;
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

            // ====== 统一数据目录（可迁移） ======
            // 所有用户数据都在 data/ 目录下，整体拷贝即可迁移
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(appDir, "data");
            var configDir = Path.Combine(dataDir, "config");
            var logsDir = Path.Combine(dataDir, "logs");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(logsDir);

            // 数据文件路径
            var connectionsPath = Path.Combine(dataDir, "connections.json");
            var keepassPath = Path.Combine(dataDir, "gdterm.kdbx");
            var passwordConfigPath = Path.Combine(dataDir, "master-password.json");
            var bookmarksPath = Path.Combine(dataDir, "bookmarks.json");
            var recentPath = Path.Combine(dataDir, "recent-connections.json");
            var commandHistoryDir = Path.Combine(logsDir, "commands");
            var dangerousCmdPath = Path.Combine(configDir, "dangerous-commands.json");
            var folderCredPath = Path.Combine(dataDir, "folder-credentials.json");
            var sessionStatePath = Path.Combine(dataDir, "session-state.json");

            Directory.CreateDirectory(commandHistoryDir);

            // ====== 加载主密码配置 ======
            MasterPasswordConfig savedPasswordConfig = null;
            if (File.Exists(passwordConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(passwordConfigPath);
                    savedPasswordConfig = ParsePasswordConfig(json);
                }
                catch { /* 配置损坏，当作首次使用 */ }
            }

            bool isFirstRun = savedPasswordConfig == null;

            // ====== 初始化安全管理器 ======
            var securityManager = new SecurityManager(
                idleTimeout: TimeSpan.FromMinutes(10),  // 默认 10 分钟（最大 30 分钟）
                passwordConfig: savedPasswordConfig);

            // ====== 首次使用：强制设置主密码 ======
            if (isFirstRun)
            {
                using (var wizard = new SetupWizardForm(securityManager))
                {
                    if (wizard.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }
                }

                // 保存主密码配置
                SavePasswordConfig(securityManager.GetPasswordConfig(), passwordConfigPath);
            }

            // ====== 初始化各模块服务 ======
            var connectionStore = new ConnectionStoreJson(connectionsPath);
            var tunnelManager = new TunnelManager();
            var terminalFactory = new TerminalSessionFactory();
            var sftpFactory = new SftpServiceFactory();
            var keepassService = new KeePassService(keepassPath);
            var auditLogger = new AuditLogger(logsDir);
            var aiConfig = new AiConfiguration
            {
                ApiEndpoint = "https://api.openai.com/v1",
                Model = "gpt-4",
                MaxTokens = 2048,
                Temperature = 0.7
            };
            var aiService = new AiAssistantService(aiConfig);
            var dangerousCmdDetector = new DangerousCommandDetector(dangerousCmdPath);
            var folderCredStore = new FolderCredentialStoreJson(folderCredPath);
            var sessionStateStore = new SessionStateStore(sessionStatePath);

            // ====== 主窗口 ======
            var mainForm = new MainForm(
                connectionStore,
                tunnelManager,
                terminalFactory,
                sftpFactory,
                keepassService,
                auditLogger,
                aiService,
                securityManager,
                dangerousCmdDetector,
                folderCredStore,
                sessionStateStore);

            // 窗口关闭时保存主密码配置
            mainForm.FormClosed += (s, e) =>
            {
                SavePasswordConfig(securityManager.GetPasswordConfig(), passwordConfigPath);
            };

            Application.Run(mainForm);
        }

        /// <summary>
        /// 保存主密码配置到文件（哈希+盐，非明文密码）
        /// </summary>
        private static void SavePasswordConfig(MasterPasswordConfig config, string path)
        {
            if (config == null) return;

            try
            {
                var json = string.Format(
                    "{{\"passwordHash\":\"{0}\",\"salt\":\"{1}\",\"lastChanged\":\"{2:O}\"}}",
                    config.PasswordHash ?? "",
                    config.Salt ?? "",
                    config.LastChanged);
                File.WriteAllText(path, json);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// 从文件加载主密码配置
        /// </summary>
        private static MasterPasswordConfig ParsePasswordConfig(string json)
        {
            var hash = ExtractJsonString(json, "passwordHash");
            var salt = ExtractJsonString(json, "salt");
            var lastChangedStr = ExtractJsonString(json, "lastChanged");

            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
                return null;

            var config = new MasterPasswordConfig
            {
                PasswordHash = hash,
                Salt = salt
            };

            if (DateTime.TryParse(lastChangedStr, out var dt))
                config.LastChanged = dt;

            return config;
        }

        private static string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{key}\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            int end = start;
            while (end < json.Length)
            {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return json.Substring(start, end - start);
        }
    }
}
