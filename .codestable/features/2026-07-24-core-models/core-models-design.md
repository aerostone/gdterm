---
doc_type: feature-design
feature: 2026-07-24-core-models
requirement: null
roadmap: gdterm
roadmap_item: core-models
status: approved
summary: 定义 gdterm 所有模块共享的核心数据模型（ConnectionConfig、CredentialRef、TunnelEndpoint 等），零业务逻辑
tags: [core, models, types, foundation]
---

# core-models — 核心数据模型定义

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| ConnectionConfig | 一个远程连接的完整配置（主机、协议、凭据引用、跳板链、隧道） | 不同于 UI 层的 "连接会话"（运行时状态） |
| CredentialRef | 对 KeePass 条目的引用（UUID），不含密码明文 | 不同于 CredentialPayload（含明文，运行时临时对象） |
| JumpChain | 从客户端到目标机器的跳板节点有序列表 | 每个节点叫 JumpHop |
| TunnelEndpoint | 隧道建立后的本地接入点（host:port） | 隧道配置叫 TunnelConfig |
| ProtocolType | 连接协议枚举：RDP、SSH | v1 仅两种 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.Core` 类库项目，定义所有模块共享的数据模型和枚举。
**为谁**：所有其他模块（Connections、Tunnel、Terminal、RDP、SFTP、KeePass、AI、Logging、Security、UI）。
**成功标准**：其他模块引用 `Gdterm.Core` 后能使用 ConnectionConfig 等类型进行数据交换，编译通过。
**明确不做**：
- 业务逻辑（验证、持久化、计算）——归各功能模块
- 接口定义（ITunnelManager、IKeePassService 等）——归各功能模块
- 依赖注入 / 服务注册——归 UI 启动层

### 复杂度档位

走 .NET 类库默认档位，无偏离。

### 关键决策

1. **项目结构**：独立 `Gdterm.Core` 类库项目，所有模块引用它。
   - 替代方案：放在主项目的 Models 目录下。被拒：其他模块无法独立引用，会变成循环依赖。
2. **密码存储策略**：ConnectionConfig 不存密码明文，只存 `CredentialRefId` 指向 KeePass 条目。
   - 替代方案：ConnectionConfig 直接存密码。被拒：违反安全设计原则，密码统一由 KeePass 管理。
3. **可变类 vs 不可变类**：使用可变 POCO（get/set），不用 record 或 immutable。
   - 理由：WinForms 绑定需要属性 setter，.NET 4.6.2 不支持 record。
4. **序列化**：模型本身不带 `[JsonProperty]` 等序列化注解。序列化细节归 Connections 模块（用 JsonSerializerSettings 处理）。
   - 理由：核心模型不应耦合特定序列化库。

### 前置依赖

无（首个 feature）。

## 2. 名词与编排

### 2.1 名词层

**现状**：无，全新项目。

**变化**：新增以下类型。

#### 枚举

```csharp
// Gdterm.Core.Enums.ProtocolType
public enum ProtocolType
{
    RDP = 0,
    SSH = 1
}

// Gdterm.Core.Enums.TunnelType
public enum TunnelType
{
    LocalForward = 0,
    DynamicSocks = 1
}
```

#### 核心模型

```csharp
// Gdterm.Core.Models.ConnectionConfig — 一个远程连接的完整配置
public class ConnectionConfig
{
    public string Id { get; set; }              // GUID，存储层全局唯一
    public string Name { get; set; }            // 显示名称
    public ProtocolType Protocol { get; set; }  // RDP | SSH
    public string Host { get; set; }            // 目标主机（IP 或域名）
    public int Port { get; set; }               // 目标端口（RDP 默认 3389，SSH 默认 22）
    public string Username { get; set; }        // 用户名
    public string Domain { get; set; }          // RDP 域名，SSH 不用
    public string GroupPath { get; set; }       // 树形分组路径，如 "Jump/Web"
    public JumpChainConfig JumpChain { get; set; }  // null = 直连
    public TunnelConfig Tunnel { get; set; }    // null = 不使用隧道
    public string CredentialRefId { get; set; } // 关联 KeePass 条目 UUID，null = 不关联
    public Dictionary<string, string> Metadata { get; set; }  // 扩展字段
}

// 示例：
// new ConnectionConfig {
//     Id = "550e8400-...",
//     Name = "Web Server",
//     Protocol = ProtocolType.SSH,
//     Host = "10.0.1.50",
//     Port = 22,
//     Username = "admin",
//     GroupPath = "Jump/Web",
//     JumpChain = new JumpChainConfig { Hops = [...] },
//     Tunnel = new TunnelConfig { Type = TunnelType.LocalForward, ... },
//     CredentialRefId = "kps-entry-uuid"
// }
```

```csharp
// Gdterm.Core.Models.JumpChainConfig — 跳板链配置
public class JumpChainConfig
{
    public List<JumpHop> Hops { get; set; }  // 按顺序的跳板节点，不可为空列表
}
// null 表示直连；空列表非法（由调用方校验）
```

```csharp
// Gdterm.Core.Models.JumpHop — 跳板链中的单个节点
public class JumpHop
{
    public string Host { get; set; }
    public int Port { get; set; }               // 默认 22
    public string Username { get; set; }
    public string CredentialRefId { get; set; } // 可选，关联 KeePass
}
```

```csharp
// Gdterm.Core.Models.TunnelConfig — 隧道配置
public class TunnelConfig
{
    public TunnelType Type { get; set; }      // LocalForward | DynamicSocks
    public int LocalPort { get; set; }        // 本地监听端口（0 = 自动分配）
    public string RemoteHost { get; set; }    // 远程目标（LocalForward 时）
    public int RemotePort { get; set; }       // 远程端口（LocalForward 时）
}
// DynamicSocks 时 RemoteHost/RemotePort 忽略
```

```csharp
// Gdterm.Core.Models.TunnelEndpoint — 隧道建立后的本地接入点
public class TunnelEndpoint
{
    public string LocalHost { get; set; }   // 通常 "127.0.0.1"
    public int LocalPort { get; set; }      // 转发后的本地端口
    public string ConnectionId { get; set; }
}
```

```csharp
// Gdterm.Core.Models.CredentialPayload — 运行时凭据（含明文，不持久化）
public class CredentialPayload
{
    public string Username { get; set; }
    public string Password { get; set; }
}
// 仅在运行时传递给 SSH/RDP 连接使用，不写入任何持久化存储
```

#### 常量

```csharp
// Gdterm.Core.Constants.DefaultPorts
public static class DefaultPorts
{
    public const int Rdp = 3389;
    public const int Ssh = 22;
}
```

### 2.2 编排层

**现状**：无，全新项目。

**变化**：本 feature 纯类型定义，无运行时编排逻辑。所有类型为被动数据容器（POCO），不包含业务逻辑、状态机或控制流。

数据流方向（供理解，非本 feature 实现）：

```
Connections 模块  ──读写──→  ConnectionConfig（JSON 序列化/反序列化）
       ↓
UI / Terminal / RDP  ──消费──→  ConnectionConfig（读取配置建立连接）
       ↓
KeePass 模块  ──解析──→  CredentialRefId → CredentialPayload（解密后）
       ↓
Tunnel 模块  ──使用──→  JumpChainConfig + TunnelConfig → TunnelEndpoint
```

### 2.3 挂载点清单

本 feature 不引入新挂入点。仅创建新的类库项目和类型定义，无路由、配置、事件订阅、UI 注入。

### 2.4 推进策略

```
1. 创建 .NET Framework 4.6.2 类库项目 Gdterm.Core
   退出信号：项目编译通过（空项目）
2. 添加枚举类型（ProtocolType、TunnelType）
   退出信号：编译通过，枚举值与 roadmap 4.1 一致
3. 添加核心模型类（ConnectionConfig、JumpChainConfig、JumpHop、TunnelConfig）
   退出信号：编译通过，属性与 roadmap 4.1 契约一致
4. 添加辅助类型（TunnelEndpoint、CredentialPayload、DefaultPorts）
   退出信号：编译通过，所有类型可被外部项目引用
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.Core/` 为新建目录，内部按 `Models/` 和 `Enums/` 和 `Constants/` 组织，初始文件数 3-4 个/目录，不构成摊平

##### 结论：不做

全新项目，无现有代码需重构，目录结构合理。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | 创建 ConnectionConfig 实例并设置所有属性 | 属性正确赋值，无异常 |
| 2 | ConnectionConfig.JumpChain = null 表示直连 | null 语义明确，不抛异常 |
| 3 | ConnectionConfig.Tunnel = null 表示不使用隧道 | null 语义明确，不抛异常 |
| 4 | CredentialRefId = null 表示不关联密码条目 | null 语义明确，不抛异常 |
| 5 | JumpChainConfig.Hops 有多个 JumpHop | 顺序保持（List 保证） |
| 6 | TunnelConfig.LocalPort = 0 表示自动分配 | 0 的语义由 Tunnel 模块消费，Core 只定义 |
| 7 | CredentialPayload 在不同 CredentialPayload 实例间无共享引用 | POCO 独立，修改一个不影响另一个 |
| 8 | 其他项目引用 Gdterm.Core 后能使用所有类型 | 编译通过，命名空间正确 |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不含业务逻辑 | Gdterm.Core 中无 `if/else` 分支逻辑（属性 getter/setter 除外） |
| 2 | 不含接口定义 | Gdterm.Core 中无 `interface` 关键字（ITunnelManager 等归各模块） |
| 3 | 不含序列化注解 | Gdterm.Core 中无 `[JsonProperty]`、`[XmlElement]` 等特性 |
| 4 | 不含密码明文持久化 | ConnectionConfig 无 `Password` 属性（只有 CredentialRefId） |

## 4. 与项目级架构文档的关系

本 feature 是项目首个代码 feature，创建 `Gdterm.Core` 类库。

**acceptance 阶段需提炼回 architecture：**
- **名词**：ConnectionConfig、JumpChainConfig、TunnelConfig、CredentialPayload → 在 ARCHITECTURE.md 加"核心模型"描述
- **模块**：Gdterm.Core 作为基础依赖模块 → 在模块结构中记录
