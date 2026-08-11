using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 每连接终端配置——不同服务器可使用不同终端设置
    /// 存储在 ConnectionConfig.Metadata["terminalProfile"] 中
    /// </summary>
    public class TerminalProfile
    {
        /// <summary>
        /// 编码（如 UTF-8, GBK, ISO-8859-1）
        /// </summary>
        public string Encoding { get; set; } = "UTF-8";

        /// <summary>
        /// 终端类型（如 xterm-256color, xterm, vt100）
        /// </summary>
        public string TerminalType { get; set; } = "xterm-256color";

        /// <summary>
        /// 颜色方案名称（对应 TerminalColorScheme）
        /// </summary>
        public string ColorScheme { get; set; } = "Classic";

        /// <summary>
        /// 回滚缓冲行数
        /// </summary>
        /// <summary>
        /// 回滚行数。默认 300（低配友好）；UI 硬顶 2000。
        /// 低内存机靠此门禁 + 非活动 tab Pause，不需要改 Renderer。
        /// </summary>
        public int ScrollbackLines { get; set; } = 300;

        /// <summary>
        /// 字体名称
        /// </summary>
        public string FontName { get; set; } = "Consolas";

        /// <summary>
        /// 字体大小（0=未显式设置，由 GlobalAppearance 决定）
        /// </summary>
        public int FontSize { get; set; } = 0;

        /// <summary>
        /// 换行符（LF / CRLF / CR）
        /// </summary>
        public string NewLineSequence { get; set; } = "\n";

        /// <summary>
        /// 光标样式（Block / Underline / Bar）
        /// </summary>
        public string CursorStyle { get; set; } = "Block";

        /// <summary>
        /// 光标闪烁
        /// </summary>
        public bool CursorBlink { get; set; } = true;

        /// <summary>
        /// 复制时去除尾部空白
        /// </summary>
        public bool TrimTrailingWhitespace { get; set; } = true;

        /// <summary>
        /// 鼠标选中即复制
        /// </summary>
        public bool CopyOnSelect { get; set; } = false;

        /// <summary>
        /// 终端透明度（0.3 - 1.0）
        /// </summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// 连接后自动发送的命令（每行一条）
        /// </summary>
        public List<string> AutoRunCommands { get; set; } = new List<string>();

        /// <summary>
        /// 是否启用会话自动日志（默认 false，省磁盘/内存）
        /// </summary>
        public bool AutoLog { get; set; } = false;

        /// <summary>
        /// 渲染器：VtCell（真彩/TUI，默认）或 Lightweight（16 色行缓冲，极低内存）
        /// Metadata: renderer=vtcell|lightweight 或 terminalProfile.renderer
        /// </summary>
        /// <summary>
        /// 渲染后端。默认 VtCell（真彩/TUI）。
        /// Lightweight 仅作紧急回退（Metadata renderer=lightweight），
        /// 低配机请优先降 scrollback，不要默认切 Lightweight。
        /// </summary>
        public string Renderer { get; set; } = "VtCell";

        /// <summary>是否使用 cell VT 引擎（真彩 / alt-screen / TUI）。</summary>
        public bool UseVtCell
        {
            get
            {
                if (string.IsNullOrEmpty(Renderer)) return true;
                var r = Renderer.Trim();
                if (string.Equals(r, "Lightweight", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r, "legacy", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r, "line", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }
        }

        /// <summary>
        /// 环境变量（连接后 export 到终端）
        /// </summary>
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 从 ConnectionConfig.Metadata 反序列化
        /// </summary>
        public static TerminalProfile FromMetadata(Dictionary<string, string> metadata)
        {
            TerminalProfile profile;
            if (metadata == null)
                return new TerminalProfile();

            if (metadata.ContainsKey("terminalProfile"))
            {
                var json = metadata["terminalProfile"];
                profile = new TerminalProfile
                {
                    Encoding = ExtractValue(json, "encoding", "UTF-8"),
                    TerminalType = ExtractValue(json, "terminalType", "xterm-256color"),
                    ColorScheme = ExtractValue(json, "colorScheme", "Classic"),
                    ScrollbackLines = ExtractInt(json, "scrollbackLines", 300),
                    FontName = ExtractValue(json, "fontName", "Consolas"),
                    FontSize = ExtractInt(json, "fontSize", 0),
                    NewLineSequence = ExtractValue(json, "newLineSequence", "\n"),
                    CursorStyle = ExtractValue(json, "cursorStyle", "Block"),
                    CursorBlink = ExtractBool(json, "cursorBlink", true),
                    TrimTrailingWhitespace = ExtractBool(json, "trimTrailingWhitespace", true),
                    CopyOnSelect = ExtractBool(json, "copyOnSelect", false),
                    Opacity = ExtractDouble(json, "opacity", 1.0),
                    AutoLog = ExtractBool(json, "autoLog", false),
                    Renderer = ExtractValue(json, "renderer", "VtCell")
                };
            }
            else
            {
                profile = new TerminalProfile();
            }

            // 简易开关：Metadata["autoLog"]=true 也可打开（无需完整 terminalProfile JSON）
            if (metadata.ContainsKey("autoLog"))
            {
                var v = metadata["autoLog"];
                if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1")
                    profile.AutoLog = true;
                else if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || v == "0")
                    profile.AutoLog = false;
            }

            // Metadata["renderer"]=lightweight|vtcell 覆盖 JSON
            if (metadata.ContainsKey("renderer") && !string.IsNullOrWhiteSpace(metadata["renderer"]))
                profile.Renderer = metadata["renderer"].Trim();

            return profile;
        }

        /// <summary>
        /// 序列化到 ConnectionConfig.Metadata
        /// </summary>
        public string ToJson()
        {
            return "{" +
                $"\"encoding\":\"{Escape(Encoding)}\"," +
                $"\"terminalType\":\"{Escape(TerminalType)}\"," +
                $"\"colorScheme\":\"{Escape(ColorScheme)}\"," +
                $"\"scrollbackLines\":{ScrollbackLines}," +
                $"\"fontName\":\"{Escape(FontName)}\"," +
                $"\"fontSize\":{FontSize}," +
                $"\"newLineSequence\":\"{Escape(NewLineSequence)}\"," +
                $"\"cursorStyle\":\"{Escape(CursorStyle)}\"," +
                $"\"cursorBlink\":{(CursorBlink ? "true" : "false")}," +
                $"\"trimTrailingWhitespace\":{(TrimTrailingWhitespace ? "true" : "false")}," +
                $"\"copyOnSelect\":{(CopyOnSelect ? "true" : "false")}," +
                $"\"opacity\":{Opacity:F2}," +
                $"\"autoLog\":{(AutoLog ? "true" : "false")}," +
                $"\"renderer\":\"{Escape(Renderer)}\"" +
                "}";
        }

        private static string ExtractValue(string json, string key, string def)
        {
            var pattern = $"\"{key}\":\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return def;
            int start = idx + pattern.Length;
            int end = json.IndexOf('"', start);
            if (end < 0) return def;
            return json.Substring(start, end - start);
        }

        private static int ExtractInt(string json, string key, int def)
        {
            var pattern = $"\"{key}\":";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return def;
            int start = idx + pattern.Length;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return def;
            return int.TryParse(json.Substring(start, end - start), out int val) ? val : def;
        }

        private static bool ExtractBool(string json, string key, bool def)
        {
            var pattern = $"\"{key}\":";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return def;
            int start = idx + pattern.Length;
            if (start + 4 <= json.Length && json.Substring(start, 4) == "true") return true;
            if (start + 5 <= json.Length && json.Substring(start, 5) == "false") return false;
            return def;
        }

        private static double ExtractDouble(string json, string key, double def)
        {
            var pattern = $"\"{key}\":";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return def;
            int start = idx + pattern.Length;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
            if (end == start) return def;
            return double.TryParse(json.Substring(start, end - start), out double val) ? val : def;
        }

        private static string Escape(string s)
        {
            return s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";
        }
    }
}
