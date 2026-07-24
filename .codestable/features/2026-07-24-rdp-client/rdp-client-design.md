---
doc_type: feature-design
feature: 2026-07-24-rdp-client
requirement: null
roadmap: gdterm
roadmap_item: rdp-client
status: approved
summary: 实现 RDP 客户端——AxMsTscLib ActiveX 控件封装、直连和跳板链模式（通过 Tunnel 端口转发连 localhost）、RDP 事件处理（连接/断开/错误）
tags: [rdp, mstsc, activex, remote-desktop]
---

# rdp-client — RDP 客户端

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| RDP 会话（RDP Session） | 一个远程桌面连接实例 | 不同于 SSH 会话 |
| 直连模式 | 直接连接目标主机 RDP 端口 | 不经过跳板 |
| 跳板模式 | 通过 SSH 隧道端口转发连接 localhost:LocalPort | 经过跳板链 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.Rdp` 类库，实现 `IRdpClient` 接口，封装 AxMsTscLib ActiveX 控件，支持直连和跳板链两种模式。

**为谁**：UI 模块（RDP 标签页）

**成功标准**：
- 可通过 AxMsTscLib 建立 RDP 连接
- 支持直连模式（直接连接目标主机）
- 支持跳板模式（通过 Tunnel 端口转发连 localhost）
- 连接/断开/错误事件正确触发

**明确不做**：
- RDP 文件传输
- RDP 剪贴板共享
- RDP 打印重定向
- 多显示器支持

### 关键决策

1. **ActiveX 封装**：封装 AxMsTscLib 到 WinForms UserControl
2. **跳板模式**：通过 TunnelManager 建立端口转发后，RDP 连接 localhost:LocalPort
3. **凭据注入**：通过 CredentialPayload 设置用户名/密码
4. **事件机制**：RDP 事件通过 IRdpClient 事件暴露

### 前置依赖

- `core-models`（done ✅）：ConnectionConfig、CredentialPayload
- `ssh-tunnel`（done ✅）：TunnelManager（跳板模式端口转发）

## 2. 名词与编排

### 2.1 名词层

```csharp
// Gdterm.Rdp.IRdpClient — 公开接口
public interface IRdpClient : IDisposable
{
    /// <summary>
    /// 连接 RDP 会话
    /// </summary>
    void Connect(ConnectionConfig config, CredentialPayload credential);

    /// <summary>
    /// 通过隧道连接（跳板模式）
    /// </summary>
    void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint);

    /// <summary>
    /// 断开连接
    /// </summary>
    void Disconnect();

    /// <summary>
    /// 是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 获取承载 ActiveX 控件的 UserControl（UI 嵌入用）
    /// </summary>
    System.Windows.Forms.UserControl Control { get; }

    /// <summary>
    /// 连接状态变化事件
    /// </summary>
    event EventHandler<RdpStateChangedEventArgs> StateChanged;
}

// Gdterm.Rdp.Models.RdpStateChangedEventArgs
public class RdpStateChangedEventArgs : EventArgs
{
    public bool IsConnected { get; set; }
    public string Reason { get; set; }  // "connected" / "disconnected" / "error"
    public string ErrorMessage { get; set; }
}
```

### 2.2 编排层

```
直连模式：
  UI 调用 IRdpClient.Connect(config, credential)
  → 设置 AxMsTscLib.Server = config.Host
  → 设置 AxMsTscLib.UserName = credential.Username
  → 设置 AxMsTscLib.AdvancedSettings7.ClearTextPassword = credential.Password
  → AxMsTscLib.Connect()

跳板模式：
  TunnelManager 建立端口转发 → TunnelEndpoint(localhost, localPort)
  → UI 调用 IRdpClient.ConnectViaTunnel(config, credential, tunnelEndpoint)
  → 设置 AxMsTscLib.Server = "localhost"
  → 设置 AxMsTscLib.AdvancedSettings7.RDPPort = tunnelEndpoint.LocalPort
  → AxMsTscLib.Connect()
```

**流程级约束**：
- 跳板模式必须先建立隧道，再连接 RDP
- 断开时同时清理隧道（如果是跳板模式）
- 连接/断开/错误事件必须在 UI 线程触发

### 2.3 挂载点清单

本 feature 不引入新挂入点。IRdpClient 由 UI 模块通过 DI 消费。

### 2.4 推进策略

```
1. 创建 Gdterm.Rdp 项目，引用 Core + Tunnel
   退出信号：项目编译通过
2. 实现 RdpStateChangedEventArgs 数据类
   退出信号：编译通过
3. 实现 IRdpClient 接口
   退出信号：编译通过
4. 实现 RdpClient（AxMsTscLib 封装、直连/跳板模式、事件处理）
   退出信号：编译通过，覆盖 Connect/ConnectViaTunnel/Disconnect
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.Rdp/` 为新建目录

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | Connect(host, credential) | 直连 RDP |
| 2 | ConnectViaTunnel(config, cred, endpoint) | 通过隧道连 RDP |
| 3 | Disconnect | 断开连接 |
| 4 | 连接成功 | StateChanged(connected) |
| 5 | 连接失败 | StateChanged(error) |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不做 RDP 文件传输 | 代码中无文件传输 |
| 2 | 不做剪贴板共享 | 代码中无剪贴板操作 |
| 3 | 不做打印重定向 | 代码中无打印逻辑 |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.Rdp 作为 RDP 客户端子系统
- **接口**：IRdpClient → UI 模块消费契约
