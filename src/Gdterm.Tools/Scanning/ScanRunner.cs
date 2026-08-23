using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Gdterm.Tools.Scanning
{
    /// <summary>
    /// 扫描执行器——选插件 × 选通道，解析输出契约，聚合结果。
    ///
    /// 输出契约（脚本零依赖，ps1/sh 通吃）：
    ///   FINDING|&lt;severity&gt;|&lt;title&gt;|&lt;detail&gt;   单条发现（severity ∈ info/low/medium/high/critical）
    ///   其余行                                        原始日志，原样呈现
    /// </summary>
    public class ScanRunner
    {
        private const int MaxFindings = 200;
        private const int MaxRawChars = 128 * 1024;

        /// <summary>单插件完成回调（工作线程，UI 自行封送）。</summary>
        public event EventHandler<ScanRunResult> PluginCompleted;

        /// <summary>
        /// 执行一批插件；阻塞调用（调用方放 Task.Run）。任一插件失败不影响其余。
        /// </summary>
        public void Run(IEnumerable<ScanPlugin> plugins, IScanChannel channel)
        {
            if (plugins == null) throw new ArgumentNullException("plugins");
            if (channel == null) throw new ArgumentNullException("channel");

            foreach (var plugin in plugins.ToList())
            {
                var result = RunOne(plugin, channel);
                var handler = PluginCompleted;
                if (handler != null)
                {
                    try { handler(this, result); } catch { }
                }
            }
        }

        public ScanRunResult RunOne(ScanPlugin plugin, IScanChannel channel)
        {
            var sw = Stopwatch.StartNew();
            var manifest = plugin != null ? plugin.Manifest : null;
            var result = new ScanRunResult
            {
                PluginId = plugin != null ? plugin.Id : "(null)",
                PluginName = plugin != null ? plugin.DisplayName : "(null)",
                TargetName = channel != null ? channel.Name : "?"
            };

            try
            {
                if (plugin == null || !plugin.IsRunnable)
                {
                    result.RuntimeError = plugin != null && plugin.LoadError != null ? plugin.LoadError : "插件不可运行";
                    return result;
                }

                var kind = manifest.Targets[0].Trim().ToLowerInvariant();
                if (!channel.Supports(kind))
                {
                    result.RuntimeError = "通道 " + channel.Name + " 不支持目标 " + kind
                        + (kind == "windows" ? "（远端 Windows 需已安装 OpenSSH Server + PowerShell）" : "（需先连接 SSH 远程主机）");
                    return result;
                }

                var content = File.ReadAllText(plugin.ScriptPath);
                var output = channel.Execute(plugin, content, manifest.TimeoutSeconds);
                sw.Stop();
                result.Duration = sw.Elapsed;
                result.ExitCode = output.ExitCode;
                result.ErrorOutput = Truncate(output.Stderr);
                result.RawOutput = Truncate(StripFindingLines(output.Stdout, result.Findings, result.TargetName, plugin.Id));
                result.Success = output.RuntimeError == null && output.ExitCode == 0;
                result.RuntimeError = output.RuntimeError;

                // 退出码非 0 且无任何发现时把 stderr 提示为运行层问题，避免用户只看到空结果
                if (!result.Success && result.Findings.Count == 0 && output.RuntimeError == null)
                {
                    result.RuntimeError = string.IsNullOrWhiteSpace(result.ErrorOutput)
                        ? "脚本退出码 " + output.ExitCode
                        : result.ErrorOutput.Trim();
                }
            }
            catch (Exception ex)
            {
                result.RuntimeError = ex.Message;
                result.Success = false;
            }
            return result;
        }

        /// <summary>摘出 FINDING 行并转为发现对象；返回剩余原始日志。</summary>
        internal static string StripFindingLines(string stdout, List<ScanFinding> sink, string targetName, string pluginId)
        {
            if (string.IsNullOrEmpty(stdout)) return "";
            var log = new StringBuilder();
            foreach (var rawLine in stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (!line.StartsWith("FINDING|", StringComparison.Ordinal))
                {
                    log.AppendLine(line);
                    continue;
                }
                if (sink.Count >= MaxFindings) continue;
                var parts = line.Split(new[] { '|' }, 4);
                // FINDING|severity|title|detail
                var severity = parts.Length > 1 ? NormalizeSeverity(parts[1]) : "info";
                var title = parts.Length > 2 ? parts[2].Trim() : "";
                var detail = parts.Length > 3 ? parts[3].Trim() : "";
                if (title.Length == 0) continue;
                sink.Add(new ScanFinding
                {
                    Severity = severity,
                    Title = title,
                    Detail = detail,
                    PluginId = pluginId,
                    TargetName = targetName
                });
            }
            return log.ToString();
        }

        internal static string NormalizeSeverity(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            switch (s)
            {
                case "critical": case "high": case "medium": case "low": case "info": return s;
                case "warn": case "warning": return "medium";
                case "error": return "high";
                default: return "info";
            }
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Length <= MaxRawChars ? s : s.Substring(0, MaxRawChars) + "\r\n…(已截断)";
        }
    }
}
