using System;
using System.IO;

namespace Gdterm.UI
{
    /// <summary>
    /// 调试配置——读写 data/config/debug.ini，控制连接对话框中的调试选项显隐。
    /// 切换即时生效，无需重启。
    /// </summary>
    public sealed class DebugConfig
    {
        /// <summary>调试模式：开启后连接对话框显示「抓包」等调试选项。</summary>
        public bool Enabled { get; set; }

        public static DebugConfig Load(string path)
        {
            var s = new DebugConfig();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return s;
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                        continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();
                    if (string.Equals(key, "enabled", StringComparison.OrdinalIgnoreCase))
                        s.Enabled = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return s;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                "[debug]\r\n" +
                "enabled=" + (Enabled ? "1" : "0") + "\r\n");
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "data", "config", "debug.ini");
            }
        }
    }
}