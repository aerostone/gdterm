using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Gdterm.Tools.Models;

namespace Gdterm.Tools.Scanning
{
    /// <summary>
    /// 扫描执行通道——同一份插件脚本在不同目标上落地。
    /// v1：本机进程 + SSH 远端（linux=bash / windows=EncodedCommand，与宿主机同源脚本）。
    /// 预留：WinRM 通道、Ansible 通道（后续按此接口插入即可）。
    /// </summary>
    public interface IScanChannel
    {
        /// <summary>通道名（用于 UI 显示与结果归属）</summary>
        string Name { get; }

        /// <summary>此通道能否执行该目标类型的脚本</summary>
        bool Supports(string scriptKind);

        /// <summary>执行脚本；返回 (exitCode, stdout, stderr)。实现自行负责超时与清理。</summary>
        ScanExecutionOutput Execute(ScanPlugin plugin, string scriptContent, int timeoutSeconds);
    }

    /// <summary>执行原始产物。</summary>
    public class ScanExecutionOutput
    {
        public int ExitCode { get; set; }
        public string Stdout { get; set; }
        public string Stderr { get; set; }
        public string RuntimeError { get; set; }
    }

    /// <summary>
    /// 本机通道：windows 脚本走 PowerShell EncodedCommand（免落盘、不受 ExecutionPolicy 限制），
    /// linux 脚本走 /bin/bash 标准输入（开发机自测用）。
    /// </summary>
    public class LocalScanChannel : IScanChannel
    {
        public string Name { get { return "本机"; } }

        public bool Supports(string scriptKind)
        {
            return scriptKind == "windows" || scriptKind == "linux";
        }

        public ScanExecutionOutput Execute(ScanPlugin plugin, string scriptContent, int timeoutSeconds)
        {
            var kind = DetectScriptKind(plugin, scriptContent);
            if (kind == "windows") return RunWindows(scriptContent, timeoutSeconds);
            return RunBash(scriptContent, timeoutSeconds);
        }

        private static string DetectScriptKind(ScanPlugin plugin, string content)
        {
            var targets = plugin != null && plugin.Manifest != null ? plugin.Manifest.Targets : null;
            if (targets != null && targets.Count > 0)
                return targets[0].Trim().ToLowerInvariant();
            // 无声明时按内容猜
            return content != null && content.IndexOf("param(", StringComparison.OrdinalIgnoreCase) >= 0 ? "windows" : "linux";
        }

        internal static ScanExecutionOutput RunWindows(string scriptContent, int timeoutSeconds)
        {
            var exe = ResolvePowerShell();
            if (exe == null)
            {
                return new ScanExecutionOutput
                {
                    RuntimeError = "未找到 PowerShell（已尝试 PATH、pwsh、System32\\WindowsPowerShell、Program Files\\PowerShell\\7）"
                };
            }
            // 前置 UTF-8 控制台编码：重定向输出默认走 OEM 代码页，中文会乱码
            var prefixed = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\r\n" + (scriptContent ?? "");
            // UTF-16LE base64 —— powershell -EncodedCommand 的官方编码
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prefixed));
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            return RunProcess(psi, timeoutSeconds);
        }

        /// <summary>
        /// 稳健的 PowerShell 定位：PATH 可能不含 WindowsPowerShell 目录，逐个探测已知位置。
        /// 返回可执行文件完整路径，全部落空返回 null。
        /// </summary>
        internal static string ResolvePowerShell()
        {
            var candidates = new List<string>
            {
                "powershell.exe",
                "pwsh.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")
            };
            foreach (var c in candidates)
            {
                try
                {
                    if (!Path.IsPathRooted(c)) return c; // 交由 Process 去 PATH 解析
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return null;
        }

        internal static ScanExecutionOutput RunBash(string scriptContent, int timeoutSeconds)
        {
            // gdterm 是 Windows 应用：本机没有 bash，linux 插件必须走 SSH/WMI 远端通道
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                return new ScanExecutionOutput
                {
                    RuntimeError = "本机是 Windows：linux 脚本请选择“当前远程主机（SSH）”目标执行"
                };
            }
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-s",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            return RunProcess(psi, timeoutSeconds, writer => writer.Write(scriptContent ?? ""));
        }

        internal static ScanExecutionOutput RunProcess(ProcessStartInfo psi, int timeoutSeconds, Action<StreamWriter> feedStdin = null)
        {
            var output = new ScanExecutionOutput();
            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        output.RuntimeError = "进程启动失败";
                        return output;
                    }
                    if (feedStdin != null)
                    {
                        try
                        {
                            using (var sw = proc.StandardInput) feedStdin(sw);
                        }
                        catch { }
                    }
                    // 异步读防管道缓冲塞死；上限各 256KB
                    var stdoutCap = 256 * 1024;
                    var stderrCap = 256 * 1024;
                    var so = new StringBuilder();
                    var se = new StringBuilder();
                    proc.OutputDataReceived += (s, ev) => { if (ev.Data != null && so.Length < stdoutCap) so.AppendLine(ev.Data); };
                    proc.ErrorDataReceived += (s, ev) => { if (ev.Data != null && se.Length < stderrCap) se.AppendLine(ev.Data); };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    if (!proc.WaitForExit(timeoutSeconds * 1000))
                    {
                        try { proc.Kill(); } catch { }
                        output.RuntimeError = "执行超时（" + timeoutSeconds + "s），已终止";
                        output.Stdout = so.ToString();
                        output.Stderr = se.ToString();
                        return output;
                    }
                    output.ExitCode = proc.ExitCode;
                    output.Stdout = so.ToString();
                    output.Stderr = se.ToString();
                }
            }
            catch (Exception ex)
            {
                output.RuntimeError = ex.Message;
            }
            return output;
        }
    }

    /// <summary>
    /// SSH 远端通道：复用现有 ISshRemoteSession 抽象。
    /// linux：脚本经 SFTP 上传 /tmp 执行后清理；windows：同一 ps1 内容 base64 后
    /// powershell -EncodedCommand 单行下发（要求远端已装 OpenSSH Server + PowerShell）。
    /// </summary>
    public class SshScanChannel : IScanChannel
    {
        private readonly ISshRemoteSession _session;

        public SshScanChannel(ISshRemoteSession session)
        {
            _session = session;
        }

        public string Name { get { return "SSH 远端"; } }

        /// <summary>超过此大小的脚本才需要 SFTP 上传；以内一律 base64 内联。</summary>
        internal const int InlineScriptLimit = 200 * 1024;

        /// <summary>
        /// base64 内联执行：写入唯一临时文件后运行并清理，保留退出码。
        /// 不需要 SFTP 子系统，只要目标有 sh 和 base64（coreutils/busybox 均内置）。
        /// </summary>
        private RemoteCommandResult RunInline(byte[] bytes)
        {
            var b64 = Convert.ToBase64String(bytes);
            var tmp = "/tmp/.gdterm_scan_$$.sh"; // $$=远端 shell pid，天然防并发冲突
            var cmdline = "printf %s " + b64
                + " | base64 -d > " + tmp
                + "; sh " + tmp
                + "; __rc=$?; rm -f " + tmp
                + "; exit $__rc";
            return _session.RunCommand(cmdline);
        }

        public bool Supports(string scriptKind)
        {
            return (_session != null && _session.IsConnected) && (scriptKind == "linux" || scriptKind == "windows");
        }

        public ScanExecutionOutput Execute(ScanPlugin plugin, string scriptContent, int timeoutSeconds)
        {
            var output = new ScanExecutionOutput();
            if (_session == null || !_session.IsConnected)
            {
                output.RuntimeError = "远程会话未连接";
                return output;
            }
            try
            {
                var kind = plugin != null && plugin.Manifest != null && plugin.Manifest.Targets != null && plugin.Manifest.Targets.Count > 0
                    ? plugin.Manifest.Targets[0].Trim().ToLowerInvariant()
                    : "linux";

                RemoteCommandResult result;
                string tempPath = null;
                if (kind == "windows")
                {
                    // 与本机通道同源：同一 ps1 经 EncodedCommand 下发；前置 UTF-8 编码防中文乱码
                    var prefixed = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\r\n" + (scriptContent ?? "");
                    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prefixed));
                    result = _session.RunCommand("powershell -NoProfile -NonInteractive -EncodedCommand " + encoded);
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(scriptContent ?? "");
                    if (bytes.Length <= InlineScriptLimit)
                    {
                        // 主通道：base64 内联下发——零 SFTP 依赖，目标机只需 sh + coreutils base64
                        result = RunInline(bytes);
                    }
                    else
                    {
                        // 大脚本回退 SFTP 上传
                        using (var transfer = _session.CreateFileTransfer())
                        {
                            tempPath = transfer.UploadToTemp(bytes, "gdterm_scan_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sh");
                            // 显式回传退出码并清理临时文件
                            result = _session.RunCommand("sh " + tempPath + "; __rc=$?; rm -f " + tempPath + "; exit $__rc");
                            tempPath = null; // 命令内已清理
                        }
                    }
                }
                if (tempPath != null)
                {
                    try { using (var t2 = _session.CreateFileTransfer()) t2.CleanupTemp(tempPath); } catch { }
                }
                output.ExitCode = result != null ? result.ExitCode : -1;
                output.Stdout = result != null ? result.Stdout : null;
                output.Stderr = result != null ? result.Stderr : null;
            }
            catch (Exception ex)
            {
                output.RuntimeError = ex.Message;
            }
            return output;
        }
    }
}
