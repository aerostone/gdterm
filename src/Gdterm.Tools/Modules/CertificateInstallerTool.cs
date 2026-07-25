using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
            if (!File.Exists(certPath))
                return new RemoteCommandResult { Command = "local-certutil", ExitCode = -1, Stderr = "证书文件不存在: " + certPath };

            var store = isTrustedRoot ? "Root" : "My";
            var args = "-addstore \"" + store + "\" \"" + certPath + "\"";
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
                        if (osInfo.Contains("Ubuntu") || osInfo.Contains("Debian"))
                        {
                            var dest = isTrustedRoot
                                ? "/usr/local/share/ca-certificates/" + Path.GetFileName(localCertPath)
                                : "/etc/ssl/certs/" + Path.GetFileName(localCertPath);
                            installCmd = string.Format("sudo cp {0} {1} && sudo update-ca-certificates", remotePath, dest);
                        }
                        else if (osInfo.Contains("CentOS") || osInfo.Contains("Red Hat") || osInfo.Contains("RHEL"))
                        {
                            var dest = isTrustedRoot
                                ? "/etc/pki/ca-trust/source/anchors/" + Path.GetFileName(localCertPath)
                                : "/etc/pki/tls/certs/" + Path.GetFileName(localCertPath);
                            installCmd = string.Format("sudo cp {0} {1} && sudo update-ca-trust", remotePath, dest);
                        }
                        else
                        {
                            // 通用方式
                            installCmd = string.Format("sudo cp {0} /usr/local/share/ca-certificates/ 2>/dev/null || sudo cp {0} /etc/pki/ca-trust/source/anchors/ 2>/dev/null; sudo update-ca-certificates 2>/dev/null || sudo update-ca-trust 2>/dev/null", remotePath);
                        }

                        var result = ExecuteRemote(installCmd);
                        exitCode = result.ExitCode;
                        sb.Append(result.Stdout);
                        if (!string.IsNullOrEmpty(result.Stderr)) sb.AppendLine("STDERR: ").Append(result.Stderr);

                        // 验证证书链
                        if (result.IsSuccess)
                        {
                            var verifyCmd = string.Format("openssl x509 -in {0} -text -noout 2>&1 | head -5", remotePath);
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
            if (!File.Exists(certPath))
                return new RemoteCommandResult { Command = "openssl", ExitCode = -1, Stderr = "文件不存在" };

            return ExecuteLocal("openssl", "x509 -in \"" + certPath + "\" -text -noout");
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

        protected override void ResetDefaults()
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
