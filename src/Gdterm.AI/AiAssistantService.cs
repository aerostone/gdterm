using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.AI.Models;
using Gdterm.Terminal;
using Gdterm.Core;

namespace Gdterm.AI
{
    /// <summary>
    /// AI 对话服务实现——OpenAI-compatible API 调用、连接上下文注入、命令提取
    /// </summary>
    public class AiAssistantService : IAiAssistantService, IDisposable
    {
        // F13：历史按连接键控（键 = ConnectionId；本地/无连接 = DefaultKey「global」）。
        // 活跃键由面板切标签时 SetActiveConversation 切换；发送链路再按会话键兜底对齐。
        private readonly Dictionary<string, List<ChatMessage>> _histories = new Dictionary<string, List<ChatMessage>>();
        private string _activeKey = ChatHistoryStore.DefaultKey;
        private readonly ChatHistoryStore _historyStore;
        /// <summary>对话历史硬顶（user+assistant 合计条数），防长会话内存/token 膨胀</summary>
        public int MaxHistoryMessages { get; set; } = 40;
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        private static readonly Regex CommandBlockRegex = new Regex(
            @"```(?:bash|sh|shell|zsh|powershell|cmd)\s*\r?\n(.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public AiConfiguration Configuration { get; set; }

        public AiAssistantService(AiConfiguration configuration)
            : this(configuration, null)
        {
        }

        /// <summary>
        /// 带历史持久化的构造（data/ai-chat-history.json，按连接键控；historyStore 为 null 时纯内存）。
        /// 历史首次访问某键时才读盘（懒加载）。
        /// </summary>
        public AiAssistantService(AiConfiguration configuration, ChatHistoryStore historyStore)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _historyStore = historyStore;
        }

        /// <summary>当前活动对话历史快照（只读副本）——供 UI 回显。</summary>
        public IList<ChatMessage> GetHistory()
        {
            lock (_histories)
            {
                EnsureActiveLoadedLocked();
                return new List<ChatMessage>(_histories[_activeKey]);
            }
        }

        /// <summary>
        /// F13：切换活动对话历史键（按连接隔离；空键 = global）。
        /// 首次切到某键时从盘加载（截断至硬顶）。幂等：同键无操作。
        /// </summary>
        public void SetActiveConversation(string key)
        {
            var k = string.IsNullOrEmpty(key) ? ChatHistoryStore.DefaultKey : key;
            lock (_histories)
            {
                if (k == _activeKey) return;
                _activeKey = k;
                EnsureActiveLoadedLocked();
            }
        }

        /// <summary>首次访问某键时读盘（须持有 _histories 锁）。</summary>
        private void EnsureActiveLoadedLocked()
        {
            List<ChatMessage> existing;
            if (_histories.TryGetValue(_activeKey, out existing)) return;
            var list = new List<ChatMessage>();
            if (_historyStore != null)
            {
                try
                {
                    var loaded = _historyStore.Load(_activeKey);
                    if (loaded != null) list.AddRange(loaded);
                }
                catch
                {
                    // 历史加载失败不阻断 AI 功能（store.Load 本身已吞异常，此处双保险）
                }
                var cap = MaxHistoryMessages > 0 ? MaxHistoryMessages : 40;
                if (list.Count > cap)
                    list = new List<ChatMessage>(list.GetRange(list.Count - cap, cap));
            }
            _histories[_activeKey] = list;
        }

        /// <summary>会话 → 对话键（ConnectionId；本地/未连接/取不到 = global）。</summary>
        private static string KeyOf(ITerminalSession session)
        {
            string id = null;
            try { id = session != null ? session.ConnectionId : null; } catch { }
            return string.IsNullOrEmpty(id) ? ChatHistoryStore.DefaultKey : id;
        }

        public async Task<AiResponse> SendMessageAsync(string message, ITerminalSession session, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(message))
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("消息不能为空") };

            if (Configuration == null || string.IsNullOrEmpty(Configuration.ApiEndpoint))
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("AI 服务未配置") };

            try
            {
                // 构建 system prompt（注入连接上下文）
                var systemPrompt = BuildSystemPrompt(session);

                // F13：发送前按本会话连接对齐活跃键（兜底——面板切标签时已切，此处防漏）
                SetActiveConversation(KeyOf(session));

                // 构建 messages 数组（带当前键的历史）
                List<ChatMessage> historySnapshot;
                lock (_histories)
                {
                    EnsureActiveLoadedLocked();
                    historySnapshot = new List<ChatMessage>(_histories[_activeKey]);
                }
                var messages = new List<ChatMessage>();
                messages.Add(new ChatMessage("system", systemPrompt));
                messages.AddRange(historySnapshot);
                messages.Add(new ChatMessage("user", message));

                // 调用 API
                var response = await CallOpenAiApiAsync(messages, ct);

                if (response.IsSuccess)
                {
                    // 添加到历史（带硬顶）
                    AppendHistory(new ChatMessage("user", message));
                    AppendHistory(new ChatMessage("assistant", response.Content));

                    // 提取命令
                    response.SuggestedCommands = ExtractCommands(response.Content);
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("请求已取消") };
            }
            catch (Exception ex)
            {
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("AI 服务调用失败: ") + ex.Message };
            }
        }

        public IList<string> ExtractCommands(string response)
        {
            if (string.IsNullOrEmpty(response))
                return new List<string>();

            var commands = new List<string>();
            var matches = CommandBlockRegex.Matches(response);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var block = match.Groups[1].Value.Trim();
                    // 按行拆分，每行一个命令（排除空行和注释）
                    foreach (var line in block.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                        {
                            commands.Add(trimmed);
                        }
                    }
                }
            }

            return commands;
        }

        /// <summary>
        /// 可选危险命令闸：返回 false 则拒绝下发。
        /// UI 应注入经 TerminalControl 确认的路径；未注入时仍直接 SendInput（兼容旧调用）。
        /// </summary>
        public Func<string, bool> CommandGate { get; set; }

        public void ExecuteCommand(ITerminalSession session, string command)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrEmpty(command)) throw new ArgumentNullException(nameof(command));

            var line = command.EndsWith("\n") || command.EndsWith("\r")
                ? command
                : command + "\n";

            if (CommandGate != null)
            {
                var bare = command.TrimEnd('\r', '\n');
                if (!CommandGate(bare))
                    return;
            }

            session.SendInput(line);
        }

        /// <summary>清空当前活动键的历史（F13：按连接清，不动其他连接）。</summary>
        public void ClearHistory()
        {
            lock (_histories)
            {
                EnsureActiveLoadedLocked();
                _histories[_activeKey].Clear();
            }
            PersistHistory();
        }

        private void AppendHistory(ChatMessage msg)
        {
            if (msg == null) return;
            lock (_histories)
            {
                EnsureActiveLoadedLocked();
                var list = _histories[_activeKey];
                list.Add(msg);
                var cap = MaxHistoryMessages > 0 ? MaxHistoryMessages : 40;
                // 成对裁剪，尽量保留完整 user/assistant 轮次
                while (list.Count > cap)
                    list.RemoveAt(0);
            }
            PersistHistory();
        }

        private void PersistHistory()
        {
            if (_historyStore == null) return;
            try
            {
                string key;
                List<ChatMessage> snapshot;
                lock (_histories)
                {
                    key = _activeKey;
                    List<ChatMessage> list;
                    if (!_histories.TryGetValue(key, out list)) list = new List<ChatMessage>();
                    snapshot = new List<ChatMessage>(list);
                }
                _historyStore.Save(key, snapshot);
            }
            catch
            {
                // 历史落盘失败不阻断对话（下次 Append/Clear 会再试）
            }
        }

        private string BuildSystemPrompt(ITerminalSession session)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是一个服务器运维助手，帮助用户管理和排查 Linux/Windows 服务器问题。");
            sb.AppendLine("请用中文回答，简洁明了。");

            if (session != null)
            {
                sb.AppendLine();
                sb.AppendLine("当前连接上下文：");

                if (!string.IsNullOrEmpty(session.Hostname))
                    sb.AppendLine($"- 主机名: {session.Hostname}");

                if (!string.IsNullOrEmpty(session.OsType))
                    sb.AppendLine($"- 操作系统: {session.OsType}");

                sb.AppendLine($"- 连接 ID: {session.ConnectionId}");

                // 注入最近终端输出
                var recentOutput = session.GetRecentOutput(20);
                if (recentOutput != null && recentOutput.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("最近终端输出（最后 20 行）：");
                    sb.AppendLine("```");
                    foreach (var line in recentOutput)
                    {
                        sb.AppendLine(line);
                    }
                    sb.AppendLine("```");
                }

                // 注入选中文本
                var selection = session.GetSelection();
                if (!string.IsNullOrEmpty(selection))
                {
                    sb.AppendLine();
                    sb.AppendLine("用户选中的文本：");
                    sb.AppendLine("```");
                    sb.AppendLine(selection);
                    sb.AppendLine("```");
                }
            }

            sb.AppendLine();
            sb.AppendLine("当需要给出命令时，请使用 ```bash 代码块包裹，每行一个命令。");

            return sb.ToString();
        }

        private async Task<AiResponse> CallOpenAiApiAsync(List<ChatMessage> messages, CancellationToken ct)
        {
            var endpoint = Configuration.ApiEndpoint.TrimEnd('/');
            if (!endpoint.EndsWith("/chat/completions"))
                endpoint += "/chat/completions";

            // 构建请求体
            var requestBody = SerializeRequest(messages);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            // 设置 Authorization header
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = content;

            if (!string.IsNullOrEmpty(Configuration.ApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {Configuration.ApiKey}");
            }

            var httpResponse = await _httpClient.SendAsync(request, ct);
            var responseJson = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode)
            {
                return new AiResponse
                {
                    IsSuccess = false,
                    ErrorMessage = L10n.Tf("API 返回错误 ({0}): {1}", (int)httpResponse.StatusCode, responseJson)
                };
            }

            // 解析响应
            return ParseResponse(responseJson);
        }

        private string SerializeRequest(List<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"model\":\"{EscapeJson(Configuration.Model)}\"");
            sb.Append($",\"max_tokens\":{Configuration.MaxTokens}");
            sb.Append($",\"temperature\":{Configuration.Temperature:F1}");
            sb.Append(",\"messages\":[");

            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append($"\"role\":\"{EscapeJson(messages[i].Role)}\"");
                sb.Append($",\"content\":\"{EscapeJson(messages[i].Content)}\"");
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private AiResponse ParseResponse(string json)
        {
            try
            {
                // 简单 JSON 解析
                var content = ExtractContent(json);
                var tokens = ExtractTokenUsage(json);

                return new AiResponse
                {
                    IsSuccess = true,
                    Content = content,
                    TokensUsed = tokens
                };
            }
            catch (Exception ex)
            {
                return new AiResponse
                {
                    IsSuccess = false,
                    ErrorMessage = L10n.T("解析响应失败: ") + ex.Message
                };
            }
        }

        private string ExtractContent(string json)
        {
            // 查找 "content":"..." 在 choices[0].message 中
            var choicesIdx = json.IndexOf("\"choices\"");
            if (choicesIdx < 0) return "";

            var contentIdx = json.IndexOf("\"content\":\"", choicesIdx);
            if (contentIdx < 0) return "";

            contentIdx += 11;
            var endIdx = FindStringEnd(json, contentIdx);
            return UnescapeJson(json.Substring(contentIdx, endIdx - contentIdx));
        }

        private int ExtractTokenUsage(string json)
        {
            var usageIdx = json.IndexOf("\"total_tokens\"");
            if (usageIdx < 0) return 0;

            var colonIdx = json.IndexOf(':', usageIdx);
            if (colonIdx < 0) return 0;

            var start = colonIdx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;

            var end = start;
            while (end < json.Length && char.IsDigit(json[end])) end++;

            if (end > start && int.TryParse(json.Substring(start, end - start), out var tokens))
                return tokens;

            return 0;
        }

        private int FindStringEnd(string json, int start)
        {
            var i = start;
            while (i < json.Length)
            {
                if (json[i] == '\\')
                {
                    i += 2; // 跳过转义字符
                    continue;
                }
                if (json[i] == '"')
                    return i;
                i++;
            }
            return i;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\")
                     .Replace("\"", "\\\"")
                     .Replace("\n", "\\n")
                     .Replace("\r", "\\r")
                     .Replace("\t", "\\t");
        }

        private static string UnescapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\\"", "\"")
                     .Replace("\\n", "\n")
                     .Replace("\\r", "\r")
                     .Replace("\\t", "\t")
                     .Replace("\\\\", "\\");
        }

        public void Dispose()
        {
            lock (_histories) { _histories.Clear(); }
            // HttpClient is static — do NOT dispose here
        }

        public async Task<AiResponse> SendMessageStreamingAsync(string message, ITerminalSession session, CancellationToken ct, Action<string> onToken)
        {
            if (string.IsNullOrEmpty(message))
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("消息不能为空") };
            if (Configuration == null || string.IsNullOrEmpty(Configuration.ApiEndpoint))
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("AI 服务未配置") };

            try
            {
                var systemPrompt = BuildSystemPrompt(session);
                // F13：发送前按本会话连接对齐活跃键（兜底——面板切标签时已切，此处防漏）
                SetActiveConversation(KeyOf(session));
                List<ChatMessage> historySnapshot;
                lock (_histories)
                {
                    EnsureActiveLoadedLocked();
                    historySnapshot = new List<ChatMessage>(_histories[_activeKey]);
                }
                var messages = new List<ChatMessage>();
                messages.Add(new ChatMessage("system", systemPrompt));
                messages.AddRange(historySnapshot);
                messages.Add(new ChatMessage("user", message));

                var endpoint = Configuration.ApiEndpoint.TrimEnd('/');
                if (!endpoint.EndsWith("/chat/completions"))
                    endpoint += "/chat/completions";

                var requestBody = SerializeStreamingRequest(messages);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = content;
                if (!string.IsNullOrEmpty(Configuration.ApiKey))
                    request.Headers.Add("Authorization", $"Bearer {Configuration.ApiKey}");

                var httpResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    var err = await httpResponse.Content.ReadAsStringAsync();
                    return new AiResponse { IsSuccess = false, ErrorMessage = L10n.Tf("API 错误 ({0}): {1}", (int)httpResponse.StatusCode, err) };
                }

                var fullContent = new StringBuilder();
                var totalTokens = 0;
                using (var stream = await httpResponse.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (!reader.EndOfStream)
                    {
                        ct.ThrowIfCancellationRequested();
                        var line = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (!line.StartsWith("data: ")) continue;
                        var data = line.Substring(6).Trim();
                        if (data == "[DONE]") break;

                        var token = ExtractStreamToken(data);
                        if (!string.IsNullOrEmpty(token))
                        {
                            fullContent.Append(token);
                            try { onToken?.Invoke(token); } catch { }
                        }
                    }
                }

                AppendHistory(new ChatMessage("user", message));
                var result = fullContent.ToString();
                AppendHistory(new ChatMessage("assistant", result));

                return new AiResponse
                {
                    IsSuccess = true,
                    Content = result,
                    SuggestedCommands = ExtractCommands(result),
                    TokensUsed = totalTokens
                };
            }
            catch (OperationCanceledException)
            {
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("请求已取消") };
            }
            catch (Exception ex)
            {
                return new AiResponse { IsSuccess = false, ErrorMessage = L10n.T("流式调用失败: ") + ex.Message };
            }
        }

        private string SerializeStreamingRequest(List<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"model\":\"{EscapeJson(Configuration.Model)}\"");
            sb.Append($",\"max_tokens\":{Configuration.MaxTokens}");
            sb.Append($",\"temperature\":{Configuration.Temperature:F1}");
            sb.Append(",\"stream\":true");
            sb.Append(",\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append($"\"role\":\"{EscapeJson(messages[i].Role)}\"");
                sb.Append($",\"content\":\"{EscapeJson(messages[i].Content)}\"");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string ExtractStreamToken(string json)
        {
            // SSE delta.content
            var deltaIdx = json.IndexOf("\"delta\"");
            if (deltaIdx < 0) return null;
            var contentIdx = json.IndexOf("\"content\":\"", deltaIdx);
            if (contentIdx < 0) return null;
            contentIdx += 11;
            var endIdx = FindStringEnd(json, contentIdx);
            return UnescapeJson(json.Substring(contentIdx, endIdx - contentIdx));
        }
    }
}
