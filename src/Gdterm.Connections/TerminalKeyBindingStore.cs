using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 终端快捷键存储——内置预设 + 用户自定义绑定，持久化到 JSON 文件
    /// </summary>
    public class TerminalKeyBindingStore
    {
        private readonly string _configPath;
        private TerminalKeyBindingConfig _config;

        public TerminalKeyBindingStore(string configPath)
        {
            _configPath = configPath;
        }

        public TerminalKeyBindingConfig Load()
        {
            if (_config != null) return _config;

            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath, Encoding.UTF8);
                    _config = ParseConfig(json);
                }
                catch
                {
                    _config = CreateDefaultConfig();
                }
            }
            else
            {
                _config = CreateDefaultConfig();
                Save();
            }

            // 确保内置预设始终存在
            EnsureBuiltinPresets(_config);
            return _config;
        }

        public void Save()
        {
            if (_config == null) return;
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_configPath, SerializeConfig(_config), Encoding.UTF8);
        }

        public void Reload()
        {
            _config = null;
            Load();
        }

        /// <summary>根据活动预设 + 自定义绑定，获取当前生效的绑定列表</summary>
        public List<TerminalKeyBinding> GetActiveBindings()
        {
            var config = Load();
            var result = new List<TerminalKeyBinding>();

            // 找到活动预设
            foreach (var preset in config.Presets)
            {
                if (string.Equals(preset.Name, config.ActivePreset, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var b in preset.Bindings)
                    {
                        if (b.Enabled) result.Add(b);
                    }
                    break;
                }
            }

            // 追加自定义绑定（覆盖同名预设绑定）
            var usedCombos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in result) usedCombos.Add(b.GetKeyCombo());

            foreach (var b in config.CustomBindings)
            {
                if (b.Enabled && !usedCombos.Contains(b.GetKeyCombo()))
                {
                    result.Add(b);
                }
            }

            return result;
        }

        /// <summary>添加自定义绑定</summary>
        public void AddCustomBinding(TerminalKeyBinding binding)
        {
            var config = Load();
            config.CustomBindings.Add(binding);
            Save();
        }

        /// <summary>删除自定义绑定</summary>
        public void RemoveCustomBinding(string keyCombo)
        {
            var config = Load();
            config.CustomBindings.RemoveAll(b => string.Equals(b.GetKeyCombo(), keyCombo, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        /// <summary>切换活动预设</summary>
        public void SetActivePreset(string presetName)
        {
            var config = Load();
            config.ActivePreset = presetName;
            Save();
        }

        // ── 内置预设 ──

        private static TerminalKeyBindingConfig CreateDefaultConfig()
        {
            var config = new TerminalKeyBindingConfig { ActivePreset = "tmux" };
            EnsureBuiltinPresets(config);
            return config;
        }

        private static void EnsureBuiltinPresets(TerminalKeyBindingConfig config)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in config.Presets) existing.Add(p.Name);

            if (!existing.Contains("tmux"))
                config.Presets.Add(CreateTmuxPreset());
            if (!existing.Contains("screen"))
                config.Presets.Add(CreateScreenPreset());
            if (!existing.Contains("terminal"))
                config.Presets.Add(CreateTerminalPreset());
            if (!existing.Contains("none"))
                config.Presets.Add(CreateNonePreset());
        }

        /// <summary>tmux 预设——Ctrl+B 前缀键 + 常用快捷键</summary>
        private static KeyBindingPreset CreateTmuxPreset()
        {
            return new KeyBindingPreset
            {
                Name = "tmux",
                Description = "tmux 快捷键（前缀键 Ctrl+B）",
                Bindings = new List<TerminalKeyBinding>
                {
                    // ── 前缀键发送 ──
                    B("发送前缀 Ctrl+B", true, false, false, "B", SendType.Sequence, "\x02", "tmux", "发送 tmux 前缀键"),

                    // ── 会话管理 ──
                    B("分离会话", true, false, false, "D", SendType.Sequence, "\x02\x64", "tmux", "Ctrl+B d = 分离会话"),
                    B("会话列表", true, false, false, "S", SendType.Sequence, "\x02\x73", "tmux", "Ctrl+B s = 会话列表"),
                    B("重命名会话", true, false, false, "R", SendType.Sequence, "\x02\x24", "tmux", "Ctrl+B $ = 重命名会话"),

                    // ── 窗口管理 ──
                    B("新建窗口", true, false, false, "C", SendType.Sequence, "\x02\x63", "tmux", "Ctrl+B c = 新建窗口"),
                    B("下一个窗口", true, false, false, "N", SendType.Sequence, "\x02\x6e", "tmux", "Ctrl+B n = 下一个窗口"),
                    B("上一个窗口", true, false, false, "P", SendType.Sequence, "\x02\x70", "tmux", "Ctrl+B p = 上一个窗口"),
                    B("窗口列表", true, false, false, "W", SendType.Sequence, "\x02\x77", "tmux", "Ctrl+B w = 窗口列表"),
                    B("切换窗口", true, false, false, "L", SendType.Sequence, "\x02\x6c", "tmux", "Ctrl+B l = 切换到上一个窗口"),
                    B("关闭窗口", true, false, false, "X", SendType.Sequence, "\x02\x78", "tmux", "Ctrl+B x = 关闭窗口"),

                    // ── 面板分割 ──
                    B("垂直分割", true, false, false, "Oem5", SendType.Sequence, "\x02\x25", "tmux", "Ctrl+B % = 垂直分割"),
                    B("水平分割", true, false, true, "Oem7", SendType.Sequence, "\x02\x22", "tmux", "Ctrl+B \" = 水平分割"),
                    B("切换面板", true, false, false, "O", SendType.Sequence, "\x02\x6f", "tmux", "Ctrl+B o = 切换面板"),
                    B("关闭面板", true, false, false, "X", SendType.Sequence, "\x02\x78", "tmux", "Ctrl+B x = 关闭面板"),

                    // ── 面块导航（方向键） ──
                    B("面板-上", true, true, false, "Up", SendType.Sequence, "\x02\x1b[1;3A", "tmux", "Ctrl+B Alt+↑ = 选择上方面板"),
                    B("面板-下", true, true, false, "Down", SendType.Sequence, "\x02\x1b[1;3B", "tmux", "Ctrl+B Alt+↓ = 选择下方面板"),
                    B("面板-左", true, true, false, "Left", SendType.Sequence, "\x02\x1b[1;3D", "tmux", "Ctrl+B Alt+← = 选择左方面板"),
                    B("面板-右", true, true, false, "Right", SendType.Sequence, "\x02\x1b[1;3C", "tmux", "Ctrl+B Alt+→ = 选择右方面板"),

                    // ── 复制模式 ──
                    B("进入复制模式", true, false, false, "OemOpenBrackets", SendType.Sequence, "\x02\x5b", "tmux", "Ctrl+B [ = 进入复制模式"),
                    B("粘贴缓冲区", true, false, false, "OemCloseBrackets", SendType.Sequence, "\x02\x5d", "tmux", "Ctrl+B ] = 粘贴"),

                    // ── 布局 ──
                    B("均衡布局", true, false, false, "E", SendType.Sequence, "\x02\x45", "tmux", "Ctrl+B E = 均衡面板"),
                    B("全屏面板", true, false, false, "Z", SendType.Sequence, "\x02\x7a", "tmux", "Ctrl+B z = 全屏/恢复面板"),

                    // ── 其他 ──
                    B("命令模式", true, false, false, "OemSemicolon", SendType.Sequence, "\x02\x3a", "tmux", "Ctrl+B : = 进入命令模式"),
                    B("帮助", true, false, false, "OemQuestion", SendType.Sequence, "\x02\x3f", "tmux", "Ctrl+B ? = 快捷键帮助"),
                }
            };
        }

        /// <summary>screen 预设——Ctrl+A 前缀键</summary>
        private static KeyBindingPreset CreateScreenPreset()
        {
            return new KeyBindingPreset
            {
                Name = "screen",
                Description = "GNU Screen 快捷键（前缀键 Ctrl+A）",
                Bindings = new List<TerminalKeyBinding>
                {
                    B("发送前缀 Ctrl+A", true, false, false, "A", SendType.Sequence, "\x01", "screen", "发送 screen 前缀键"),
                    B("分离会话", true, false, false, "D", SendType.Sequence, "\x01\x64", "screen", "Ctrl+A d = 分离"),
                    B("新建窗口", true, false, false, "C", SendType.Sequence, "\x01\x63", "screen", "Ctrl+A c = 新建窗口"),
                    B("下一个窗口", true, false, false, "N", SendType.Sequence, "\x01\x6e", "screen", "Ctrl+A n = 下一个"),
                    B("上一个窗口", true, false, false, "P", SendType.Sequence, "\x01\x70", "screen", "Ctrl+A p = 上一个"),
                    B("窗口列表", true, false, false, "W", SendType.Sequence, "\x01\x77", "screen", "Ctrl+A w = 窗口列表"),
                    B("水平分割", true, false, false, "S", SendType.Sequence, "\x01\x73", "screen", "Ctrl+A S = 水平分割"),
                    B("垂直分割", true, false, false, "V", SendType.Sequence, "\x01\x76", "screen", "Ctrl+A V = 垂直分割"),
                    B("切换面板", true, false, false, "Tab", SendType.Sequence, "\x01\x09", "screen", "Ctrl+A Tab = 切换面板"),
                    B("复制模式", true, false, false, "OemOpenBrackets", SendType.Sequence, "\x01\x5b", "screen", "Ctrl+A [ = 复制模式"),
                    B("粘贴", true, false, false, "OemCloseBrackets", SendType.Sequence, "\x01\x5d", "screen", "Ctrl+A ] = 粘贴"),
                    B("命令模式", true, false, false, "OemSemicolon", SendType.Sequence, "\x01\x3a", "screen", "Ctrl+A : = 命令"),
                    B("关闭面板", true, false, false, "K", SendType.Sequence, "\x01\x6b", "screen", "Ctrl+A K = 关闭"),
                    B("帮助", true, false, false, "OemQuestion", SendType.Sequence, "\x01\x3f", "screen", "Ctrl+A ? = 帮助"),
                }
            };
        }

        /// <summary>终端增强预设——常用终端操作</summary>
        private static KeyBindingPreset CreateTerminalPreset()
        {
            return new KeyBindingPreset
            {
                Name = "terminal",
                Description = "终端增强（复制/粘贴/滚动等）",
                Bindings = new List<TerminalKeyBinding>
                {
                    // 内置动作
                    B("复制", true, false, false, "C", SendType.Action, "copy", "terminal", "复制选中文本"),
                    B("粘贴", true, false, false, "V", SendType.Action, "paste", "terminal", "粘贴剪贴板"),
                    B("向上滚动", true, true, false, "Up", SendType.Action, "scroll_up", "terminal", "滚动历史"),
                    B("向下滚动", true, true, false, "Down", SendType.Action, "scroll_down", "terminal", "滚动历史"),
                    B("清除屏幕", true, false, false, "L", SendType.Action, "clear", "terminal", "清除终端"),
                    B("查找", true, false, false, "F", SendType.Action, "find", "terminal", "搜索终端内容"),
                }
            };
        }

        /// <summary>无绑定预设——所有按键直接发送到终端</summary>
        private static KeyBindingPreset CreateNonePreset()
        {
            return new KeyBindingPreset
            {
                Name = "none",
                Description = "无快捷键（所有按键直接发送到终端）",
                Bindings = new List<TerminalKeyBinding>()
            };
        }

        private static TerminalKeyBinding B(string name, bool ctrl, bool alt, bool shift, string key,
            SendType type, string value, string group, string desc)
        {
            return new TerminalKeyBinding
            {
                Name = name,
                Ctrl = ctrl, Alt = alt, Shift = shift, Key = key,
                Type = type, Value = value, Group = group, Description = desc
            };
        }

        // ── 手工 JSON 序列化 ──

        private string SerializeConfig(TerminalKeyBindingConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendFormat("  \"activePreset\": \"{0}\",\n", Escape(config.ActivePreset));
            sb.AppendFormat("  \"interceptMode\": {0},\n", config.InterceptMode ? "true" : "false");
            sb.AppendLine("  \"customBindings\": [");
            for (int i = 0; i < config.CustomBindings.Count; i++)
            {
                SerializeBinding(sb, config.CustomBindings[i], "    ");
                if (i < config.CustomBindings.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"presets\": [");
            for (int i = 0; i < config.Presets.Count; i++)
            {
                var preset = config.Presets[i];
                sb.AppendLine("    {");
                sb.AppendFormat("      \"name\": \"{0}\",\n", Escape(preset.Name));
                sb.AppendFormat("      \"description\": \"{0}\",\n", Escape(preset.Description));
                sb.AppendLine("      \"bindings\": [");
                for (int j = 0; j < preset.Bindings.Count; j++)
                {
                    SerializeBinding(sb, preset.Bindings[j], "        ");
                    if (j < preset.Bindings.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine("      ]");
                sb.Append("    }");
                if (i < config.Presets.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.Append("}");
            return sb.ToString();
        }

        private void SerializeBinding(StringBuilder sb, TerminalKeyBinding b, string indent)
        {
            sb.Append(indent + "{");
            sb.AppendFormat("\"name\":\"{0}\",", Escape(b.Name));
            sb.AppendFormat("\"ctrl\":{0},", b.Ctrl ? "true" : "false");
            sb.AppendFormat("\"alt\":{0},", b.Alt ? "true" : "false");
            sb.AppendFormat("\"shift\":{0},", b.Shift ? "true" : "false");
            sb.AppendFormat("\"key\":\"{0}\",", Escape(b.Key));
            sb.AppendFormat("\"type\":\"{0}\",", b.Type.ToString().ToLower());
            sb.AppendFormat("\"value\":\"{0}\",", EscapeValue(b.Value));
            sb.AppendFormat("\"enabled\":{0},", b.Enabled ? "true" : "false");
            sb.AppendFormat("\"group\":\"{0}\",", Escape(b.Group));
            sb.AppendFormat("\"description\":\"{0}\"", Escape(b.Description));
            sb.Append("}");
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string EscapeValue(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\x1b') sb.Append("\\u001b");
                else if (c == '\x02') sb.Append("\\u0002");
                else if (c == '\x01') sb.Append("\\u0001");
                else if (c == '\x03') sb.Append("\\u0003");
                else if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ── 手工 JSON 解析 ──

        private TerminalKeyBindingConfig ParseConfig(string json)
        {
            var config = new TerminalKeyBindingConfig();
            config.ActivePreset = ExtractString(json, "activePreset") ?? "tmux";
            config.InterceptMode = json.Contains("\"interceptMode\": true");

            // 解析 customBindings 数组
            var customBlock = ExtractArrayBlock(json, "customBindings");
            if (customBlock != null) config.CustomBindings = ParseBindings(customBlock);

            // 解析 presets 数组
            var presetsBlock = ExtractArrayBlock(json, "presets");
            if (presetsBlock != null) config.Presets = ParsePresets(presetsBlock);

            return config;
        }

        private List<KeyBindingPreset> ParsePresets(string arrayContent)
        {
            var presets = new List<KeyBindingPreset>();
            int depth = 0;
            int start = -1;
            for (int i = 0; i < arrayContent.Length; i++)
            {
                if (arrayContent[i] == '{') { if (depth == 0) start = i; depth++; }
                else if (arrayContent[i] == '}') { depth--; if (depth == 0 && start >= 0) { presets.Add(ParsePreset(arrayContent.Substring(start, i - start + 1))); start = -1; } }
            }
            return presets;
        }

        private KeyBindingPreset ParsePreset(string obj)
        {
            var preset = new KeyBindingPreset();
            preset.Name = ExtractString(obj, "name");
            preset.Description = ExtractString(obj, "description");
            var bindingsBlock = ExtractArrayBlock(obj, "bindings");
            if (bindingsBlock != null) preset.Bindings = ParseBindings(bindingsBlock);
            return preset;
        }

        private List<TerminalKeyBinding> ParseBindings(string arrayContent)
        {
            var bindings = new List<TerminalKeyBinding>();
            int depth = 0;
            int start = -1;
            for (int i = 0; i < arrayContent.Length; i++)
            {
                if (arrayContent[i] == '{') { if (depth == 0) start = i; depth++; }
                else if (arrayContent[i] == '}') { depth--; if (depth == 0 && start >= 0) { bindings.Add(ParseBinding(arrayContent.Substring(start, i - start + 1))); start = -1; } }
            }
            return bindings;
        }

        private TerminalKeyBinding ParseBinding(string obj)
        {
            return new TerminalKeyBinding
            {
                Name = ExtractString(obj, "name"),
                Ctrl = obj.Contains("\"ctrl\": true") || obj.Contains("\"ctrl\":true"),
                Alt = obj.Contains("\"alt\": true") || obj.Contains("\"alt\":true"),
                Shift = obj.Contains("\"shift\": true") || obj.Contains("\"shift\":true"),
                Key = ExtractString(obj, "key"),
                Type = ParseSendType(ExtractString(obj, "type")),
                Value = ParseValueString(ExtractString(obj, "value")),
                Enabled = !obj.Contains("\"enabled\": false") && !obj.Contains("\"enabled\":false"),
                Group = ExtractString(obj, "group"),
                Description = ExtractString(obj, "description")
            };
        }

        private static SendType ParseSendType(string s)
        {
            if (string.Equals(s, "text", StringComparison.OrdinalIgnoreCase)) return SendType.Text;
            if (string.Equals(s, "action", StringComparison.OrdinalIgnoreCase)) return SendType.Action;
            return SendType.Sequence;
        }

        private static string ParseValueString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    if (next == '\\' || next == '"') { sb.Append(next); i++; }
                    else if (next == 'n') { sb.Append('\n'); i++; }
                    else if (next == 'r') { sb.Append('\r'); i++; }
                    else if (next == 't') { sb.Append('\t'); i++; }
                    else if (next == 'u' && i + 5 < s.Length)
                    {
                        int cp;
                        if (int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out cp))
                        {
                            sb.Append((char)cp);
                            i += 5;
                        }
                    }
                    else sb.Append(s[i]);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string ExtractString(string json, string key)
        {
            var token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += token.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\t')) idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++;
            int end = idx;
            while (end < json.Length)
            {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            var raw = json.Substring(idx, end - idx);
            return raw.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
        }

        private static string ExtractArrayBlock(string json, string key)
        {
            var token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += token.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\t' || json[idx] == '\n' || json[idx] == '\r')) idx++;
            if (idx >= json.Length || json[idx] != '[') return null;
            int depth = 0;
            int start = idx;
            for (int i = idx; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') { depth--; if (depth == 0) return json.Substring(start + 1, i - start - 1); }
            }
            return null;
        }
    }
}
