using System;
using System.Collections.Generic;
using System.IO;
using Gdterm.Core;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 通用窗体偏好持久化（feature-map §4 横切补「配置持久化」缺口，2026-11-08）。
    ///
    /// 与 AppearanceSettings 同一 INI 风格（key=value、分节），但按页面分节、
    /// 无需每页写一个 POCO：data/config/form-settings.ini 里
    /// <c>[password-generator] length=16</c> / <c>[toolbox] lastTool=ping</c> 这类。
    ///
    /// 规则：
    ///   - 只存非敏感偏好（长度/字号/上次选择）。密码、令牌、路径凭据一律不进这里。
    ///   - 读：文件缺/键缺/值非法 → 返回调用方给定的默认值，绝不抛。
    ///   - 写：整文件读-改-写，其他分节原样保留；失败静默吞（DiagLog.Swallowed）。
    /// </summary>
    public static class FormSettings
    {
        private static readonly object _gate = new object();

        /// <summary>默认落盘位置（与 Program 的 configDir 同一约定）。</summary>
        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "data", "config", "form-settings.ini");
            }
        }

        /// <summary>取字符串偏好；缺省返回 def。</summary>
        public static string Get(string section, string key, string def)
        {
            var v = ReadValue(section, key);
            return string.IsNullOrEmpty(v) ? def : v;
        }

        /// <summary>取整数偏好；越界/非法回 def。</summary>
        public static int GetInt(string section, string key, int def, int min, int max)
        {
            var v = ReadValue(section, key);
            int n;
            return int.TryParse(v, out n) && n >= min && n <= max ? n : def;
        }

        /// <summary>取布尔偏好（1/true=真）。</summary>
        public static bool GetBool(string section, string key, bool def)
        {
            var v = ReadValue(section, key);
            if (string.IsNullOrEmpty(v)) return def;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>写一个偏好值（整文件读-改-写，保留其他分节）。失败静默。</summary>
        public static void Set(string section, string key, string value)
        {
            try
            {
                lock (_gate)
                {
                    var path = DefaultPath;
                    var lines = new List<string>();
                    if (File.Exists(path))
                    {
                        foreach (var raw in File.ReadAllLines(path))
                            lines.Add(raw);
                    }
                    string cur = null;
                    int idx = -1;
                    int sectionIdx = -1;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var line = lines[i].Trim();
                        if (line.StartsWith("[") && line.EndsWith("]"))
                        {
                            cur = line.Substring(1, line.Length - 2).Trim();
                            if (string.Equals(cur, section, StringComparison.OrdinalIgnoreCase))
                                sectionIdx = i;
                            continue;
                        }
                        if (cur != null &&
                            string.Equals(cur, section, StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq > 0 &&
                                string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                            {
                                idx = i;
                                break;
                            }
                        }
                    }
                    var kv = key + "=" + value;
                    if (idx >= 0)
                    {
                        lines[idx] = kv;
                    }
                    else
                    {
                        // 键不存在：优先追加到该分节末尾；分节不存在则文件尾建新分节
                        int insertAt;
                        if (sectionIdx >= 0)
                        {
                            insertAt = sectionIdx + 1;
                            while (insertAt < lines.Count)
                            {
                                var t = lines[insertAt].Trim();
                                if (t.StartsWith("[") && t.EndsWith("]")) break;
                                if (t.Length > 0 && !t.StartsWith(";") && !t.StartsWith("#"))
                                    insertAt++;
                            }
                        }
                        else
                        {
                            if (lines.Count > 0) lines.Add("");
                            lines.Add("[" + section + "]");
                            insertAt = lines.Count;
                        }
                        lines.Insert(insertAt, kv);
                    }
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
                }
            }
            catch (Exception exSwallowed)
            {
                try { Gdterm.UI.Diagnostics.DiagLog.Swallowed("FormSettings.Set", exSwallowed); } catch { }
            }
        }

        private static string ReadValue(string section, string key)
        {
            try
            {
                lock (_gate)
                {
                    var path = DefaultPath;
                    if (!File.Exists(path)) return null;
                    string cur = null;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("[") && line.EndsWith("]"))
                        {
                            cur = line.Substring(1, line.Length - 2).Trim();
                            continue;
                        }
                        if (!string.Equals(cur, section, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                            return line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception exSwallowed)
            {
                try { Gdterm.UI.Diagnostics.DiagLog.Swallowed("FormSettings.ReadValue", exSwallowed); } catch { }
            }
            return null;
        }
    }
}
