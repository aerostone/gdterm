---
doc_type: feature-design
feature: 2026-07-24-terminal-emulator
requirement: null
roadmap: gdterm
roadmap_item: terminal-emulator
status: approved
summary: 实现 SSH 终端会话——封装 .NET 终端模拟库实现全彩 ANSI 渲染，内置 SSH 直连（SSH.NET），支持跳板模式
tags: [terminal, ssh, ansi, winforms]
---

# terminal-emulator — SSH 终端会话

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| 终端会话（TerminalSession） | 一个 SSH 连接 + 一个 shell stream 的生命周期 | 不同于 TunnelSession（只管隧道） |
| 终端模拟库 | 第三方 .NET 库，负责 ANSI 解码和字符渲染 | 本模块封装它，不直接暴露 |
| ShellStream | SSH.NET 提供的交互式 shell 通道 | 不同于 SshClient.ExecuteCommand（非交互） |
| 终端控件（TerminalControl） | WinForms UserControl，承载终端模拟库的渲染 | 对外暴露的 UI 组件 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.Terminal` 类库，实现：
- `ITerminalSession` 接口（roadmap 4.4 完全一致）
- SSH 直连模式（SSH.NET SshClient + ShellStream）
- 跳板模式（通过 Tunnel 端口转发后连接 localhost:forwarded_port）
- WinForms TerminalControl（封装终端模拟库，支持 ANSI 渲染）

**为谁**：UI 模块（嵌入标签页）、AI 模块（获取终端上下文）

**成功标准**：
- 可以通过 SSH 直连打开一个全彩终端会话
- 可以通过跳板模式打开终端会话
- 终端输出事件可被 AI 模块订阅

**明确不做**：
- SSH 密钥认证（v1 仅密码认证）
- 终端录制/回放
- 多标签页管理（归 UI 模块）
- 终端模拟库自研（v1 引入第三方库）

### 关键决策

1. **终端模拟库选型**：v1 引入第三方 .NET 终端模拟库（候选：TerminalControl、vt100.net、AvalonTerm），通过 `IRenderer` 接口封装，后期可替换。
2. **SSH 连接策略**：使用 SSH.NET SshClient.CreateShellStream() 创建交互式 shell，数据通过事件回调推送到终端模拟库。
3. **跳板模式**：通过 ITunnelManager 建立端口转发后，用 localhost:forwarded_port 连接目标机器。
4. **会话生命周期**：TerminalSession 持有 SshClient + ShellStream，Dispose 时清理两者。

### 前置依赖

- `core-models`（done ✅）：ConnectionConfig、CredentialPayload
- `ssh-tunnel`（done ✅）：ITunnelManager（跳板模式）

## 2. 名词与编排

### 2.1 名词层

**现状**：Core 模型已定义 ConnectionConfig、CredentialPayload

**变化**：新增以下类型

```csharp
// Gdterm.Terminal.ITerminalSession — 公开接口（roadmap 4.4 完全一致）
public interface ITerminalSession : IDisposable
{
    string ConnectionId { get; }
    string Hostname { get; }
    string OsType { get; }
    bool IsConnected { get; }
    IList<string> GetRecentOutput(int lineCount);
    string GetSelection();
    void SendInput(string text);
    event EventHandler<TerminalOutputEventArgs> OutputReceived;
}

// Gdterm.Terminal.Models.TerminalOutputEventArgs
public class TerminalOutputEventArgs : EventArgs
{
    public string Text { get; set; }
    public DateTime Timestamp { get; set; }
}

// Gdterm.Terminal.Rendering.IRenderer — 终端渲染抽象（内部使用，可替换底层库）
internal interface IRenderer
{
    void Write(string text);
    void Clear();
    Control GetControl();  // 返回 WinForms 控件
}

// Gdterm.Terminal.TerminalControl — WinForms UserControl
public class TerminalControl : UserControl
{
    public ITerminalSession Session { get; }
    public void AttachSession(ITerminalSession session);
    public void DetachSession();
}
```

### 2.2 编排层

```
用户点击连接 → UI 创建 TerminalControl
  → TerminalControl.AttachSession(session)
    → TerminalSession.ConnectAsync(config, credential)
      → 直连：new SshClient(host, port, user, pass).Connect()
      → 跳板：ITunnelManager.EstablishAsync → new SshClient(localhost, forwardedPort)
      → SshClient.CreateShellStream("xterm", cols, rows)
      → ShellStream.DataReceived += on data → renderer.Write(data) + OutputReceived(this, args)
    → session 返回到 TerminalControl
  → TerminalControl 渲染终端内容

用户输入 → TerminalControl.KeyDown → session.SendInput(key)
  → ShellStream.Write(key)

会话关闭 → session.Dispose() → ShellStream.Close + SshClient.Disconnect
```

**流程级约束**：
- 连接失败时 TerminalSession 不应崩溃，应设置 IsConnected=false 并触发 OutputReceived 通知错误
- ShellStream.DataReceived 是后台线程回调，UI 更新需 Invoke
- 近期输出缓冲区保留最近 500 行（可配置）
- Dispose 顺序：先 ShellStream，再 SshClient

### 2.3 挂载点清单

本 feature 不引入新挂入点。ITerminalSession 由 UI 模块通过 DI 消费，TerminalControl 由 UI 模块嵌入标签页。

### 2.4 推进策略

```
1. 创建 Gdterm.Terminal 项目，引用 SSH.NET + Core + Tunnel
   退出信号：项目编译通过
2. 实现 ITerminalSession 接口和 TerminalOutputEventArgs
   退出信号：编译通过
3. 实现 IRenderer 接口和基础 TerminalRenderer（封装第三方库）
   退出信号：编译通过，TerminalRenderer 可接收文本并渲染
4. 实现 TerminalSession（SSH 直连 + ShellStream 数据回调）
   退出信号：单元测试覆盖 ConnectAsync + SendInput + Dispose
5. 实现跳板模式（通过 ITunnelManager 建立端口转发后连接）
   退出信号：单元测试覆盖跳板模式连接路径
6. 实现 TerminalControl（WinForms UserControl，承载渲染控件）
   退出信号：编译通过，TerminalControl 可 Attach/Detach session
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.Terminal/` 为新建目录，内部按 `Models/Rendering/` 组织，初始文件数 2-3 个/目录，不构成摊平

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | SSH 直连：config.Host="127.0.0.1"，无 JumpChain | TerminalSession 连接成功，IsConnected=true |
| 2 | 跳板模式：config.JumpChain 有 1 hop | 通过 Tunnel 建立端口转发后连接 |
| 3 | 终端输出：SSH 执行 "echo hello" | OutputReceived 事件触发，Text 含 "hello" |
| 4 | SendInput("ls\n") | 命令通过 ShellStream 发送到远程 |
| 5 | GetRecentOutput(10) | 返回最近 10 行输出 |
| 6 | Dispose | ShellStream 和 SshClient 都释放，IsConnected=false |
| 7 | 连接失败 | IsConnected=false，OutputReceived 触发错误消息 |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不支持 SSH 密钥认证 | 代码中无 PrivateKeyFile / PrivateKeyAuthenticationMethod |
| 2 | 不做终端录制/回放 | 代码中无录制/回放逻辑 |
| 3 | 不做多标签页管理 | TerminalControl 是单会话控件，无标签页逻辑 |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.Terminal 作为终端子系统 → 在模块结构中记录
- **接口**：ITerminalSession → 跨模块消费契约（UI、AI）
