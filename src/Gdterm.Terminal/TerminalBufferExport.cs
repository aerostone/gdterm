using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端缓冲区导出工具——导出为文本/HTML格式
    /// </summary>
    public static class TerminalBufferExport
    {
        /// <summary>导出为纯文本</summary>
        public static string ExportAsText(List<string> lines, string hostName = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# gdterm terminal output");
            if (!string.IsNullOrEmpty(hostName)) sb.AppendLine("# Host: " + hostName);
            sb.AppendLine("# Exported: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("# ─────────────────────────────────────");
            sb.AppendLine();
            if (lines != null)
                foreach (var line in lines) sb.AppendLine(line);
            return sb.ToString();
        }

        /// <summary>导出为 HTML（带颜色）</summary>
        public static string ExportAsHtml(List<string> lines, string hostName = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>gdterm terminal export</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { background: #1e1e1e; color: #cccccc; font-family: 'Consolas','Courier New',monospace; font-size: 13px; padding: 20px; }");
            sb.AppendLine(".header { color: #808080; margin-bottom: 16px; font-size: 12px; }");
            sb.AppendLine("pre { margin: 0; white-space: pre-wrap; word-wrap: break-word; }");
            sb.AppendLine(".line { padding: 1px 0; }");
            sb.AppendLine(".error { color: #ff4444; font-weight: bold; }");
            sb.AppendLine(".warn { color: #ffd700; }");
            sb.AppendLine(".success { color: #00cc00; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class='header'>");
            sb.AppendFormat("<p>Host: {0} | Exported: {1}</p>\n", hostName ?? "local", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("</div><pre>");

            if (lines != null)
            {
                foreach (var line in lines)
                {
                    var cssClass = "";
                    if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("fatal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                        cssClass = " class='error'";
                    else if (line.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0)
                        cssClass = " class='warn'";
                    else if (line.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0)
                        cssClass = " class='success'";

                    sb.AppendFormat("<div class='line'{0}>{1}</div>\n", cssClass, System.Net.WebUtility.HtmlEncode(line));
                }
            }

            sb.AppendLine("</pre></body></html>");
            return sb.ToString();
        }

        /// <summary>保存到文件</summary>
        public static void SaveToFile(string content, string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, content, Encoding.UTF8);
        }
    }
}
