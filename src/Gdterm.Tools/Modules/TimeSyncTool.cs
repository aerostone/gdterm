using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Gdterm.Core.Security;
using Gdterm.Tools.Models;

namespace Gdterm.Tools.Modules
{
    /// <summary>
    /// 时间同步工具——本地+远程NTP时间同步
    /// </summary>
    public class TimeSyncTool : IRemoteToolModule
    {
        private ISshRemoteSession _session;
        private TimeSyncConfig _config;

        public string ToolId { get { return "time-sync"; } }
        public string DisplayName { get { return "时间同步"; } }
        public string Description { get { return "本地和远程NTP时间同步"; } }
        public string Category { get { return "系统"; } }
        public bool HasRemoteSession { get { return _session != null && _session.IsConnected; } }

        public event EventHandler<string> OutputReceived;

        public TimeSyncTool()
        {
            _config = new TimeSyncConfig();
        }

        public void SetSshSession(ISshRemoteSession session) { _session = session; }
        public void ClearSshSession() { _session = null; }
        public void LoadConfig() { _config.LoadFromFile(); }
        public void SaveConfig() { _config.SaveToFile(); }

        /// <summary>同步本地时间</summary>
        public RemoteCommandResult SyncLocalTime(string ntpServer)
        {
            // SEC-01: NTP 服务器白名单
            try
            {
                ntpServer = ShellArgument.ValidateNtpServer(ntpServer);
            }
            catch (ArgumentException ex)
            {
                return new RemoteCommandResult { Command = "w32tm", ExitCode = -1, Stderr = ex.Message };
            }
            OnOutput("本地同步: " + ntpServer);
            // Windows: w32tm /resync /computer:NTP
            return ExecuteLocal("w32tm", "/resync /computer:" + ntpServer);
        }

        /// <summary>查询本地时间状态</summary>
        public RemoteCommandResult QueryLocalTime()
        {
            return ExecuteLocal("w32tm", "/query /status");
        }

        /// <summary>同步远程时间</summary>
        public RemoteCommandResult SyncRemoteTime(string ntpServer)
        {
            if (!HasRemoteSession)
                return new RemoteCommandResult { Command = "remote-time", ExitCode = -1, Stderr = "未连接远程SSH" };
            // SEC-01: NTP 服务器白名单 + shell 引号
            try
            {
                ntpServer = ShellArgument.ShellQuote(ShellArgument.ValidateNtpServer(ntpServer));
            }
            catch (ArgumentException ex)
            {
                return new RemoteCommandResult { Command = "remote-time", ExitCode = -1, Stderr = ex.Message };
            }

            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            int exitCode = -1;

            try
            {
                // 获取同步前时间
                var beforeResult = ExecuteRemote("date '+%Y-%m-%d %H:%M:%S' && timedatectl status 2>/dev/null | grep -i 'synchronized\\|NTP'");
                sb.AppendLine("=== 同步前 ===").AppendLine(beforeResult.Stdout);

                // 检测并执行时间同步——ntpServer 已用 ShellQuote 包裹为 POSIX 安全单引号
                var syncResult = ExecuteRemote(
                    "(chronyc -a 'burst 3/4' && chronyc -a makestep 2>/dev/null) || " +
                    "(ntpd -gq -p " + ntpServer + " 2>/dev/null && systemctl restart ntpd 2>/dev/null) || " +
                    "(ntpdate " + ntpServer + " 2>/dev/null) || " +
                    "(timedatectl set-ntp true 2>/dev/null)");
                exitCode = syncResult.ExitCode;

                // 获取同步后时间
                var afterResult = ExecuteRemote("date '+%Y-%m-%d %H:%M:%S'");
                sb.AppendLine("=== 同步后 ===").AppendLine(afterResult.Stdout);
                sb.AppendLine("=== NTP服务器 ===").AppendLine(ntpServer);

                OnOutput("远程时间同步" + (syncResult.ExitCode == 0 ? "成功" : "尝试完成"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message);
            }

            sw.Stop();
            return new RemoteCommandResult { Command = "remote-time-sync", ExitCode = exitCode, Stdout = sb.ToString(), Duration = sw.Elapsed };
        }

        /// <summary>查询远程时间状态</summary>
        public RemoteCommandResult QueryRemoteTime()
        {
            if (!HasRemoteSession)
                return new RemoteCommandResult { Command = "remote-time-query", ExitCode = -1, Stderr = "未连接远程SSH" };

            return ExecuteRemote("echo '=== 系统时间 ===' && date '+%Y-%m-%d %H:%M:%S %Z' && echo && echo '=== timedatectl ===' && timedatectl status 2>/dev/null && echo && echo '=== chrony ===' && chronyc tracking 2>/dev/null && echo && echo '=== ntpstat ===' && ntpstat 2>/dev/null");
        }

        private RemoteCommandResult ExecuteLocal(string fileName, string arguments)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEnd();
                    var stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    sw.Stop();
                    return new RemoteCommandResult { Command = fileName + " " + arguments, ExitCode = p.ExitCode, Stdout = stdout, Stderr = stderr, Duration = sw.Elapsed };
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new RemoteCommandResult { Command = fileName, ExitCode = -1, Stderr = ex.Message, Duration = sw.Elapsed };
            }
        }

        private RemoteCommandResult ExecuteRemote(string command)
        {
            if (_session == null)
                return new RemoteCommandResult { Command = command, ExitCode = -1, Stderr = "SSH 未连接" };
            return _session.RunCommand(command);
        }

        private void OnOutput(string msg) { OutputReceived?.Invoke(this, msg); }

        /// <summary>finding-05 共用后台执行骨架（同 NetworkScannerTool.RunBackground）。</summary>
        private static void RunBackground<T>(
            System.Windows.Forms.RichTextBox output,
            System.Windows.Forms.Label status,
            Func<T> work,
            Func<T, string> render)
        {
            var root = output.Parent as System.Windows.Forms.Control;
            Action setRunning = () => { status.Text = "执行中..."; status.ForeColor = System.Drawing.Color.FromArgb(255, 200, 80); };
            if (root != null && root.IsHandleCreated) root.BeginInvoke(setRunning); else setRunning();

            Task.Run(() =>
            {
                try
                {
                    var doneText = render(work());
                    SetStatus(root, status, doneText, System.Drawing.Color.FromArgb(120, 200, 120));
                }
                catch (Exception ex)
                {
                    ToolPanelHelper.AppendLine(output, "失败: " + ex.Message);
                    SetStatus(root, status, "失败: " + ex.Message, System.Drawing.Color.FromArgb(255, 100, 100));
                }
            });
        }

        private static void SetStatus(System.Windows.Forms.Control root, System.Windows.Forms.Label status, string text, System.Drawing.Color color)
        {
            Action ui = () => { status.Text = text; status.ForeColor = color; };
            if (root != null && root.IsHandleCreated) root.BeginInvoke(ui); else ui();
        }

        public System.Windows.Forms.Control CreatePanel()
        {
            return ToolPanelHelper.CreateActionPanel(
                DisplayName,
                "输入 NTP 服务器后点执行；空则用默认阿里云 NTP。远程需先绑定 SSH 会话。",
                null,
                (inputs, output, status) =>
                {
                    var ntp = string.IsNullOrWhiteSpace(inputs[0].Text) || inputs[0].Text.StartsWith("目标")
                        ? "ntp.aliyun.com" : inputs[0].Text.Trim();
                    // SEC-01: 入口处提前拒绝非法 NTP 服务器名
                    try { ShellArgument.ValidateNtpServer(ntp); }
                    catch (ArgumentException ex)
                    {
                        status.Text = "无效 NTP 服务器";
                        ToolPanelHelper.AppendLine(output, "错误: " + ex.Message);
                        return;
                    }
                    // finding-05：w32tm/远程 SSH 同步可能耗时，后台执行防 UI 冻结
                    RunBackground(output, status, () =>
                    {
                        var local = SyncLocalTime(ntp);
                        ToolPanelHelper.AppendLine(output, "[本地] exit=" + local.ExitCode + " " + local.Stdout + local.Stderr);
                        if (HasRemoteSession)
                        {
                            var remote = SyncRemoteTime(ntp);
                            ToolPanelHelper.AppendLine(output, "[远程] exit=" + remote.ExitCode + " " + remote.Stdout + remote.Stderr);
                        }
                        return true;
                    }, _ => "完成");
                });
        }

        public void Dispose() { _session = null; }
    }

    /// <summary>时间同步配置</summary>
    public class TimeSyncConfig : ToolConfigBase
    {
        public string[] NtpServers { get; set; }

        public override void ResetDefaults()
        {
            NtpServers = new[] { "ntp.aliyun.com", "cn.pool.ntp.org", "time.windows.com" };
        }

        protected override void LoadFromJson(string json)
        {
            NtpServers = ExtractStringList(json, "ntpServers").ToArray();
            if (NtpServers.Length == 0) ResetDefaults();
        }

        protected override string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"ntpServers\":[");
            for (int i = 0; i < NtpServers.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(NtpServers[i])).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
