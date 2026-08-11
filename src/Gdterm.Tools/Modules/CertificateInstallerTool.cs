using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Core.Security;
using Gdterm.Tools.Models;

namespace Gdterm.Tools.Modules
{
    /// <summary>
    /// 证书安装工具——本地+远程证书安装（PEM/DER/PFX/P12）
    /// </summary>
    public class CertificateInstallerTool : IRemoteToolModule
    {
        private ISshRemoteSession _session;
        private CertificateInstallerConfig _config;

        public string ToolId { get { return "cert-installer"; } }
        public string DisplayName { get { return "证书安装器"; } }
        public string Description { get { return "本地和远程证书安装（PEM/DER/PFX/P12）"; } }
        public string Category { get { return "安全"; } }
        public bool HasRemoteSession { get { return _session != null && _session.IsConnected; } }

        public event EventHandler<string> OutputReceived;

        public CertificateInstallerTool()
        {
            _config = new CertificateInstallerConfig();
        }

        public void SetSshSession(ISshRemoteSession session) { _session = session; }
        public void ClearSshSession() { _session = null; }
        public void LoadConfig() { _config.LoadFromFile(); }
        public void SaveConfig() { _config.SaveToFile(); }

        /// <summary>安装本地证书</summary>
        public RemoteCommandResult InstallLocal(string certPath, bool isTrustedRoot)
        {
            // SEC-01: 本地路径走 Windows 进程，certutil 本身不解析 shell 元字符，但仍校验避免意外执行
            try
            {
                ShellArgument.ValidateLocalPath(certPath, "certPath");
            }
            catch (ArgumentException ex)
            {
                return new RemoteCommandResult { Command = "local-certutil", ExitCode = -1, Stderr = ex.Message };
            }
            if (!File.Exists(certPath))
                return new RemoteCommandResult { Command = "local-certutil", ExitCode = -1, Stderr = "证书文件不存在: " + certPath };

            var store = isTrustedRoot ? "Root" : "My";
            // 双引号包裹 Windows 路径，内部双引号转义为 ""
            var safePath = "\"" + certPath.Replace("\"", "\"\"") + "\"";
            var safeStore = "\"" + store + "\"";
            var args = "-addstore " + safeStore + " " + safePath;
            var result = ExecuteLocal("certutil", args);
            OnOutput("本地证书安装" + (result.IsSuccess ? "成功" : "失败") + ": " + certPath);
            return result;
        }

        /// <summary>安装远程证书（Linux）</summary>
        public RemoteCommandResult InstallRemote(string localCertPath, bool isTrustedRoot)
        {
            if (!HasRemoteSession)
                return new RemoteCommandResult { Command = "remote-cert", ExitCode = -1, Stderr = "未连接远程SSH" };

            if (!File.Exists(localCertPath))
                return new RemoteCommandResult { Command = "remote-cert", ExitCode = -1, Stderr = "本地证书文件不存在" };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sb = new StringBuilder();
            int exitCode = -1;

            try
            {
                using (var transfer = _session.CreateFileTransfer())
                {
                    var remotePath = transfer.UploadToTemp(localCertPath);
                    try
                    {
                        // 检测系统类型并安装
                        var osResult = ExecuteRemote("cat /etc/os-release 2>/dev/null || cat /etc/redhat-release 2>/dev/null");
                        var osInfo = osResult.Stdout ?? "";

                        string installCmd;
                        // SEC-01: remotePath 与 dest 全部走 ShellArgument.ShellQuote——避免临时文件路径含空格/特殊字符触发注入
                        if (osInfo.Contains("Ubuntu") || osInfo.Contains("Debian"))
                        {
                            var dest = isTrustedRoot
                                ? "/usr/local/share/ca-certificates/" + Path.GetFileName(localCertPath)
                                : "/etc/ssl/certs/" + Path.GetFileName(localCertPath);
                            installCmd = "sudo cp " + ShellArgument.ShellQuote(remotePath) + " " + ShellArgument.ShellQuote(dest) + " && sudo update-ca-certificates";
                        }
                        else if (osInfo.Contains("CentOS") || osInfo.Contains("Red Hat") || osInfo.Contains("RHEL"))
                        {
                            var dest = isTrustedRoot
                                ? "/etc/pki/ca-trust/source/anchors/" + Path.GetFileName(localCertPath)
                                : "/etc/pki/tls/certs/" + Path.GetFileName(localCertPath);
                            installCmd = "sudo cp " + ShellArgument.ShellQuote(remotePath) + " " + ShellArgument.ShellQuote(dest) + " && sudo update-ca-trust";
                        }
                        else
                        {
                            // 通用方式
                            installCmd = "sudo cp " + ShellArgument.ShellQuote(remotePath) + " /usr/local/share/ca-certificates/ 2>/dev/null || sudo cp " + ShellArgument.ShellQuote(remotePath) + " /etc/pki/ca-trust/source/anchors/ 2>/dev/null; sudo update-ca-certificates 2>/dev/null || sudo update-ca-trust 2>/dev/null";
                        }

                        var result = ExecuteRemote(installCmd);
                        exitCode = result.ExitCode;
                        sb.Append(result.Stdout);
                        if (!string.IsNullOrEmpty(result.Stderr)) sb.AppendLine("STDERR: ").Append(result.Stderr);

                        // 验证证书链
                        if (result.IsSuccess)
                        {
                            var verifyCmd = "openssl x509 -in " + ShellArgument.ShellQuote(remotePath) + " -text -noout 2>&1 | head -5";
                            var verifyResult = ExecuteRemote(verifyCmd);
                            sb.AppendLine().AppendLine("=== 证书信息 ===").Append(verifyResult.Stdout);
                        }

                        OnOutput("远程证书安装" + (result.IsSuccess ? "成功" : "失败") + ": " + localCertPath);
                    }
                    finally
                    {
                        transfer.CleanupTemp(remotePath);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message);
            }

            sw.Stop();
            return new RemoteCommandResult { Command = "remote-cert-install", ExitCode = exitCode, Stdout = sb.ToString(), Duration = sw.Elapsed };
        }

        /// <summary>查看证书信息</summary>
        public RemoteCommandResult InspectCert(string certPath)
        {
            // SEC-01: 路径元字符拦截
            try
            {
                ShellArgument.ValidateLocalPath(certPath, "certPath");
            }
            catch (ArgumentException ex)
            {
                return new RemoteCommandResult { Command = "openssl", ExitCode = -1, Stderr = ex.Message };
            }
            if (!File.Exists(certPath))
                return new RemoteCommandResult { Command = "openssl", ExitCode = -1, Stderr = "文件不存在" };

            var safePath = "\"" + certPath.Replace("\"", "\"\"") + "\"";
            return ExecuteLocal("openssl", "x509 -in " + safePath + " -text -noout");
        }

        private RemoteCommandResult ExecuteLocal(string fileName, string arguments)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
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

        public System.Windows.Forms.Control CreatePanel()
        {
            return ToolPanelHelper.CreateActionPanel(
                DisplayName,
                "输入本地证书路径；勾选远程需先绑定 SSH。默认安装到本机受信任根。",
                null,
                (inputs, output, status) =>
                {
                    var path = (inputs[0].Text ?? "").Trim();
                    if (string.IsNullOrEmpty(path) || path.StartsWith("目标"))
                    {
                        status.Text = "请输入证书路径";
                        return;
                    }
                    var local = InstallLocal(path, true);
                    ToolPanelHelper.AppendLine(output, "[本地] " + (local.Stdout ?? "") + (local.Stderr ?? ""));
                    if (HasRemoteSession)
                    {
                        var remote = InstallRemote(path, true);
                        ToolPanelHelper.AppendLine(output, "[远程] " + (remote.Stdout ?? "") + (remote.Stderr ?? ""));
                    }
                    status.Text = "完成";
                });
        }

        public void Dispose()
        {
            _session = null;
        }
    }

    /// <summary>证书工具配置</summary>
    public class CertificateInstallerConfig : ToolConfigBase
    {
        public List<string> TrustedCertPaths { get; set; }
        public bool AutoVerifyChain { get; set; }

        public override void ResetDefaults()
        {
            TrustedCertPaths = new List<string>();
            AutoVerifyChain = true;
        }

        protected override void LoadFromJson(string json)
        {
            TrustedCertPaths = ExtractStringList(json, "trustedCertPaths");
            AutoVerifyChain = ExtractBool(json, "autoVerifyChain", true);
        }

        protected override string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"autoVerifyChain\":").Append(AutoVerifyChain ? "true" : "false");
            sb.Append(",\"trustedCertPaths\":[");
            for (int i = 0; i < TrustedCertPaths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(TrustedCertPaths[i])).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
