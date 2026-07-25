using System;
using System.Diagnostics;
using System.Text;
using Gdterm.Tools.Models;
using Renci.SshNet;

namespace Gdterm.Tools.Modules
{
    /// <summary>
    /// 时间同步工具——本地+远程NTP时间同步
    /// </summary>
    public class TimeSyncTool : IRemoteToolModule
    {
        private SshClient _ssh;
        private TimeSyncConfig _config;

        public string ToolId { get { return "time-sync"; } }
        public string DisplayName { get { return "时间同步"; } }
        public string Description { get { return "本地和远程NTP时间同步"; } }
        public string Category { get { return "系统"; } }
        public bool HasRemoteSession { get { return _ssh != null && _ssh.IsConnected; } }

        public event EventHandler<string> OutputReceived;

        public TimeSyncTool()
        {
            _config = new TimeSyncConfig();
        }

        public void SetSshSession(SshClient client) { _ssh = client; }
        public void ClearSshSession() { _ssh = null; }
        public void LoadConfig() { _config.LoadFromFile(); }
        public void SaveConfig() { _config.SaveToFile(); }

        /// <summary>同步本地时间</summary>
        public RemoteCommandResult SyncLocalTime(string ntpServer)
        {
            OnOutput("本地同步: " + ntpServer);
            // Windows: w32tm /resync
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

            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            int exitCode = -1;

            try
            {
                // 获取同步前时间
                var beforeResult = ExecuteRemote("date '+%Y-%m-%d %H:%M:%S' && timedatectl status 2>/dev/null | grep -i 'synchronized\\|NTP'");
                sb.AppendLine("=== 同步前 ===").AppendLine(beforeResult.Stdout);

                // 检测并执行时间同步
                var syncResult = ExecuteRemote(string.Format(
                    "(chronyc -a 'burst 3/4' && chronyc -a makestep 2>/dev/null) || " +
                    "(ntpd -gq -p {0} 2>/dev/null && systemctl restart ntpd 2>/dev/null) || " +
                    "(ntpdate {0} 2>/dev/null) || " +
                    "(timedatectl set-ntp true 2>/dev/null)",
                    ntpServer));
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
            var sw = Stopwatch.StartNew();
            try
            {
                var cmd = _ssh.RunCommand(command);
                sw.Stop();
                return new RemoteCommandResult { Command = command, ExitCode = cmd.ExitStatus, Stdout = cmd.Result, Stderr = cmd.Error, Duration = sw.Elapsed };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new RemoteCommandResult { Command = command, ExitCode = -1, Stderr = ex.Message, Duration = sw.Elapsed };
            }
        }

        private void OnOutput(string msg) { OutputReceived?.Invoke(this, msg); }

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
                    var local = SyncLocalTime(ntp);
                    ToolPanelHelper.AppendLine(output, "[本地] exit=" + local.ExitCode + " " + local.Stdout + local.Stderr);
                    if (HasRemoteSession)
                    {
                        var remote = SyncRemoteTime(ntp);
                        ToolPanelHelper.AppendLine(output, "[远程] exit=" + remote.ExitCode + " " + remote.Stdout + remote.Stderr);
                    }
                    status.Text = "完成";
                });
        }

        public void Dispose() { _ssh = null; }
    }

    /// <summary>时间同步配置</summary>
    public class TimeSyncConfig : ToolConfigBase
    {
        public string[] NtpServers { get; set; }

        protected override void ResetDefaults()
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
