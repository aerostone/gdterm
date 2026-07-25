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

namespace Gdterm.AI
{
    /// <summary>
    /// AI 对话服务实现——OpenAI-compatible API 调用、连接上下文注入、命令提取
    /// </summary>
    public class AiAssistantService : IAiAssistantService, IDisposable
    {
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        private static readonly Regex CommandBlockRegex = new Regex(
            @"```(?:bash|sh|shell|zsh|powershell|cmd)\s*\r?\n(.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public AiConfiguration Configuration { get; set; }

        public AiAssistantService(AiConfiguration configuration)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<AiResponse> SendMessageAsync(string message, ITerminalSession session, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(message))
                return new AiResponse { IsSuccess = false, ErrorMessage = "消息不能为空" };

            if (Configuration == null || string.IsNullOrEmpty(Configuration.ApiEndpoint))
                return new AiResponse { IsSuccess = false, ErrorMessage = "AI 服务未配置" };

            try
            {
                // 构建 system prompt（注入连接上下文）
                var systemPrompt = BuildSystemPrompt(session);

                // 构建 messages 数组
                var messages = new List<ChatMessage>();
                messages.Add(new ChatMessage("system", systemPrompt));
                messages.AddRange(_history);
                messages.Add(new ChatMessage("user", message));

                // 调用 API
                var response = await CallOpenAiApiAsync(messages, ct);

                if (response.IsSuccess)
                {
                    // 添加到历史
                    _history.Add(new ChatMessage("user", message));
                    _history.Add(new ChatMessage("assistant", response.Content));

                    // 提取命令
                    response.SuggestedCommands = ExtractCommands(response.Content);
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                return new AiResponse { IsSuccess = false, ErrorMessage = "请求已取消" };
            }
            catch (Exception ex)
            {
                return new AiResponse { IsSuccess = false, ErrorMessage = $"AI 服务调用失败: {ex.Message}" };
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

        public void ExecuteCommand(ITerminalSession session, string command)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrEmpty(command)) throw new ArgumentNullException(nameof(command));

            session.SendInput(command + "\n");
        }

        public void ClearHistory()
        {
            _history.Clear();
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
                    ErrorMessage = $"API 返回错误 ({(int)httpResponse.StatusCode}): {responseJson}"
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
                    ErrorMessage = $"解析响应失败: {ex.Message}"
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
            _history.Clear();
            // HttpClient is static — do NOT dispose here
        }

        public async Task<AiResponse> SendMessageStreamingAsync(string message, ITerminalSession session, CancellationToken ct, Action<string> onToken)
        {
            if (string.IsNullOrEmpty(message))
                return new AiResponse { IsSuccess = false, ErrorMessage = "消息不能为空" };
            if (Configuration == null || string.IsNullOrEmpty(Configuration.ApiEndpoint))
                return new AiResponse { IsSuccess = false, ErrorMessage = "AI 服务未配置" };

            try
            {
                var systemPrompt = BuildSystemPrompt(session);
                var messages = new List<ChatMessage>();
                messages.Add(new ChatMessage("system", systemPrompt));
                messages.AddRange(_history);
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
                    return new AiResponse { IsSuccess = false, ErrorMessage = $"API 错误 ({(int)httpResponse.StatusCode}): {err}" };
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

                _history.Add(new ChatMessage("user", message));
                var result = fullContent.ToString();
                _history.Add(new ChatMessage("assistant", result));

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
                return new AiResponse { IsSuccess = false, ErrorMessage = "请求已取消" };
            }
            catch (Exception ex)
            {
                return new AiResponse { IsSuccess = false, ErrorMessage = $"流式调用失败: {ex.Message}" };
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
