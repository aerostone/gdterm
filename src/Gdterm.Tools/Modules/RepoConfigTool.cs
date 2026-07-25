using System;
using System.Diagnostics;
using System.Text;
using Gdterm.Tools.Models;
using Renci.SshNet;

namespace Gdterm.Tools.Modules
{
    /// <summary>
    /// 软件仓库配置工具——远程yum/apt/zypper仓库配置
    /// </summary>
    public class RepoConfigTool : IRemoteToolModule
    {
        private SshClient _ssh;

        public string ToolId { get { return "repo-config"; } }
        public string DisplayName { get { return "仓库配置"; } }
        public string Description { get { return "远程yum/apt/zypper软件仓库配置管理"; } }
        public string Category { get { return "系统"; } }
        public bool HasRemoteSession { get { return _ssh != null && _ssh.IsConnected; } }

        public event EventHandler<string> OutputReceived;

        public void SetSshSession(SshClient client) { _ssh = client; }
        public void ClearSshSession() { _ssh = null; }
        public void LoadConfig() { }
        public void SaveConfig() { }

        /// <summary>列出远程仓库</summary>
        public RemoteCommandResult ListRepos()
        {
            if (!HasRemoteSession)
                return new RemoteCommandResult { Command = "repo-list", ExitCode = -1, Stderr = "未连接远程SSH" };

            return ExecuteRemote(
                "echo '=== 检测包管理器 ===' && " +
                "(which yum 2>/dev/null && echo 'YUM:' && yum repolist all 2>/dev/null) || " +
                "(which dnf 2>/dev/null && echo 'DNF:' && dnf repolist all 2>/dev/null) || " +
                "(which apt 2>/dev/null && echo 'APT:' && apt-cache policy 2>/dev/null && echo && cat /etc/apt/sources.list 2>/dev/null && ls /etc/apt/sources.list.d/ 2>/dev/null) || " +
                "(which zypper 2>/dev/null && echo 'ZYPPER:' && zypper repos 2>/dev/null) || " +
                "echo '未检测到支持的包管理器'");
        }

        /// <summary>添加远程仓库</summary>
        public RemoteCommandResult AddRepo(string name, string url, bool enabled = true)
        {
            if (!HasRemoteSession)
                return new RemoteCommandResult { Command = "repo-add", ExitCode = -1, Stderr = "未连接远程SSH" };

            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            int exitCode = -1;

            try
            {
                // 备份现有配置
                var backupResult = ExecuteRemote(
                    "mkdir -p /tmp/gdterm_repo_backup && " +
                    "cp -r /etc/yum.repos.d/ /tmp/gdterm_repo_backup/ 2>/dev/null; " +
                    "cp /etc/apt/sources.list /tmp/gdterm_repo_backup/ 2>/dev/null; " +
                    "cp -r /etc/apt/sources.list.d/ /tmp/gdterm_repo_backup/ 2>/dev/null; " +
                    "echo '备份完成'");
                sb.AppendLine("备份: ").AppendLine(backupResult.Stdout);

                // 添加仓库
                string addCmd;
                if (url.Contains("yum") || url.Contains("rpm"))
                {
                    addCmd = string.Format(
                        "cat > /etc/yum.repos.d/{0}.repo << 'EOF'\n[{0}]\nname={0}\nbaseurl={1}\nenabled={2}\ngpgcheck=0\nEOF",
                        name, url, enabled ? "1" : "0");
                }
                else if (url.Contains("deb"))
                {
                    addCmd = string.Format("echo '{0}' >> /etc/apt/sources.list", url);
                }
                else
                {
                    addCmd = string.Format(
                        "cat > /etc/yum.repos.d/{0}.repo << 'EOF'\n[{0}]\nname={0}\nbaseurl={1}\nenabled={2}\ngpgcheck=0\nEOF",
                        name, url, enabled ? "1" : "0");
                }

                var addResult = ExecuteRemote(addCmd);
                exitCode = addResult.ExitCode;
                sb.AppendLine("添加结果: ").AppendLine(addResult.Stdout);

                // 刷新缓存
                var refreshResult = ExecuteRemote("yum clean all 2>/dev/null; dnf clean all 2>/dev/null; apt update 2>/dev/null; zypper refresh 2>/dev/null");
                sb.AppendLine("刷新缓存: ").AppendLine(refreshResult.Stdout);

                OnOutput("添加仓库: " + name);
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message);
            }

            sw.Stop();
            return new RemoteCommandResult { Command = "repo-add", ExitCode = exitCode, Stdout = sb.ToString(), Duration = sw.Elapsed };
        }

        /// <summary>删除远程仓库</summary>
        public RemoteCommandResult RemoveRepo(string name)
        {
            if (!HasRemoteSession)
                return new RemoteCommandResult { Command = "repo-remove", ExitCode = -1, Stderr = "未连接远程SSH" };

            var result = ExecuteRemote(string.Format(
                "rm -f /etc/yum.repos.d/{0}.repo 2>/dev/null; " +
                "sed -i '/{0}/d' /etc/apt/sources.list 2>/dev/null; " +
                "rm -f /etc/apt/sources.list.d/{0}* 2>/dev/null; " +
                "yum clean all 2>/dev/null; apt update 2>/dev/null; echo 'done'",
                name));

            OnOutput("删除仓库: " + name);
            return result;
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
        public System.Windows.Forms.Control CreatePanel() { return null; }
        public void Dispose() { _ssh = null; }
    }
}
