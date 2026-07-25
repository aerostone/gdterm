using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 终端关键词高亮规则
    /// </summary>
    public class HighlightRule
    {
        /// <summary>规则ID</summary>
        public string Id { get; set; }

        /// <summary>规则名称</summary>
        public string Name { get; set; }

        /// <summary>匹配模式（关键词或正则）</summary>
        public string Pattern { get; set; }

        /// <summary>是否正则表达式</summary>
        public bool IsRegex { get; set; }

        /// <summary>是否区分大小写</summary>
        public bool CaseSensitive { get; set; }

        /// <summary>前景色（null=不改变）</summary>
        public string ForegroundColor { get; set; }

        /// <summary>背景色（null=不改变）</summary>
        public string BackgroundColor { get; set; }

        /// <summary>是否加粗</summary>
        public bool Bold { get; set; }

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>排序</summary>
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// 高亮规则集合（带内置预设）
    /// </summary>
    public class HighlightRuleConfig
    {
        public List<HighlightRule> Rules { get; set; } = new List<HighlightRule>();

        /// <summary>获取内置默认规则</summary>
        public static HighlightRuleConfig GetDefault()
        {
            var config = new HighlightRuleConfig();
            var defaults = new[]
            {
                // 错误/失败 — 红色加粗
                new HighlightRule { Id = "hl-error", Name = "错误", Pattern = @"(?i)\b(ERROR|FATAL|FAILED|FAILURE|CRITICAL|PANIC)\b", IsRegex = true, ForegroundColor = "#FF4444", Bold = true, Enabled = true, SortOrder = 0 },
                // 警告 — 黄色
                new HighlightRule { Id = "hl-warn", Name = "警告", Pattern = @"(?i)\b(WARN|WARNING|ALERT)\b", IsRegex = true, ForegroundColor = "#FFD700", Enabled = true, SortOrder = 1 },
                // 成功 — 绿色
                new HighlightRule { Id = "hl-success", Name = "成功", Pattern = @"(?i)\b(SUCCESS|SUCCEEDED|OK|DONE|COMPLETE|PASSED|ACCEPTED)\b", IsRegex = true, ForegroundColor = "#00CC00", Enabled = true, SortOrder = 2 },
                // IP 地址 — 青色
                new HighlightRule { Id = "hl-ip", Name = "IP地址", Pattern = @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", IsRegex = true, ForegroundColor = "#00CCCC", Enabled = true, SortOrder = 3 },
                // URL — 蓝色下划线
                new HighlightRule { Id = "hl-url", Name = "URL", Pattern = @"https?://[^\s]+", IsRegex = true, ForegroundColor = "#4488FF", Enabled = true, SortOrder = 4 },
                // 数字 — 浅蓝
                new HighlightRule { Id = "hl-number", Name = "数字", Pattern = @"(?<![a-zA-Z])\b\d+(\.\d+)?\b(?![a-zA-Z])", IsRegex = true, ForegroundColor = "#88BBFF", Enabled = false, SortOrder = 5 },
                // root/sudo — 橙色背景
                new HighlightRule { Id = "hl-root", Name = "root/sudo", Pattern = @"(?i)\b(root|sudo|su\b)", IsRegex = true, ForegroundColor = "#FF8800", Bold = true, Enabled = true, SortOrder = 6 },
                // 文件路径 — 紫色
                new HighlightRule { Id = "hl-path", Name = "文件路径", Pattern = @"(/[a-zA-Z0-9_./-]+)", IsRegex = true, ForegroundColor = "#CC88FF", Enabled = false, SortOrder = 7 },
                // systemd 服务状态
                new HighlightRule { Id = "hl-service", Name = "服务状态", Pattern = @"(?i)\b(active|inactive|dead|running|stopped|enabled|disabled)\b", IsRegex = true, ForegroundColor = "#44DD88", Enabled = true, SortOrder = 8 },
                // 端口号
                new HighlightRule { Id = "hl-port", Name = "端口", Pattern = @"(?i)\b(port\s+\d+|:\d{2,5})\b", IsRegex = true, ForegroundColor = "#DDA0DD", Enabled = true, SortOrder = 9 },
            };
            config.Rules.AddRange(defaults);
            return config;
        }
    }
}
