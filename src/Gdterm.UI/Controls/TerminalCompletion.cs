using System;
using System.Collections.Generic;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 终端输入自动补全——候选来源：历史命令 + 快捷命令 + 内置常用。
    /// 纯逻辑（无 UI 依赖），由 TerminalControl 在 Tab 时调用。
    /// </summary>
    internal sealed class TerminalCompletion
    {
        private static readonly string[] Builtin = new string[]
        {
            "ls", "ll", "cd", "pwd", "cat", "less", "tail", "head", "grep", "find", "awk", "sed",
            "tar", "gzip", "unzip", "zip", "cp", "mv", "rm", "mkdir", "chmod", "chown", "df", "du",
            "ps", "top", "htop", "kill", "systemctl", "service", "journalctl", "ss", "netstat",
            "ping", "curl", "wget", "ssh", "scp", "rsync", "git", "docker", "kubectl", "vim", "nano",
            "echo", "export", "history", "sudo", "su", "exit", "clear", "whoami", "id", "uname"
        };

        private readonly Func<IList<string>> _historyProvider;
        private readonly Func<IList<string>> _quickProvider;

        public TerminalCompletion(Func<IList<string>> historyProvider = null, Func<IList<string>> quickProvider = null)
        {
            _historyProvider = historyProvider;
            _quickProvider = quickProvider;
        }

        /// <summary>按当前词前缀取候选（历史优先去重，最多 8 个）。返回补全后的整行+候选数。</summary>
        public List<string> Complete(string line, out string wordPrefix)
        {
            wordPrefix = LastWord(line);
            var result = new List<string>();
            if (string.IsNullOrEmpty(wordPrefix)) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddMatches(result, seen, _historyProvider != null ? Safe(_historyProvider) : null, wordPrefix, 8);
            if (result.Count < 8) AddMatches(result, seen, _quickProvider != null ? Safe(_quickProvider) : null, wordPrefix, 8);
            if (result.Count < 8) AddMatches(result, seen, Builtin, wordPrefix, 8);
            return result;
        }

        /// <summary>取当前行最后一个词（空格/分号/管道/&后）。</summary>
        public static string LastWord(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            int end = line.Length;
            while (end > 0 && char.IsWhiteSpace(line[end - 1])) end--;
            int start = end;
            while (start > 0 && !IsWordBreak(line[start - 1])) start--;
            return line.Substring(start, end - start);
        }

        private static bool IsWordBreak(char c)
        {
            return char.IsWhiteSpace(c) || c == ';' || c == '|' || c == '&' || c == '(' || c == ')';
        }

        private static IList<string> Safe(Func<IList<string>> p)
        {
            try { return p() ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static void AddMatches(List<string> into, HashSet<string> seen, IList<string> src, string prefix, int cap)
        {
            if (src == null) return;
            foreach (var s in src)
            {
                if (into.Count >= cap) return;
                if (string.IsNullOrEmpty(s)) continue;
                var cand = s.Trim();
                // 历史是整行命令：只取首词匹配，候选给整行
                var first = cand.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (first.Length == 0) continue;
                string key = first[0];
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length > prefix.Length && seen.Add(key))
                    into.Add(key);
            }
        }
    }
}
