using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;

namespace Gdterm.UI.Services
{
    /// <summary>一次更新检查的结果。</summary>
    public sealed class UpdateInfo
    {
        /// <summary>release tag（如 v0.1.366）。</summary>
        public string Tag { get; set; }
        /// <summary>规范化版本（去掉前导 v）。</summary>
        public string Version { get; set; }
        /// <summary>release 标题。</summary>
        public string Name { get; set; }
        /// <summary>release 说明（已截断）。</summary>
        public string Body { get; set; }
        /// <summary>发布页 URL。</summary>
        public string HtmlUrl { get; set; }
        /// <summary>是否比当前版本新。</summary>
        public bool IsNewer { get; set; }
        /// <summary>失败原因（成功为 null）。</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 更新检查（P2-9）：查 GitHub releases/latest，静默失败——网络不通/被墙/限流都不该打扰用户。
    /// 目标框架是 .NET Framework 4.6.2，Win7 上默认不开 TLS1.2，这里显式打开。
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>默认仓库（与 AppVeyor 项目一致）。</summary>
        public const string DefaultRepo = "aerostone/gdterm";

        private static string s_current;

        /// <summary>当前程序版本（InformationalVersion 优先，取不到退回 AssemblyVersion）。</summary>
        public static string CurrentVersion
        {
            get
            {
                if (s_current != null) return s_current;
                string v = null;
                try
                {
                    var asm = Assembly.GetEntryAssembly() ?? typeof(UpdateChecker).Assembly;
                    var info = Attribute.GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute))
                               as AssemblyInformationalVersionAttribute;
                    if (info != null && !string.IsNullOrEmpty(info.InformationalVersion)) v = info.InformationalVersion;
                    if (string.IsNullOrEmpty(v))
                    {
                        var ver = asm.GetName().Version;
                        if (ver != null) v = ver.ToString();
                    }
                }
                catch (Exception) { }
                s_current = string.IsNullOrEmpty(v) ? "0.0.0" : v.Trim();
                return s_current;
            }
        }

        /// <summary>查最新 release。永不抛，失败时返回带 Error 的结果。</summary>
        public static async Task<UpdateInfo> CheckAsync(string repo, int timeoutSeconds)
        {
            var result = new UpdateInfo();
            repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
            try
            {
                EnableTls12();
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds)) })
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "gdterm-update-check");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    string url = "https://api.github.com/repos/" + repo + "/releases/latest";
                    var resp = await http.GetAsync(url).ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        result.Error = L10n.T("仓库没有 release");
                        return result;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        result.Error = "HTTP " + (int)resp.StatusCode;
                        return result;
                    }
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    result.Tag = ReadString(json, "tag_name");
                    result.Name = ReadString(json, "name");
                    result.HtmlUrl = ReadString(json, "html_url");
                    string body = ReadString(json, "body");
                    if (!string.IsNullOrEmpty(body) && body.Length > 4000) body = body.Substring(0, 4000) + "\n…";
                    result.Body = body;
                    result.Version = Normalize(result.Tag);
                    if (string.IsNullOrEmpty(result.Version))
                    {
                        result.Error = L10n.T("release 里没有版本号");
                        return result;
                    }
                    result.IsNewer = CompareVersions(result.Version, CurrentVersion) > 0;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        private static void EnableTls12()
        {
            try
            {
                // Win7/.NET 4.6.2 默认可能只有 TLS1.0，GitHub 早就拒了
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch (Exception) { }
        }

        /// <summary>去掉前导 v / 多余空白。</summary>
        public static string Normalize(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var t = tag.Trim();
            if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);
            return t.Trim();
        }

        /// <summary>点分版本比较：缺位按 0 算，非数字后缀（-beta）忽略。返回 &gt;0 表示 a 更新。</summary>
        public static int CompareVersions(string a, string b)
        {
            var pa = SplitVersion(a);
            var pb = SplitVersion(b);
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? pa[i] : 0;
                int y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        private static int[] SplitVersion(string v)
        {
            var list = new System.Collections.Generic.List<int>();
            if (string.IsNullOrEmpty(v)) return list.ToArray();
            foreach (var part in Normalize(v).Split('.'))
            {
                int value = 0;
                int i = 0;
                while (i < part.Length && char.IsDigit(part[i])) i++;
                if (i > 0) int.TryParse(part.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                list.Add(value);
                if (i < part.Length && (part[i] == '-' || part[i] == '+')) break;   // 预发布后缀：忽略
            }
            return list.ToArray();
        }

        /// <summary>从 GitHub 的 JSON 里抠一个字符串字段（不引第三方 JSON 库，避免依赖膨胀）。</summary>
        private static string ReadString(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return null;
            var m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return Unescape(m.Groups[1].Value);
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                                break;
                            }
                        }
                        sb.Append(n);
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}
