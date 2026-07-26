using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端就绪状态检测器——判断终端是否处于命令提示符状态
    /// 只有就绪状态的终端才适合加入多通道广播
    /// 
    /// 就绪特征：最后一行匹配常见 shell 提示符（$、#、>、%、~# 等）
    /// 非就绪特征：top/vim/less/tail -f/编译输出/交互式程序正在运行
    /// </summary>
    public static class TerminalReadyDetector
    {
        // ===== 常见 shell 提示符正则 =====
        // 匹配: user@host:~$ 、root@server:~# 、bash-5.1$ 、[user@host dir]$ 、> 、$ 、#
        private static readonly Regex[] PromptPatterns = new[]
        {
            // Linux/Bash 标准: user@host:~/path$ 或 user@host:~/path#
            new Regex(@"\S+@\S+:\S*[#$]\s?$", RegexOptions.Compiled),
            // Zsh: user@host ~/dir %
            new Regex(@"\S+@\S+\s+\S+\s+%\s?$", RegexOptions.Compiled),
            // Bash 简写: bash-5.1$ 或 bash-4.4#
            new Regex(@"bash-[\d.]+\S*[#$]\s?$", RegexOptions.Compiled),
            // CentOS/RHEL: [user@host dir]$
            new Regex(@"\[\S+@\S+\s+\S+\][#$]\s?$", RegexOptions.Compiled),
            // Fish: user@host ~/dir>
            new Regex(@"\S+@\S+\s+[^>\n]+>\s?$", RegexOptions.Compiled),
            // Windows CMD (via SSH): C:\Users\user>
            new Regex(@"[A-Z]:\\[^>]+>\s?$", RegexOptions.Compiled),
            // PowerShell (via SSH): PS C:\Users\user>
            new Regex(@"PS\s+[A-Z]:\\[^>]+>\s?$", RegexOptions.Compiled),
            // Cisco/网络设备: hostname> 或 hostname#
            new Regex(@"^[A-Za-z0-9_-]+[>#]\s?$", RegexOptions.Compiled),
            // 纯提示符（保守匹配）
            new Regex(@"[#$>%]\s?$", RegexOptions.Compiled),
        };

        // ===== 非就绪状态特征（正在运行的程序） =====
        private static readonly string[] BusyIndicators = new[]
        {
            "top -", "htop", "Tasks:", "%Cpu", "MiB Mem",     // top/htop
            "KiB Mem", "load average:",                         // top
            "-- INSERT --", "-- VISUAL --", "-- NORMAL --",     // vim/neovim
            "-- REPLACE --", "-- COMMAND --",                   // vim
            ":",                                                // vim 底行模式（需结合行首）
            "(END)",                                            // less/man
            "(lines ",                                          // tail -f
            "Press [Enter]", "Press any key",                   // 交互提示
            "password:", "Password:",                           // 密码输入
            "sudo",                                             // sudo 密码
            "y/n", "Y/n", "[Y/n]", "[y/N]",                    // 确认提示
            "continue?", "Continue?",                           // 确认
            "(q)uit",                                           // 各种交互
            ">>>",                                              // Python REPL
            "irb(main):",                                       // Ruby REPL
            "node >", ">",                                      // Node REPL（需结合上下文）
            "mysql>", "MariaDB",                                // MySQL REPL
            "redis>",                                           // Redis REPL
            "postgres=#",                                       // PostgreSQL REPL
            "mongo>",                                           // MongoDB REPL
            "ftp>", "sftp>",                                    // FTP/SFTP
            "telnet>",                                          // Telnet
        };

        // ===== 编译/长时间运行输出特征 =====
        private static readonly string[] LongRunningIndicators = new[]
        {
            "make[", "gcc ", "g++ ", "cmake ",                 // 编译
            "Compiling", "Building", "Linking",                // 编译
            "npm ", "yarn ", "pnpm ",                           // 包管理
            "docker ", "podman ",                               // 容器
            "scp ", "rsync ",                                   // 文件传输
            "ping ", "traceroute ", "mtr ",                     // 网络诊断
            "dd ", "tar ", "zip ", "unzip ",                    // 归档/IO
            "apt ", "yum ", "dnf ", "pacman ",                  // 包管理
            "systemctl", "journalctl",                         // systemd
            "tail -f", "tail -F",                              // 实时日志
        };

        /// <summary>
        /// 检测终端是否处于就绪状态（命令提示符）
        /// </summary>
        /// <param name="recentOutput">终端最近的输出行（至少 3-5 行）</param>
        /// <returns>就绪状态信息</returns>
        public static ReadyState Detect(IList<string> recentOutput)
        {
            if (recentOutput == null || recentOutput.Count == 0)
                return new ReadyState(false, "无输出", ReadyReason.NoOutput);

            // 取最后一行非空行
            var lastLine = "";
            for (int i = recentOutput.Count - 1; i >= 0; i--)
            {
                var trimmed = (recentOutput[i] ?? "").TrimEnd();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    lastLine = trimmed;
                    break;
                }
            }

            if (string.IsNullOrEmpty(lastLine))
                return new ReadyState(false, "无输出", ReadyReason.NoOutput);

            // 检查是否匹配非就绪状态
            var lastLineLower = lastLine.ToLowerInvariant();
            foreach (var indicator in BusyIndicators)
            {
                if (lastLineLower.Contains(indicator.ToLowerInvariant()))
                {
                    return new ReadyState(false, $"终端忙: 检测到 '{indicator}'", ReadyReason.BusyProgram);
                }
            }

            // 检查最近几行是否有长时间运行的特征（net462 无 TakeLast）
            var take = Math.Min(5, recentOutput.Count);
            var recentSlice = new List<string>(take);
            for (var i = recentOutput.Count - take; i < recentOutput.Count; i++)
                recentSlice.Add(recentOutput[i]);
            var recentText = string.Join(" ", recentSlice).ToLowerInvariant();
            foreach (var indicator in LongRunningIndicators)
            {
                if (recentText.Contains(indicator.ToLowerInvariant()))
                {
                    // 长时间运行的命令输出中，最后一行不是提示符
                    bool isPrompt = PromptPatterns.Any(p => p.IsMatch(lastLine));
                    if (!isPrompt)
                    {
                        return new ReadyState(false, $"可能在执行: '{indicator}'", ReadyReason.LongRunning);
                    }
                }
            }

            // 检查是否匹配命令提示符
            foreach (var pattern in PromptPatterns)
            {
                if (pattern.IsMatch(lastLine))
                {
                    return new ReadyState(true, "命令就绪", ReadyReason.PromptDetected);
                }
            }

            // 默认：无法确定，视为未就绪（保守策略）
            return new ReadyState(false, "未检测到提示符", ReadyReason.NoPrompt);
        }

        /// <summary>
        /// 快速检查：终端是否就绪
        /// </summary>
        public static bool IsReady(IList<string> recentOutput)
        {
            return Detect(recentOutput).IsReady;
        }
    }

    /// <summary>
    /// 终端就绪状态
    /// </summary>
    public class ReadyState
    {
        /// <summary>
        /// 是否就绪
        /// </summary>
        public bool IsReady { get; }

        /// <summary>
        /// 状态描述
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// 就绪/未就绪原因
        /// </summary>
        public ReadyReason Reason { get; }

        public ReadyState(bool isReady, string description, ReadyReason reason)
        {
            IsReady = isReady;
            Description = description;
            Reason = reason;
        }

        public override string ToString() => IsReady ? $"✓ {Description}" : $"✗ {Description}";
    }

    /// <summary>
    /// 就绪原因分类
    /// </summary>
    public enum ReadyReason
    {
        /// <summary>
        /// 检测到命令提示符（$、#、>、% 等）
        /// </summary>
        PromptDetected,

        /// <summary>
        /// 无输出
        /// </summary>
        NoOutput,

        /// <summary>
        /// 未检测到提示符
        /// </summary>
        NoPrompt,

        /// <summary>
        /// 正在运行程序（top/vim/less 等）
        /// </summary>
        BusyProgram,

        /// <summary>
        /// 长时间运行的命令（编译/传输等）
        /// </summary>
        LongRunning
    }
}
