using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Gdterm.Tools.Scanning
{
    /// <summary>
    /// 扫描插件清单（manifest.json）——声明式描述一个可热更新的扫描插件。
    /// 插件 = 本清单 + 同目录脚本文件；脚本按 <see cref="Targets"/> 决定由哪个通道执行。
    /// </summary>
    public class ScanPluginManifest
    {
        /// <summary>唯一标识（= 目录名，约定小写中划线）</summary>
        public string Id { get; set; }

        /// <summary>显示名称</summary>
        public string Name { get; set; }

        /// <summary>一句话说明</summary>
        public string Description { get; set; }

        /// <summary>分类（安全 / 系统 / 网络…）</summary>
        public string Category { get; set; }

        /// <summary>适用目标："windows"（本机与远端 Windows 同源脚本）/ "linux"</summary>
        public List<string> Targets { get; set; }

        /// <summary>脚本文件名（相对本清单所在目录）</summary>
        public string ScriptFile { get; set; }

        /// <summary>超时秒数（默认 60）</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>版本号</summary>
        public string Version { get; set; }

        /// <summary>是否启用（内置插件可通过置 false 永久停用）</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>加载完成的插件实例。</summary>
    public class ScanPlugin
    {
        public ScanPluginManifest Manifest { get; set; }

        /// <summary>脚本绝对路径</summary>
        public string ScriptPath { get; set; }

        /// <summary>来源：builtin（内置物化）/ user（用户自建）</summary>
        public string Source { get; set; }

        /// <summary>加载失败原因（非 null 时此插件不可运行）</summary>
        public string LoadError { get; set; }

        public bool IsRunnable { get { return LoadError == null && Manifest != null && Manifest.Enabled && File.Exists(ScriptPath); } }

        public string DisplayName { get { return Manifest != null ? Manifest.Name : Id; } }
        public string Id { get { return Manifest != null ? Manifest.Id : "(未知)"; } }

        public string TargetSummary
        {
            get
            {
                if (Manifest == null || Manifest.Targets == null || Manifest.Targets.Count == 0) return "-";
                return string.Join("/", Manifest.Targets);
            }
        }
    }

    /// <summary>单条扫描发现。</summary>
    public class ScanFinding
    {
        /// <summary>info / low / medium / high / critical</summary>
        public string Severity { get; set; }

        public string Title { get; set; }
        public string Detail { get; set; }
        public string PluginId { get; set; }
        public string TargetName { get; set; }
    }

    /// <summary>单个插件一次执行的结果。</summary>
    public class ScanRunResult
    {
        public string PluginId { get; set; }
        public string PluginName { get; set; }
        public string TargetName { get; set; }

        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public TimeSpan Duration { get; set; }

        public List<ScanFinding> Findings { get; set; } = new List<ScanFinding>();

        /// <summary>原始输出（已截断），不含 FINDING 行</summary>
        public string RawOutput { get; set; }

        public string ErrorOutput { get; set; }

        /// <summary>超时/异常等运行层错误（非空时 Success=false）</summary>
        public string RuntimeError { get; set; }
    }
}
