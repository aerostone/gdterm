---
doc_type: feature-design
feature: 2026-07-24-ssh-tunnel
requirement: null
roadmap: gdterm
roadmap_item: ssh-tunnel
status: approved
summary: 实现 SSH.NET 隧道管理器——按跳板链逐跳建立 SSH 连接，支持本地端口转发和 SOCKS 代理，提供 ITunnelManager 接口
tags: [tunnel, ssh, ssh.net, port-forwarding, socks]
---

# ssh-tunnel — SSH 隧道管理器

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| 跳板链（JumpChain） | ConnectionConfig.JumpChain.Hops 的有序列表 | 由 Core 模型定义，本模块消费 |
| Hop | 跳板链中的一个 SSH 连接节点 | 不同于"SSH session"（本模块不用 shell session） |
| 端口转发（LocalForward） | 将本地端口流量通过 SSH 隧道转发到远程主机 | 不同于"直接连接" |
| 隧道建立 | 完成所有 Hop 的 SSH 连接 + 最后一跳的端口转发 | 全流程成功才叫"已建立" |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.Tunnel` 类库，实现 `ITunnelManager` 接口，支持：
- 按 JumpChain.Hops 顺序逐跳建立 SSH 连接
- 最后一跳建立端口转发（LocalForward 或 DynamicSocks）
- 自动分配端口（LocalPort = 0）
- 单 connectionId 单活跃隧道
- 异常携带失败 HopIndex

**为谁**：RDP 模块（通过隧道连接跳板后的目标机器）、SFTP 模块、Terminal 模块

**成功标准**：
- 可以通过 JumpChain 建立到目标机器的 SSH 隧道
- 建立成功后 TunnelEndpoint.LocalPort 可用于后续连接
- 异常时 HopIndex 指示失败在哪一跳

**明确不做**：
- 自动重连（v1 手动重连）
- 隧道健康检查/心跳（v1 靠使用方检测连接异常）
- 动态 SOCKS 代理的完整 SOCKS5 协议实现（v1 仅做端口转发）
- SSH shell 会话（归 Terminal 模块）

### 关键决策

1. **SSH.NET 作为底层库**：Renci.SshNet 纯托管，Win7/2008 兼容，NuGet 安装
2. **逐跳连接策略**：每个 Hop 建立一个 SshClient，后一跳通过前一跳的端口转发连接
3. **端口转发用 `ForwardedPortLocal`**：SSH.NET 原生支持，LocalPort=0 时自动分配
4. **连接生命周期**：TunnelManager 内部维护 `ConcurrentDictionary<connectionId, TunnelSession>`，每个 session 持有所有 hop 的 SshClient 和 forwarded port

### 前置依赖

- `core-models`（done ✅）：ConnectionConfig、JumpChainConfig、TunnelConfig、TunnelEndpoint

## 2. 名词与编排

### 2.1 名词层

**现状**：Core 模型已定义 TunnelConfig、TunnelEndpoint、JumpChainConfig、JumpHop

**变化**：新增以下类型

```csharp
// Gdterm.Tunnel.ITunnelManager — 隧道管理器接口（roadmap 4.2 完全一致）
public interface ITunnelManager
{
    Task<TunnelEndpoint> EstablishAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct);
    Task CloseAsync(string connectionId);
    TunnelStatus GetStatus(string connectionId);
}

// Gdterm.Tunnel.Models.TunnelStatus
public class TunnelStatus
{
    public bool IsActive { get; set; }
    public string LastError { get; set; }
    public TimeSpan Uptime { get; set; }
}

// Gdterm.Tunnel.Exceptions.TunnelException
public class TunnelException : Exception
{
    public int HopIndex { get; set; }  // 失败在哪一跳（0-based），-1 表示非跳板相关错误
    public string Host { get; set; }
    public int Port { get; set; }
}

// Gdterm.Tunnel.Models.TunnelSession — 内部类型，不对外暴露
// 持有一个 connectionId 对应的所有 hop SshClient 和 ForwardedPort
```

### 2.2 编排层

```
EstablishAsync(config, credential):
  1. 检查 connectionId 是否已有活跃隧道 → 有则先 Close
  2. 若 config.JumpChain 为 null（直连）→ 直接建立端口转发到 config.Host
  3. 逐跳建立 SSH 连接：
     hop[0]: SshClient(hop.Host, hop.Port, hop.Username, credential.Password)
     hop[i>0]: SshClient(hop.Host, hop.Port) 通过 hop[i-1] 的端口转发连接
  4. 最后一跳建立 ForwardedPortLocal → 获取 LocalPort
  5. 注册到 TunnelSession，返回 TunnelEndpoint
```

**流程级约束**：
- 任一 Hop 连接失败 → TunnelException(hopIndex=i)，已建立的连接全部释放
- 连接超时统一 30 秒
- 一个 connectionId 同时只允许一个隧道，重复调用先关闭旧的

### 2.3 挂载点清单

本 feature 不引入新挂入点。`ITunnelManager` 接口由各使用模块通过 DI 消费，无路由/配置/事件订阅/UI 注入。

### 2.4 推进策略

```
1. 创建 Gdterm.Tunnel 项目，引用 SSH.NET + Core，配置目录结构
   退出信号：项目编译通过
2. 实现 TunnelException 和 TunnelStatus 类型
   退出信号：编译通过
3. 实现 TunnelSession 内部类（管理多个 SshClient 的生命周期）
   退出信号：单元测试覆盖 ConnectHop / DisposeAll
4. 实现 TunnelManager.EstablishAsync（直连模式 + 跳板模式）
   退出信号：单元测试覆盖直连和跳板两种路径
5. 实现 CloseAsync / GetStatus
   退出信号：单元测试覆盖关闭和状态查询
6. 端到端集成验证
   退出信号：通过 localhost 隧道连接真实 SSH 服务
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.Tunnel/` 为新建目录，内部按 `Models/Exceptions/` 组织，初始文件数 2-3 个/目录，不构成摊平

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | 直连模式：config.JumpChain=null，config.Host="127.0.0.1" | 建立端口转发，TunnelEndpoint.LocalPort > 0 |
| 2 | 跳板模式：JumpChain.Hops 有 1 个 hop | 逐跳连接，端口转发成功 |
| 3 | 多跳：Hops 有 2+ 个 hop | 每跳通过前一跳的转发连接 |
| 4 | 连接失败：hop[1] 连接超时 | TunnelException.HopIndex=1，已建立连接全部释放 |
| 5 | LocalPort=0 | 自动分配可用端口 |
| 6 | 重复调用 EstablishAsync 同一 connectionId | 旧隧道先关闭，再建新的 |
| 7 | CloseAsync | 隧道关闭，GetStatus.IsActive=false |
| 8 | 隧道关闭后 LocalPort 不可再连接 | 端口不再监听 |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不自动重连 | 代码中无 reconnect / retry 循环 |
| 2 | 不做心跳 | 代码中无 timer / ping 机制 |
| 3 | 不做完整 SOCKS5 | 代码中无 SOCKS5 协议实现 |
| 4 | 不做 SSH shell 会话 | 代码中无 ShellStream / ExecuteCommand |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.Tunnel 作为 SSH 隧道子系统 → 在模块结构中记录
- **接口**：ITunnelManager → 跨模块消费契约
