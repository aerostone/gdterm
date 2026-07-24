---
doc_type: roadmap
slug: gdterm
status: active
created: 2026-07-24
last_reviewed: 2026-07-24
tags: [remote-desktop, ssh, terminal, keepass, ai, portable, win7]
related_requirements: []
related_architecture: []
---

# gdterm — 统一远程运维客户端

## 1. 背景

运维/开发人员日常需要通过 RDP、SSH 连接大量远程机器，工具散落各处（MSTSC、PuTTY、KeePass、各种隧道工具），在公共场所切换用户时密码暴露风险大。部分内网机器需要通过 jump server 串联访问。

gdterm 是一个绿色便携、低内存（30-80MB）、支持 Windows 7/Server 2008 的统一远程运维客户端，集成 RDP 远程桌面、全彩终端、KeePass 密码管理、SSH 隧道代理、AI 对话、日志审计和闲时锁定。

技术栈：.NET Framework 4.6.2 + WinForms，单文件夹免安装，U盘便携。

## 2. 范围与明确不做

### 本 roadmap 覆盖

- RDP 远程桌面（ActiveX 嵌入，支持 jump server 跳板链）
- 全彩 SSH 终端（成熟 .NET 终端模拟库）
- SSH 隧道代理（端口转发 + SOCKS 代理，纯托管 SSH.NET）
- SFTP 文件浏览器（浏览、上传、下载，基于 SSH.NET SFTP 客户端）
- KeePass 密码库（.kdbx 读写，自动填充+连接关联，强密码策略）
- AI 对话（OpenAI-compatible API，连接感知+建议执行）
- 日志轮转（连接/操作/密码使用/AI 交互审计）
- 闲时锁定（密码库+会话自动锁定）
- WinForms 主界面（左侧树形连接列表 + 右侧标签页 + 状态栏）

### 明确不做

- **跨平台**：仅 Windows，不考虑 macOS/Linux（WinForms + AxMsTscLib 锁定）
- **多显示器/多屏 RDP**：v1 不支持 span/multimon 模式
- **SFTP 高级功能**：不实现断点续传、队列批量传输、远程编辑（v1 只做基础浏览+上传下载）
- **自动发现/智能路由**：jump server 拓扑由用户手动配置，不做自动探测
- **AI 自动执行**：AI 仅建议，用户手动确认执行
- **KeePass OTP/TOTP**：v1 不做一次性密码生成
- **凭据轮换/自动改密**：密码库只管存储和填充，不自动改密码
- **会话录制/回放**：日志记录审计信息，不录制屏幕或终端会话视频
- **远程剪贴板同步**：RDP 自带剪贴板共享，不做额外剪贴板管理

## 3. 模块拆分（概设）

```
gdterm
├── Core           — 核心模型（ConnectionConfig、CredentialRef、TunnelEndpoint）
├── Connections    — 连接配置存储/加载、树形分组管理
├── Tunnel         — SSH.NET 隧道管理器（端口转发、SOCKS 代理、跳板链编排）
├── Terminal       — SSH 终端会话（封装终端模拟库 + SSH.NET 直连）
├── RDP            — RDP 客户端（AxMsTscLib 嵌入，通过 Tunnel 走跳板链）
├── SFTP           — SFTP 文件浏览器（SSH.NET SFTP 客户端，浏览/上传/下载）
├── KeePass        — 密码库管理（.kdbx 读写、自动填充、连接关联、密码强度校验）
├── AI             — AI 对话客户端（OpenAI-compatible API、连接上下文、建议执行）
├── Logging        — 日志引擎（结构化日志、轮转策略、审计追踪）
├── Security       — 闲时锁定（密码库自动锁定、会话锁定、主密码管理）
└── UI             — WinForms 主界面（树形面板、标签页容器、状态栏、对话框）
```

### Core · 核心模型
- **职责**：定义所有模块共享的数据结构（ConnectionConfig、CredentialRef、TunnelEndpoint、ProtocolType）。纯数据模型，零业务逻辑。
- **承载的子 feature**：`core-models`
- **触碰的现有代码**：全新

### Connections · 连接管理
- **职责**：连接配置的 CRUD 和持久化（JSON 文件），树形分组（文件夹）管理，连接导入/导出。不负责实际连接建立。
- **承载的子 feature**：`connections-storage`
- **触碰的现有代码**：全新，依赖 Core 模型

### Tunnel · SSH 隧道
- **职责**：基于 SSH.NET 的隧道管理——SSH 连接建立、本地/远程端口转发、动态 SOCKS 代理、跳板链编排（顺序连接多跳）。提供 `TunnelManager` 给 RDP/Terminal 调用。不包含终端交互。
- **承载的子 feature**：`ssh-tunnel`
- **触碰的现有代码**：全新

### Terminal · SSH 终端
- **职责**：封装 .NET 终端模拟库，提供全彩 ANSI 终端控件。直连模式下内置 SSH 连接（SSH.NET），跳板模式下通过 Tunnel 端口转发连接。处理终端输入输出、ANSI 转义渲染。
- **承载的子 feature**：`terminal-emulator`
- **触碰的现有代码**：全新

### RDP · 远程桌面
- **职责**：嵌入 AxMsTscLib ActiveX 控件实现 RDP 客户端。直连模式直接连目标，跳板模式通过 Tunnel 建立端口转发后连 localhost:forwarded_port。处理 RDP 事件（连接/断开/错误）。
- **承载的子 feature**：`rdp-client`
- **触碰的现有代码**：全新

### SFTP · 文件浏览器
- **职责**：基于 SSH.NET 的 SFTP 客户端，提供远程文件浏览（树形/列表视图）、上传、下载、删除、重命名、创建目录。不支持断点续传、队列批量传输、远程编辑。
- **承载的子 feature**：`sftp-browser`
- **触碰的现有代码**：全新

### KeePass · 密码管理
- **职责**：基于 KeePassLib 的 .kdbx 文件读写。管理密码条目（CRUD），连接与密码条目的关联映射，自动填充凭据到连接，密码强度校验（创建/修改时强制），主密码会话级解锁/锁定。
- **承载的子 feature**：`keepass-integration`
- **触碰的现有代码**：全新

### AI · AI 对话
- **职责**：OpenAI-compatible API 客户端，支持多 provider（OpenAI、Ollama、vLLM 等）。维护连接上下文（hostname、OS、最近 N 条命令），提供"建议执行"功能将 AI 回复中的命令发送到活动终端。不自动执行。
- **承载的子 feature**：`ai-assistant`
- **触碰的现有代码**：全新

### Logging · 日志引擎
- **职责**：结构化日志记录（连接事件、命令执行、密码使用、AI 交互），日志轮转（按大小+按天，可配置保留策略），审计查询接口。
- **承载的子 feature**：`logging-engine`
- **触碰的现有代码**：全新

### Security · 安全
- **职责**：闲时无操作检测（可配置超时），触发锁定密码库和/或活动会话，主密码管理（设置/修改/验证），密码强度策略引擎（最小长度、字符类别、常见密码检查）。
- **承载的子 feature**：`security-idle-lock`
- **触碰的现有代码**：全新

### UI · 主界面
- **职责**：WinForms 主窗口——左侧 TreeView 连接面板（右键菜单管理），右侧 TabControl 标签页容器（RDP/终端/AI 面板），底部 StatusBar（连接状态、隧道状态、密码库状态、AI 状态），全局菜单和工具栏。
- **承载的子 feature**：`ui-shell`
- **触碰的现有代码**：全新

## 4. 模块间接口契约 / 共享协议（架构层详设）

### 4.1 ConnectionConfig（Core → 全模块共享）

**方向**：Core 定义，所有模块读取
**形式**：.NET 类定义

```csharp
// Gdterm.Core.Models.ConnectionConfig
public class ConnectionConfig
{
    public string Id { get; set; }              // GUID
    public string Name { get; set; }            // 显示名称
    public ProtocolType Protocol { get; set; }  // RDP | SSH
    public string Host { get; set; }            // 目标主机
    public int Port { get; set; }               // 目标端口（RDP默认3389，SSH默认22）
    public string Username { get; set; }
    public string Password { get; set; }        // 可选，优先从 KeePass 填充
    public string Domain { get; set; }          // RDP 域名，可选
    public string GroupPath { get; set; }       // 树形分组路径，如 "Jump/Web"
    public JumpChainConfig JumpChain { get; set; }  // null = 直连
    public TunnelConfig Tunnel { get; set; }    // null = 不使用隧道
    public string CredentialRefId { get; set; } // 关联的 KeePass 条目 ID，可选
    public Dictionary<string, string> Metadata { get; set; }  // 扩展字段
}

public enum ProtocolType { RDP, SSH }

public class JumpChainConfig
{
    public List<JumpHop> Hops { get; set; }  // 按顺序的跳板节点列表
}

public class JumpHop
{
    public string Host { get; set; }
    public int Port { get; set; }           // 默认 22
    public string Username { get; set; }
    public string CredentialRefId { get; set; }  // 可选，关联 KeePass
}

public class TunnelConfig
{
    public TunnelType Type { get; set; }        // LocalForward | DynamicSocks
    public int LocalPort { get; set; }          // 本地监听端口（0=自动分配）
    public string RemoteHost { get; set; }      // 远程目标（LocalForward 时）
    public int RemotePort { get; set; }         // 远程端口（LocalForward 时）
}

public enum TunnelType { LocalForward, DynamicSocks }
```

**约束**：
- `Id` 在存储层全局唯一
- `JumpChain.Hops` 顺序即连接顺序，不可为空列表（空表示直连，用 `null`）
- `CredentialRefId` 格式为 KeePass entry UUID 字符串，空或 null 表示不关联

### 4.2 TunnelManager API（Tunnel → Terminal / RDP）

**方向**：Terminal / RDP 调用 Tunnel
**形式**：.NET 接口调用

```csharp
// Gdterm.Tunnel.ITunnelManager
public interface ITunnelManager
{
    /// <summary>
    /// 建立隧道连接。直连模式不调用此接口。
    /// </summary>
    /// <param name="connection">包含 JumpChain 和 TunnelConfig 的连接配置</param>
    /// <param name="credentials">跳板链各节点的凭据（按 Hop 顺序）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>隧道端点，调用方连接 localhost:Endpoint.LocalPort</returns>
    Task<TunnelEndpoint> EstablishAsync(
        ConnectionConfig connection,
        IList<CredentialPayload> credentials,
        CancellationToken ct);

    /// <summary>
    /// 关闭指定连接的所有隧道资源
    /// </summary>
    Task CloseAsync(string connectionId);

    /// <summary>
    /// 查询隧道状态
    /// </summary>
    TunnelStatus GetStatus(string connectionId);
}

public class TunnelEndpoint
{
    public string LocalHost { get; set; }   // 通常 "127.0.0.1"
    public int LocalPort { get; set; }      // 转发后的本地端口
    public string ConnectionId { get; set; }
}

public class TunnelStatus
{
    public bool IsActive { get; set; }
    public string LastError { get; set; }
    public TimeSpan Uptime { get; set; }
}

public class CredentialPayload
{
    public string Username { get; set; }
    public string Password { get; set; }
    // 未来可扩展：PrivateKeyPath, Passphrase
}
```

**约束**：
- `EstablishAsync` 内部按 `JumpChain.Hops` 顺序逐跳建立 SSH 连接，最后一跳建立端口转发
- `LocalPort = 0` 时自动选择可用端口
- 连接失败时抛出 `TunnelException`（含 `HopIndex` 指示失败在哪一跳）
- 每个 `connectionId` 只允许一个活跃隧道，重复调用先关闭旧的

### 4.3 KeePass AutoFill API（KeePass → Terminal / RDP）

**方向**：Terminal / RDP 调用 KeePass 获取凭据
**形式**：.NET 接口调用

```csharp
// Gdterm.KeePass.IKeePassService
public interface IKeePassService
{
    /// <summary>
    /// 解锁密码库（提示用户输入主密码）
    /// </summary>
    Task<bool> UnlockAsync(string masterPassword);

    /// <summary>
    /// 锁定密码库，清除内存中的明文
    /// </summary>
    void Lock();

    /// <summary>
    /// 是否已解锁
    /// </summary>
    bool IsUnlocked { get; }

    /// <summary>
    /// 根据 ConnectionConfig.CredentialRefId 获取凭据
    /// </summary>
    CredentialPayload GetCredential(string credentialRefId);

    /// <summary>
    /// 创建密码条目（自动校验密码强度）
    /// </summary>
    /// <exception cref="WeakPasswordException">密码不满足强度要求时抛出</exception>
    KeePassEntry CreateEntry(KeePassEntry entry);

    /// <summary>
    /// 更新密码条目（自动校验密码强度）
    /// </summary>
    /// <exception cref="WeakPasswordException">密码不满足强度要求时抛出</exception>
    void UpdateEntry(KeePassEntry entry);

    /// <summary>
    /// 列出所有条目（不含密码明文，用于 UI 展示和关联选择）
    /// </summary>
    IList<KeePassEntrySummary> ListEntries();
}

public class KeePassEntry
{
    public string Id { get; set; }       // KeePass entry UUID
    public string Title { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string Url { get; set; }
    public string Notes { get; set; }
    public string GroupPath { get; set; }
}

public class KeePassEntrySummary
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Username { get; set; }
    public string GroupPath { get; set; }
}

public class WeakPasswordException : Exception
{
    public IList<string> Violations { get; set; }  // 具体违反的规则列表
}
```

**约束**：
- 密码库未解锁时调用 `GetCredential` 抛出 `InvalidOperationException`
- `CreateEntry` / `UpdateEntry` 内部强制校验密码强度（最小 12 字符，含大写+小写+数字+特殊字符，不含常见弱密码）
- `ListEntries` 不返回密码明文，仅用于 UI 展示和关联选择

### 4.4 TerminalContext（Terminal → AI）

**方向**：Terminal 提供上下文给 AI
**形式**：.NET 接口调用 + 事件

```csharp
// Gdterm.Terminal.ITerminalSession
public interface ITerminalSession
{
    string ConnectionId { get; }
    string Hostname { get; }
    string OsType { get; }              // 从 SSH 检测或用户标记

    /// <summary>
    /// 获取最近 N 行终端输出（用于 AI 上下文）
    /// </summary>
    IList<string> GetRecentOutput(int lineCount);

    /// <summary>
    /// 获取当前选中文本
    /// </summary>
    string GetSelection();

    /// <summary>
    /// 向终端发送命令（AI 建议执行时调用）
    /// </summary>
    void SendInput(string text);

    /// <summary>
    /// 终端输出事件（AI 实时订阅）
    /// </summary>
    event EventHandler<TerminalOutputEventArgs> OutputReceived;
}

public class TerminalOutputEventArgs : EventArgs
{
    public string Text { get; set; }
    public DateTime Timestamp { get; set; }
}
```

### 4.5 SFTP Service API（SFTP → UI）

**方向**：UI 调用 SFTP 服务
**形式**：.NET 接口调用

```csharp
// Gdterm.Sftp.ISftpService
public interface ISftpService
{
    Task ConnectAsync(ConnectionConfig connection, CredentialPayload credential, CancellationToken ct);
    Task<IList<SftpFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct);
    Task UploadAsync(string localPath, string remotePath, IProgress<FileTransferProgress> progress, CancellationToken ct);
    Task DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress> progress, CancellationToken ct);
    Task DeleteAsync(string remotePath, bool recursive, CancellationToken ct);
    Task CreateDirectoryAsync(string remotePath, CancellationToken ct);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct);
    void Disconnect();
    bool IsConnected { get; }
}

public class SftpFileInfo
{
    public string Name { get; set; }
    public string FullPath { get; set; }
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public string Permissions { get; set; }  // rwxrwxrwx 格式
    public string Owner { get; set; }
    public string Group { get; set; }
}

public class FileTransferProgress
{
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;
    public TimeSpan Elapsed { get; set; }
}
```

**约束**：
- 跳板模式下 SFTP 通过 `ITunnelManager.EstablishAsync` 建立端口转发后连接 `localhost:forwarded_port`
- `ListDirectoryAsync` 返回按类型（目录优先）+ 名称排序
- 上传/下载通过 `IProgress<FileTransferProgress>` 报告进度，UI 可绑定进度条
- 不支持断点续传——中断后重新开始整个传输

### 4.6 AI Service API（AI → UI）

**方向**：UI 调用 AI 服务
**形式**：.NET 接口调用

```csharp
// Gdterm.AI.IAiService
public interface IAiService
{
    /// <summary>
    /// 发送对话消息，流式返回响应
    /// </summary>
    IAsyncEnumerable<string> ChatAsync(
        string message,
        TerminalContext context,
        CancellationToken ct);

    /// <summary>
    /// 从 AI 响应中提取可执行命令
    /// </summary>
    IList<string> ExtractCommands(string aiResponse);

    /// <summary>
    /// 配置 API 端点和密钥
    /// </summary>
    void Configure(AiProviderConfig config);
}

public class TerminalContext
{
    public string ConnectionId { get; set; }
    public string Hostname { get; set; }
    public string OsType { get; set; }
    public IList<string> RecentOutput { get; set; }  // 最近 N 行
    public string SelectedText { get; set; }
}

public class AiProviderConfig
{
    public string Endpoint { get; set; }     // API base URL
    public string ApiKey { get; set; }       // 可选（Ollama 不需要）
    public string Model { get; set; }        // 模型名称
    public int MaxContextLines { get; set; } // 发送给 AI 的最大终端行数，默认 50
}
```

**约束**：
- `ChatAsync` 使用 `IAsyncEnumerable` 实现流式输出，UI 逐 token 显示
- `ExtractCommands` 解析 AI 响应中的代码块（```bash ... ``` 或 `command` 格式）
- API Key 加密存储在本地配置文件中

### 4.7 Logging API（Logging → 全模块）

**方向**：所有模块调用 Logging 记录事件
**形式**：.NET 静态/注入调用

```csharp
// Gdterm.Logging.IAuditLogger
public interface IAuditLogger
{
    void LogConnection(string connectionId, string host, string protocol, ConnectionAction action);
    void LogCredentialUse(string connectionId, string credentialRefId, CredentialAction action);
    void LogCommand(string connectionId, string command);
    void LogAiInteraction(string connectionId, string prompt, string response);
    void LogSecurityEvent(SecurityEvent evt, string detail);
    IList<AuditEntry> Query(AuditQuery query, int limit = 100);
}

public enum ConnectionAction { Open, Close, Error, Timeout }
public enum CredentialAction { AutoFill, ManualCopy, Unlock, Lock }
public enum SecurityEvent { IdleLock, Unlock, WeakPasswordRejected, BruteForceDetected }

public class AuditEntry
{
    public DateTime Timestamp { get; set; }
    public string ConnectionId { get; set; }
    public string EventType { get; set; }
    public string Detail { get; set; }
}

public class AuditQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string ConnectionId { get; set; }
    public string EventType { get; set; }
}

public class LogRotationConfig
{
    public long MaxFileSizeBytes { get; set; }  // 默认 10MB
    public int MaxFileCount { get; set; }       // 默认 10 个文件
    public int RetentionDays { get; set; }      // 默认 30 天
}
```

### 4.8 Security API（Security → UI / KeePass）

**方向**：UI 调用 Security 管理锁定，Security 触发 KeePass 锁定
**形式**：.NET 接口 + 事件

```csharp
// Gdterm.Security.ISecurityManager
public interface ISecurityManager
{
    /// <summary>
    /// 重置空闲计时器（UI 每次用户操作时调用）
    /// </summary>
    void ResetIdleTimer();

    /// <summary>
    /// 手动锁定
    /// </summary>
    void LockNow();

    /// <summary>
    /// 解锁（验证主密码）
    /// </summary>
    bool Unlock(string masterPassword);

    /// <summary>
    /// 设置/修改主密码
    /// </summary>
    /// <exception cref="WeakPasswordException">密码不满足强度要求</exception>
    void SetMasterPassword(string oldPassword, string newPassword);

    /// <summary>
    /// 锁定状态变化事件（UI 订阅以更新界面）
    /// </summary>
    event EventHandler<LockStateChangedEventArgs> LockStateChanged;

    bool IsLocked { get; }
    TimeSpan IdleTimeout { get; set; }
}
```

**约束**：
- `ResetIdleTimer` 在 UI 的 `MouseMove`、`KeyDown`、`Click` 等事件中调用
- 超时触发时 Security 内部调用 `IKeePassService.Lock()` 并触发 `LockStateChanged` 事件
- 主密码强度校验复用 KeePass 的 `WeakPasswordException` 逻辑

### 4.9 共享配置存储格式

**形式**：JSON 文件，随绿色版走

```
{app_folder}/
├── gdterm.exe
├── config.json                  # 全局配置（AI provider、日志轮转、安全超时等）
├── connections.json             # 连接配置列表
├── gdterm.kdbx                  # KeePass 密码库（默认位置，可自定义路径）
├── logs/                        # 日志目录
│   ├── gdterm-2026-07-24.log
│   └── ...
└── lib/                         # 依赖 DLL
    ├── KeePassLib.dll
    ├── Renci.SshNet.dll
    └── TerminalLib.dll
```

```json
// config.json
{
  "ai": {
    "endpoint": "http://localhost:11434/v1",
    "apiKey": null,
    "model": "qwen2.5:7b",
    "maxContextLines": 50
  },
  "logging": {
    "maxFileSizeBytes": 10485760,
    "maxFileCount": 10,
    "retentionDays": 30
  },
  "security": {
    "idleTimeoutMinutes": 15,
    "masterPasswordHash": "bcrypt:...",
    "masterPasswordSalt": "..."
  },
  "ui": {
    "theme": "light",
    "language": "zh-CN"
  }
}
```

```json
// connections.json
{
  "version": 1,
  "connections": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "name": "Web Server",
      "protocol": "SSH",
      "host": "10.0.1.50",
      "port": 22,
      "username": "admin",
      "password": null,
      "domain": null,
      "groupPath": "Jump/Web",
      "jumpChain": {
        "hops": [
          { "host": "jump.example.com", "port": 22, "username": "ops", "credentialRefId": "..." }
        ]
      },
      "tunnel": {
        "type": "LocalForward",
        "localPort": 0,
        "remoteHost": "10.0.1.50",
        "remotePort": 22
      },
      "credentialRefId": "kps-entry-uuid-here",
      "metadata": {}
    }
  ]
}
```

**约束**：
- `connections.json` 中 `password` 字段为 null（密码只存在 KeePass 中）
- `connections.json` 版本号用于未来迁移
- `config.json` 中 `masterPasswordHash` 使用 bcrypt 存储，不存明文

## 5. 子 feature 清单

1. **core-models** — 核心数据模型定义（ConnectionConfig、CredentialRef、TunnelEndpoint、ProtocolType、枚举）
   - 所属模块：Core
   - 依赖：无
   - 状态：planned
   - 对应 feature：未启动
   - 备注：纯数据模型，零依赖，所有模块的公共基础

2. **connections-storage** — 连接配置 JSON 存储、树形分组管理、连接 CRUD
   - 所属模块：Connections
   - 依赖：[core-models]
   - 状态：planned
   - 对应 feature：未启动
   - 备注：最小闭环候选——做完后能增删改查连接配置

3. **ssh-tunnel** — SSH.NET 隧道管理器（端口转发、SOCKS 代理、跳板链编排）
   - 所属模块：Tunnel
   - 依赖：[core-models]
   - 状态：planned
   - 对应 feature：未启动
   - 备注：纯托管代码，不依赖 UI

4. **terminal-emulator** — SSH 终端会话（封装终端模拟库 + SSH 直连 + ANSI 渲染）
   - 所属模块：Terminal
   - 依赖：[core-models]
   - 状态：planned
   - 对应 feature：未启动
   - 备注：包含 SSH 直连能力（不依赖 Tunnel），跳板模式才需要 Tunnel

5. **sftp-browser** — SFTP 文件浏览器（远程目录浏览、文件上传/下载、删除/重命名/创建目录，跳板模式支持）
   - 所属模块：SFTP
   - 依赖：[core-models, ssh-tunnel]
   - 状态：planned
   - 对应 feature：未启动
   - 备注：基于 SSH.NET SftpClient；跳板模式通过 Tunnel 端口转发

6. **keepass-integration** — KeePass 密码库管理（.kdbx 读写、自动填充、连接关联、密码强度校验）
   - 所属模块：KeePass
   - 依赖：[core-models]
   - 状态：planned
   - 对应 feature：未启动
   - 备注：独立模块，仅共享 Core 模型

7. **logging-engine** — 日志引擎（结构化审计日志、轮转策略、查询接口）
   - 所属模块：Logging
   - 依赖：[core-models]
   - 状态：planned
   - 对应 feature：未启动

8. **ai-assistant** — AI 对话客户端（OpenAI API、连接上下文、建议执行）
   - 所属模块：AI
   - 依赖：[terminal-emulator]
   - 状态：planned
   - 对应 feature：未启动
   - 备注：依赖 Terminal 提供上下文接口

9. **security-idle-lock** — 闲时锁定（空闲检测、密码库锁定、主密码管理、密码强度策略）
   - 所属模块：Security
   - 依赖：[core-models, keepass-integration]
   - 状态：planned
   - 对应 feature：未启动
   - 备注：依赖 KeePass 的 Lock 接口

10. **rdp-client** — RDP 客户端（AxMsTscLib 嵌入，支持直连和跳板链）
    - 所属模块：RDP
    - 依赖：[core-models, connections-storage, ssh-tunnel]
    - 状态：planned
    - 对应 feature：未启动
    - 备注：跳板模式需要 Tunnel 建立端口转发

11. **ui-shell** — WinForms 主界面（树形连接面板、标签页容器、状态栏、全局菜单、SFTP 面板）
    - 所属模块：UI
    - 依赖：[core-models, connections-storage, ssh-tunnel, terminal-emulator, sftp-browser, keepass-integration, logging-engine, ai-assistant, security-idle-lock, rdp-client]
    - 状态：planned
    - 对应 feature：未启动
    - 备注：最后实现，集成所有模块

**最小闭环**：第 4 条 `terminal-emulator` 做完后（配合 `core-models` + `connections-storage`），用户可以添加 SSH 连接配置、点击打开一个全彩 SSH 终端会话。这是最窄的端到端路径。

## 6. 排期思路

**按依赖分层推进**：

```
Layer 0（基础）：core-models                        ← 第一批
Layer 1（独立模块）：connections-storage, ssh-tunnel, terminal-emulator, sftp-browser, keepass-integration, logging-engine  ← 可并行
Layer 2（有依赖的模块）：ai-assistant (→terminal), security-idle-lock (→keepass), rdp-client (→tunnel)  ← 可并行
Layer 3（集成）：ui-shell (→all)                     ← 最后
```

**第一条选 `core-models` 的理由**：它是所有模块的公共基础，定义好数据模型后后续模块可并行开发。

**最小闭环选 `terminal-emulator` 的理由**：SSH 终端是运维工具最核心的体验。做完 `core-models` + `connections-storage` + `terminal-emulator` 后，用户就能添加服务器、点击连接、看到全彩终端——这是最小可演示的功能闭环。

**潜在卡点**：
- 终端模拟库选型需要 spike 验证（ANSI 支持完整度、内存占用、维护活跃度）
- AxMsTscLib 在 Win7/2008 上的兼容性需要实测
- KeePassLib 版本选择需要确认对 .NET 4.6.2 的支持

## 7. 观察项

- 终端模拟库候选需要 spike 验证：建议在 `terminal-emulator` feature 开始前做一次 spike，评估 2-3 个候选库
- .NET Framework 4.6.2 的长期支持状况：微软已结束主要支持，但 Win7 用户无法升级更高版本。需评估是否同时支持 .NET 4.8
- SSH.NET 的 SOCKS 代理实现成熟度需验证：可能需要在 `ssh-tunnel` feature 中做 spike
- `connections.json` 存明文连接配置（不含密码）的安全性：攻击者拿到文件可以看到所有服务器地址。可考虑加密，但会增加复杂度
