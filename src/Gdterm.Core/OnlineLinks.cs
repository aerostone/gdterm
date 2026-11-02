using System;
using System.Text.RegularExpressions;

namespace Gdterm.Core
{
    /// <summary>
    /// 终端选中文本的在线动作 URL 构造（nyaterm 对比吸收 A4：在线搜索/翻译/IP 查询）。
    /// 纯逻辑，零依赖，可测；浏览器打开由 UI 层 Process.Start 负责。
    /// </summary>
    public static class OnlineLinks
    {
        private static readonly Regex Ipv4Regex = new Regex(
            @"\b((?:\d{1,3}\.){3}\d{1,3})\b",
            RegexOptions.Compiled);

        /// <summary>Bing 搜索（中性入口，国内可达）。</summary>
        public static string BuildSearchUrl(string query)
        {
            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(query ?? "");
        }

        /// <summary>Bing 翻译（auto → 简中）。</summary>
        public static string BuildTranslateUrl(string query)
        {
            return "https://www.bing.com/translator?from=auto&to=zh-Hans&q=" + Uri.EscapeDataString(query ?? "");
        }

        /// <summary>ip138 IP 查询（中文运维常用）。</summary>
        public static string BuildIpLookupUrl(string ip)
        {
            return "https://www.ip138.com/ipslookup/ipWhoisSave?ip=" + Uri.EscapeDataString(ip ?? "");
        }

        /// <summary>
        /// 取文本中第一个合法 IPv4（四段 0-255）。
        /// "192.168.1.1:8080" → "192.168.1.1"；"10.0.0.256" → null（非法段）。
        /// </summary>
        public static string ExtractFirstIpv4(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (Match m in Ipv4Regex.Matches(text))
            {
                var candidate = m.Groups[1].Value;
                var parts = candidate.Split('.');
                bool ok = parts.Length == 4;
                foreach (var p in parts)
                {
                    int v;
                    if (!int.TryParse(p, out v) || v < 0 || v > 255) { ok = false; break; }
                }
                if (ok) return candidate;
            }
            return null;
        }

        /// <summary>host:port 匹配结果（A3-lite：选中文本中的地址 → 打开对应连接）。</summary>
        public sealed class HostPortMatch
        {
            public string Host { get; internal set; }
            public int Port { get; internal set; }
        }

        private static readonly Regex HostPortRegex = new Regex(
            @"\b([A-Za-z0-9][A-Za-z0-9._\-]*):([0-9]{1,5})\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 取文本中第一个 host:port（port 1-65535 合法）。
        /// 已知限制："12:30" 这类时间串会命中（port=30）——动作由用户主动点击，误报无害。
        /// </summary>
        public static HostPortMatch ExtractFirstHostPort(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (Match m in HostPortRegex.Matches(text))
            {
                int port;
                if (!int.TryParse(m.Groups[2].Value, out port) || port < 1 || port > 65535) continue;
                return new HostPortMatch { Host = m.Groups[1].Value, Port = port };
            }
            return null;
        }
    }
}
