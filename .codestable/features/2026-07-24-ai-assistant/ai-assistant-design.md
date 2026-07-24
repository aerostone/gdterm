---
doc_type: feature-design
feature: 2026-07-24-ai-assistant
requirement: null
roadmap: gdterm
roadmap_item: ai-assistant
status: approved
summary: 实现 AI 对话客户端——OpenAI-compatible API 调用、连接上下文感知（hostname/OS/最近命令）、"建议执行"命令提取、用户确认后发送到终端
tags: [ai, openai, chat, terminal]
---

# ai-assistant — AI 对话客户端

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| AI 服务（AI Service） | OpenAI-compatible API 的 HTTP 客户端 | 不同于本地模型推理 |
| 连接上下文（Connection Context） | 当前终端会话的 hostname/OS/最近命令 | 用于 AI 理解环境 |
| 建议执行（Suggest Execute） | AI 回复中提取命令，用户确认后发送到终端 | 不自动执行 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.AI` 类库，实现 `IAiAssistantService` 接口，提供 OpenAI-compatible API 对话能力、连接上下文感知、命令建议提取。

**为谁**：UI 模块（AI 对话面板）、Terminal 模块（提供上下文）

**成功标准**：
- 可通过 OpenAI-compatible API 发送对话请求
- 自动注入连接上下文（hostname/OS/最近命令）到对话
- 从 AI 回复中提取可执行命令
- 用户确认后可将命令发送到活动终端

**明确不做**：
- 本地模型推理
- 自动执行命令（必须用户确认）
- 多轮对话历史持久化
- 流式响应（v1 不做）

### 关键决策

1. **API 协议**：OpenAI-compatible（支持 OpenAI、Ollama、vLLM 等）
2. **上下文注入**：每次请求自动附加连接信息到 system prompt
3. **命令提取**：正则匹配 AI 回复中的代码块（```bash/```sh/```）
4. **内存管理**：对话历史仅在内存中，关闭面板即清空

### 前置依赖

- `core-models`（done ✅）：ConnectionConfig
- `terminal-emulator`（done ✅）：ITerminalSession（提供上下文）

## 2. 名词与编排

### 2.1 名词层

```csharp
// Gdterm.AI.IAiAssistantService — 公开接口
public interface IAiAssistantService
{
    /// <summary>
    /// 发送消息并获取回复
    /// </summary>
    Task<AiResponse> SendMessageAsync(string message, ITerminalSession session, CancellationToken ct);

    /// <summary>
    /// 从 AI 回复中提取可执行命令
    /// </summary>
    IList<string> ExtractCommands(string response);

    /// <summary>
    /// 发送命令到终端（用户确认后调用）
    /// </summary>
    void ExecuteCommand(ITerminalSession session, string command);

    /// <summary>
    /// 清空对话历史
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// AI 服务配置
    /// </summary>
    AiConfiguration Configuration { get; set; }
}

// Gdterm.AI.Models.AiConfiguration
public class AiConfiguration
{
    public string ApiEndpoint { get; set; }   // OpenAI-compatible endpoint
    public string ApiKey { get; set; }        // API key
    public string Model { get; set; }         // 模型名称
    public int MaxTokens { get; set; }        // 最大 token 数
    public double Temperature { get; set; }   // 温度参数
}

// Gdterm.AI.Models.AiResponse
public class AiResponse
{
    public string Content { get; set; }              // AI 回复内容
    public IList<string> SuggestedCommands { get; set; }  // 提取的可执行命令
    public int TokensUsed { get; set; }              // 消耗的 token 数
    public bool IsSuccess { get; set; }              // 是否成功
    public string ErrorMessage { get; set; }         // 错误信息
}

// Gdterm.AI.Models.ChatMessage
public class ChatMessage
{
    public string Role { get; set; }     // system/user/assistant
    public string Content { get; set; }
}
```

### 2.2 编排层

```
用户输入消息 → UI 调用 IAiAssistantService.SendMessageAsync(message, session)
  → 构建 system prompt（注入连接上下文：hostname/OS/最近命令）
  → 构建 messages 数组（历史 + 新消息）
  → HTTP POST 到 OpenAI-compatible endpoint
  → 解析响应 → 提取命令 → 返回 AiResponse

用户点击"执行" → UI 调用 IAiAssistantService.ExecuteCommand(session, command)
  → session.SendInput(command + "\n")
```

**流程级约束**：
- 每次请求自动注入连接上下文到 system prompt
- 命令提取仅匹配 ```bash/```sh/``` 代码块
- ExecuteCommand 必须由用户主动调用，不自动执行
- 对话历史仅在内存中，不持久化

### 2.3 挂载点清单

本 feature 不引入新挂入点。IAiAssistantService 由 UI 模块通过 DI 消费。

### 2.4 推进策略

```
1. 创建 Gdterm.AI 项目，引用 Core + Terminal
   退出信号：项目编译通过
2. 实现数据类（AiConfiguration、AiResponse、ChatMessage）
   退出信号：编译通过
3. 实现 IAiAssistantService 接口
   退出信号：编译通过
4. 实现 AiAssistantService 核心逻辑（API 调用、上下文注入、命令提取）
   退出信号：单元测试覆盖 SendMessageAsync + ExtractCommands + ExecuteCommand
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.AI/` 为新建目录

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | SendMessageAsync(message, session) | 返回 AiResponse |
| 2 | AI 回复含 ```bash ... ``` | ExtractCommands 提取命令 |
| 3 | ExecuteCommand(session, cmd) | 命令发送到终端 |
| 4 | ClearHistory | 对话历史清空 |
| 5 | API 调用失败 | IsSuccess=false, ErrorMessage 有值 |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不做本地模型推理 | 代码中无模型加载 |
| 2 | 不做自动执行命令 | ExecuteCommand 必须用户调用 |
| 3 | 不做对话历史持久化 | 代码中无文件/数据库写入 |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.AI 作为 AI 对话子系统
- **接口**：IAiAssistantService → UI 模块消费契约
