using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace Gdterm.Tools.Scanning
{
    /// <summary>
    /// WMI 备用通道——远端 Windows 没有 OpenSSH Server 时使用。
    /// 仅依赖目标机自带服务（Winmgmt 常开 + 默认 ADMIN$ 管理共享），零安装：
    ///   1. Win32_Process.Create 启动 cmd /V:ON 包裹的 powershell -EncodedCommand，
    ///      stdout/stderr/退出码重定向到 %SystemRoot%\Temp 下唯一命名文件
    ///   2. 轮询进程消失（即整条命令结束）
    ///   3. 经 \\host\ADMIN$\Temp 取回三个文件并清理
    /// 权限：需要目标机管理员身份（域环境当前用户通常即可；工作组填本地管理员账号）。
    /// </summary>
    public class WmiScanChannel : IScanChannel
    {
        private readonly string _host;
        private readonly string _username; // 可空=用当前 Windows 身份；域账号格式 DOMAIN\user
        private readonly string _password;

        public WmiScanChannel(string host, string username, string password)
        {
            _host = (host ?? "").Trim();
            _username = (username ?? "").Trim();
            _password = password ?? "";
        }

        public string Name { get { return "WMI 远端(" + _host + ")"; } }

        public bool Supports(string scriptKind)
        {
            return scriptKind == "windows";
        }

        public ScanExecutionOutput Execute(ScanPlugin plugin, string scriptContent, int timeoutSeconds)
        {
            var output = new ScanExecutionOutput();
            if (_host.Length == 0)
            {
                output.RuntimeError = "未填写远程主机地址";
                return output;
            }
            if (timeoutSeconds <= 0) timeoutSeconds = 60;

            var tag = Guid.NewGuid().ToString("N").Substring(0, 12);
            var remoteTemp = Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\Temp"; // C:\Windows\Temp
            var outPath = remoteTemp + @"\gdterm_" + tag + "_o.txt";
            var errPath = remoteTemp + @"\gdterm_" + tag + "_e.txt";
            var rcPath = remoteTemp + @"\gdterm_" + tag + "_rc.txt";

            try
            {
                var scope = CreateScope();
                var prefixed = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\r\n" + (scriptContent ?? "");
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prefixed));

                // /V:ON 开启延迟展开：!ERRORLEVEL! 在 powershell 结束后才求值
                var cmdline = "cmd.exe /V:ON /C powershell -NoProfile -NonInteractive -EncodedCommand " + encoded
                    + " > " + outPath + " 2> " + errPath
                    + "& echo !ERRORLEVEL! > " + rcPath;

                uint pid = StartProcess(scope, cmdline, output);
                if (pid == 0) return output;

                WaitForExit(scope, pid, timeoutSeconds, output);
                if (output.RuntimeError != null && output.Stdout == null)
                {
                    // finding-14：超时/终止后不再尝试取回（远端可能未产出结果文件），保留超时提示
                    return output;
                }

                // 取回结果（ADMIN$ 映射到 C:\Windows）
                var uncOut = ToUnc(outPath);
                var uncErr = ToUnc(errPath);
                var uncRc = ToUnc(rcPath);
                output.Stdout = TryReadFile(uncOut, output);
                output.Stderr = TryReadFile(uncErr, output);
                var rcText = TryReadFile(uncRc, output);
                int rc;
                if (int.TryParse((rcText ?? "").Trim(), out rc)) output.ExitCode = rc;
            }
            catch (UnauthorizedAccessException ex)
            {
                output.RuntimeError = DescribeAccessDenied(ex);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                output.RuntimeError = DescribeComError(ex);
            }
            catch (ManagementException ex)
            {
                output.RuntimeError = "WMI 操作失败: " + ex.Message + HintSuffix();
            }
            catch (Exception ex)
            {
                output.RuntimeError = ex.Message + HintSuffix();
            }
            finally
            {
                CleanupRemoteFiles(ToUnc(outPath), ToUnc(errPath), ToUnc(rcPath));
            }
            return output;
        }

        // ===== WMI 基元 =====

        private ManagementScope CreateScope()
        {
            var path = new ManagementPath("\\\\" + _host + "\\root\\cimv2");
            ManagementScope scope;
            if (_username.Length == 0)
            {
                // 本地系统、同域信任身份或已缓存的凭据——走当前进程令牌
                scope = new ManagementScope(path);
            }
            else
            {
                var options = new ConnectionOptions
                {
                    Username = _username,
                    Password = _password,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.PacketPrivacy,
                    EnablePrivileges = true,
                    Timeout = new TimeSpan(0, 0, 15)
                };
                scope = new ManagementScope(path, options);
            }
            scope.Connect(); // 连不上在此抛 COMException/ManagementException
            return scope;
        }

        private static uint StartProcess(ManagementScope scope, string cmdline, ScanExecutionOutput output)
        {
            using (var procClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), new ObjectGetOptions()))
            using (var inParams = procClass.GetMethodParameters("Create"))
            {
                inParams["CommandLine"] = cmdline;
                inParams["CurrentDirectory"] = @"C:\Windows\Temp";
                using (var outParams = procClass.InvokeMethod("Create", inParams, null))
                {
                    uint rc = outParams["ReturnValue"] != null ? Convert.ToUInt32(outParams["ReturnValue"]) : 8;
                    if (rc != 0)
                    {
                        output.RuntimeError = "Win32_Process.Create 失败，返回码 " + rc + "（2=拒绝访问 3=路径未找到 8=未知失败 9=路径不存在 21=参数无效）" + HintSuffix();
                        return 0;
                    }
                    return Convert.ToUInt32(outParams["ProcessId"]);
                }
            }
        }

        private static void WaitForExit(ManagementScope scope, uint pid, int timeoutSeconds, ScanExecutionOutput output)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(600);
                bool alive;
                using (var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT ProcessId FROM Win32_Process WHERE ProcessId=" + pid)))
                using (var results = searcher.Get())
                {
                    alive = results.Count > 0;
                }
                if (!alive) return;
            }
            // finding-14：超时后尽力终止远端进程，避免残留 powershell 与临时文件
            if (TryTerminateRemote(scope, pid))
                output.RuntimeError = "执行超时（" + timeoutSeconds + "s），已终止远端进程 PID " + pid;
            else
                output.RuntimeError = "执行超时（" + timeoutSeconds + "s）——远端进程可能仍在运行，请到目标机任务管理器确认 PID " + pid;
        }

        /// <summary>best-effort 终止远端 Win32_Process；失败不抛出（超时提示已足够指导人工处理）。</summary>
        private static bool TryTerminateRemote(ManagementScope scope, uint pid)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT ProcessId FROM Win32_Process WHERE ProcessId=" + pid)))
                using (var results = searcher.Get())
                {
                    foreach (var baseObj in results)
                    using (baseObj)
                    {
                        // CS1061：InvokeMethod 在 ManagementObject 上，而非 ManagementBaseObject
                        var obj = baseObj as ManagementObject;
                        if (obj == null) continue;
                        // 该重载（string, object[]）直接返回方法返回值（Terminate 的 uint 返回码），无 out 参数集
                        var rc = obj.InvokeMethod("Terminate", new object[] { 0u });
                        return rc != null && Convert.ToUInt32(rc) == 0;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ===== 结果取回 =====

        /// <summary>C:\Windows\Temp\x → \\host\ADMIN$\Temp\x（ADMIN$ 即远端 C:\Windows）。</summary>
        private string ToUnc(string remotePath)
        {
            var windowsPrefix = Environment.GetFolderPath(Environment.SpecialFolder.System); // C:\Windows
            if (remotePath != null && remotePath.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase))
                return "\\\\" + _host + "\\ADMIN$" + remotePath.Substring(windowsPrefix.Length);
            return "\\\\" + _host + "\\C$" + remotePath; // 兼容非系统盘路径
        }

        private static string TryReadFile(string uncPath, ScanExecutionOutput output)
        {
            try
            {
                if (!File.Exists(uncPath))
                {
                    // finding-14：仅在尚无更早错误时才写取回失败——避免覆盖 stdout/stderr 阶段已有的提示
                    if (output.Stdout == null && output.Stderr == null && output.RuntimeError == null
                        && uncPath.EndsWith("_o.txt", StringComparison.Ordinal))
                        output.RuntimeError = "取回结果失败：文件不存在——ADMIN$ 共享可能被关闭。" + HintSuffix();
                    return null;
                }
                return File.ReadAllText(uncPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                if (output.RuntimeError == null)
                    output.RuntimeError = "取回结果失败: " + ex.Message + "——需开启 ADMIN$ 管理共享。" + HintSuffix();
                return null;
            }
        }

        private static void CleanupRemoteFiles(params string[] uncPaths)
        {
            foreach (var p in uncPaths)
            {
                try { if (p != null && File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        // ===== 错误文案 =====

        private static string HintSuffix()
        {
            return "\r\n提示：WMI 通道要求目标管理员权限且 ADMIN$ 共享可用；若不可满足请改用 SSH 通道（目标安装 OpenSSH Server）。";
        }

        private static string DescribeAccessDenied(UnauthorizedAccessException ex)
        {
            return "访问被拒绝: " + ex.Message + " —— 当前账号对目标没有管理员权限。" + HintSuffix();
        }

        private static string DescribeComError(System.Runtime.InteropServices.COMException ex)
        {
            // 0x800706BA RPC 服务器不可用 / 0x80070005 拒绝访问 / 0x800A0046 等 DCOM 拒绝
            var hr = ex.HResult & 0xFFFF;
            var extra = "";
            if (ex.HResult == unchecked((int)0x800706BA)) extra = "（RPC 不可达：检查目标防火墙放行 WMI/DCOM）";
            else if (ex.HResult == unchecked((int)0x80070005)) extra = "（凭据被拒绝：核对用户名格式 DOMAIN\\user 与密码）";
            return "WMI 连接失败: " + ex.Message + extra + HintSuffix();
        }
    }
}
