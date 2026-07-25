---
doc_type: roadmap
slug: ops-toolbox
status: active
created: 2026-07-25
last_reviewed: 2026-07-25
tags: [ops-tools, network, certificate, ntp, scanner, portable, win7]
related_requirements: []
related_architecture: []
---

# ops-toolbox — 运维工具箱

## 1. 背景

gdterm 已具备 SSH/RDP/SFTP/Terminal/KeePass/AI 全链路能力，但运维人员日常还需要大量"围绕机器"的辅助操作：装证书、配源、校时、扫端口、发现资产。目前这些操作要么跳出去用别的工具（MobaXterm、nmap、SecureCRT 内置工具），要么手动 SSH 逐台执行。

本 roadmap 将 gdterm 扩展为一站式运维入口：在已有的连接管理基础上，内建一套运维工具模块，覆盖"后连接运维"和"网络侦察"两大场景。

技术约束：.NET Framework 4.6.2 + WinForms，绿色便携，Win7/Server 2008 兼容，不引入外部 native 依赖。

## 2. 范围与明确不做

### 本 roadmap 覆盖

**后连接运维工具**（通过 SSH 在远程目标机器上执行）：
- 证书安装器：本地/远程安装 SSL 证书，支持证书链验证
- 时间同步：本地/远程 NTP 校时，可配置 NTP 服务器
- 仓库配置：配置远程机器的 yum/apt/zypper 软件源

**网络侦察工具**（本地执行，扫描远程目标）：
- 端口扫描：探测指定主机的开放端口
- IP 扫描：发现网段内活跃主机
- DNS 查询：正反向解析、多种记录类型
- Traceroute：路由追踪 + 延迟可视化
- Ping 监控：持续监控主机可达性
- 子网计算器：CIDR/掩码/范围计算
- WHOIS 查询：域名/IP 归属查询

**工具框架**：
- IToolModule 统一接口，工具菜单注册展示
- 所有工具支持配置文件定制（`data/config/tools/*.json`）

### 明确不做

- **插件系统**：不做 DLL 动态加载/热插拔，工具内建为项目模块
- **CVE 漏洞检查**：需要庞大的 CVE 数据库，太重
- **SNMP Walk**：需要 MIB 库，增加 native 依赖
- **LLDP/CDP 发现**：需要原始套接字，Win7 兼容性问题
- **X11 Server**：Win7 上不现实
- **工具结果持久化**：v1 不做扫描结果导出/历史记录（记观察项）
- **远程执行密码破解**：超出运维工具范围
- **网络流量分析**：需要 pcap native 依赖

## 3. 模块拆分（概设）

```
gdterm
├── (existing 11 projects)
│
└── Tools/
    ├── Gdterm.Tools.Core        — 工具框架（IToolModule 接口、ToolRegistry 注册表、工具配置基类）
    ├── Gdterm.Tools.CertInstaller — 证书安装器（本地 certutil + 远程 SSH）
    ├── Gdterm.Tools.TimeSync     — 时间同步（本地 w32tm + 远程 ntpdate/chrony）
    ├── Gdterm.Tools.RepoConfig   — 仓库配置（远程 SSH 写 repo 文件）
    ├── Gdterm.Tools.PortScanner  — 端口扫描（TcpClient 异步连接）
    ├── Gdterm.Tools.NetScanner   — 网络侦察合集（IP扫描/DNS/Traceroute/Ping/子网计算/WHOIS）
    └── Gdterm.Tools.UI           — 工具箱 UI（工具列表面板、各工具配置对话框）
```

### Gdterm.Tools.Core · 工具框架
- **职责**：定义 `IToolModule` / `IRemoteToolModule` 接口、`ToolRegistry` 工具注册与发现、`ToolConfigBase` 配置基类。纯框架，零业务逻辑。
- **依赖**：Gdterm.Core（模型）
- **承载的子 feature**：`tools-core`

### Gdterm.Tools.CertInstaller · 证书安装器
- **职责**：本地安装 Windows 证书（certutil）、远程通过 SSH 推送证书文件并执行安装命令（update-ca-certificates / certutil）。支持 PEM/DER/PFX/P12 格式。可配置证书存储路径和信任链策略。
- **依赖**：Tools.Core, Terminal（SSH 会话）
- **承载的子 feature**：`tools-cert-installer`

### Gdterm.Tools.TimeSync · 时间同步
- **职责**：本地 NTP 校时（w32tm）、远程通过 SSH 执行 ntpdate/chronyc。可配置 NTP 服务器列表（公网/内网）、同步间隔、偏移阈值。支持手动/自动同步。
- **依赖**：Tools.Core, Terminal（SSH 会话）
- **承载的子 feature**：`tools-time-sync`

### Gdterm.Tools.RepoConfig · 仓库配置
- **职责**：通过 SSH 在远程机器上配置软件源。支持 yum（/etc/yum.repos.d/）、apt（/etc/apt/sources.list.d/）、zypper。可配置源 URL 列表、GPG key 路径、目标路径模板。
- **依赖**：Tools.Core, Terminal（SSH 会话）
- **承载的子 feature**：`tools-repo-config`

### Gdterm.Tools.PortScanner · 端口扫描
- **职责**：TcpClient 异步连接扫描，探测指定主机的开放端口。支持端口范围（1-65535）、常用端口预设、超时和并发数配置。结果按状态分类（开放/关闭/过滤）。
- **依赖**：Tools.Core
- **承载的子 feature**：`tools-port-scanner`

### Gdterm.Tools.NetScanner · 网络侦察合集
- **职责**：集成 6 个网络工具——IP 扫描（ICMP + TCP 探测活跃主机）、DNS 查询（A/MX/TXT/CNAME/SOA/PTR）、Traceroute（UDP/ICMP 逐跳追踪）、Ping 监控（持续 ICMP 监控+延迟图表）、子网计算器（CIDR 转换/可用主机数）、WHOIS 查询（域名/IP 归属）。每个工具作为 NetScanner 子模块，共享项目引用。
- **依赖**：Tools.Core
- **承载的子 feature**：`tools-net-scanner`

### Gdterm.Tools.UI · 工具箱 UI
- **职责**：工具菜单/面板注册、工具列表展示、各工具的配置对话框、工具执行结果展示。集成到 MainForm。
- **依赖**：Tools.Core, 所有工具项目
- **承载的子 feature**：`tools-ui`

## 4. 接口契约

### 4.1 IToolModule — 工具模块统一接口

```csharp
namespace Gdterm.Tools.Core
{
    /// <summary>
    /// 所有工具模块实现此接口
    /// </summary>
    public interface IToolModule
    {
        /// <summary>工具唯一标识（如 "cert-installer"）</summary>
        string ToolId { get; }

        /// <summary>显示名称（如 "证书安装器"）</summary>
        string DisplayName { get; }

        /// <summary>工具简述</summary>
        string Description { get; }

        /// <summary>分类（"RemoteOps" / "Network"）</summary>
        string Category { get; }

        /// <summary>创建工具 UI 面板（每次调用返回新实例）</summary>
        Control CreatePanel();

        /// <summary>从配置文件加载设置</summary>
        void LoadConfig(string configPath);

        /// <summary>保存设置到配置文件</summary>
        void SaveConfig(string configPath);
    }

    /// <summary>
    /// 需要 SSH 会话的远程工具扩展接口
    /// </summary>
    public interface IRemoteToolModule : IToolModule
    {
        /// <summary>设置目标机器的 SSH 客户端（由 UI 层在连接建立后注入）</summary>
        void SetSshSession(SshClient client);

        /// <summary>清除 SSH 会话（断开连接时调用）</summary>
        void ClearSshSession();
    }
}
```

### 4.2 ToolRegistry — 工具注册表

```csharp
namespace Gdterm.Tools.Core
{
    /// <summary>
    /// 工具注册与发现
    /// </summary>
    public class ToolRegistry
    {
        private readonly Dictionary<string, IToolModule> _tools;

        /// <summary>注册工具模块</summary>
        void Register(IToolModule tool);

        /// <summary>按 ID 获取工具</summary>
        IToolModule GetTool(string toolId);

        /// <summary>获取所有工具（按分类分组）</summary>
        IList<ToolCategory> GetAllTools();

        /// <summary>加载所有工具配置</summary>
        void LoadAllConfigs(string configDirectory);

        /// <summary>保存所有工具配置</summary>
        void SaveAllConfigs(string configDirectory);
    }

    public class ToolCategory
    {
        public string Name { get; set; }      // "远程运维" / "网络侦察"
        public string CategoryKey { get; set; } // "RemoteOps" / "Network"
        public IList<IToolModule> Tools { get; set; }
    }
}
```

### 4.3 ToolConfigBase — 配置基类

```csharp
namespace Gdterm.Tools.Core
{
    /// <summary>
    /// 工具配置基类——JSON 文件读写（手写序列化，无外部 JSON 库）
    /// 每个工具继承此类，定义自己的配置属性
    /// </summary>
    public abstract class ToolConfigBase
    {
        /// <summary>从 JSON 文件加载</summary>
        public void LoadFromFile(string path);

        /// <summary>保存到 JSON 文件</summary>
        public void SaveToFile(string path);

        /// <summary>重置为默认值</summary>
        public abstract void ResetDefaults();
    }
}
```

### 4.4 远程执行协议

远程工具（CertInstaller / TimeSync / RepoConfig）通过 `IRemoteToolModule.SetSshSession(SshClient)` 获取 SSH 连接，然后使用 `SshClient.RunCommand(string)` 执行远程命令。

```csharp
// 远程执行结果
public class RemoteCommandResult
{
    public string Command { get; set; }
    public string Stdout { get; set; }
    public string Stderr { get; set; }
    public int ExitCode { get; set; }
    public bool IsSuccess => ExitCode == 0;
}

// 文件传输到远程（证书安装器用）
public interface IRemoteFileTransfer
{
    /// <summary>上传本地文件到远程临时目录</summary>
    string UploadToTemp(string localPath, SshClient client);

    /// <summary>删除远程临时文件</summary>
    void CleanupTemp(string remotePath, SshClient client);
}
```

### 4.5 各工具配置文件格式

所有配置存放在 `data/config/tools/` 目录：

**cert-installer.json**
```json
{
  "certStorePath": "/etc/ssl/certs",
  "trustChainPolicy": "full",
  "remoteInstallScript": "update-ca-certificates",
  "supportedFormats": ["PEM", "DER", "PFX", "P12"],
  "defaultFormat": "PEM"
}
```

**time-sync.json**
```json
{
  "ntpServers": ["ntp.aliyun.com", "cn.pool.ntp.org", "time.windows.com"],
  "syncIntervalMinutes": 60,
  "offsetThresholdMs": 1000,
  "remoteCommand": "ntpdate",
  "fallbackCommand": "chronyc makestep"
}
```

**repo-config.json**
```json
{
  "yumRepos": [
    {"name": "base", "url": "http://mirrors.aliyun.com/centos/$releasever/os/$basearch/", "gpgcheck": true}
  ],
  "aptSources": [
    {"url": "http://mirrors.aliyun.com/ubuntu/", "distribution": "jammy", "components": "main restricted universe multiverse"}
  ],
  "backupOriginal": true,
  "targetPathYum": "/etc/yum.repos.d/",
  "targetPathApt": "/etc/apt/sources.list.d/"
}
```

**port-scanner.json**
```json
{
  "defaultPorts": [22, 80, 443, 3389, 3306, 5432, 6379, 8080, 8443],
  "timeoutMs": 3000,
  "maxConcurrency": 100,
  "commonPortPresets": {
    "web": [80, 443, 8080, 8443],
    "database": [3306, 5432, 6379, 27017, 1521],
    "remote": [22, 3389, 5900, 23]
  }
}
```

**net-scanner.json**
```json
{
  "ipScan": {"timeoutMs": 1000, "maxConcurrency": 50, "methods": ["ICMP", "TCP"]},
  "dns": {"defaultServers": ["114.114.114.114", "223.5.5.5"], "timeoutMs": 3000},
  "traceroute": {"maxHops": 30, "timeoutMs": 3000, "protocol": "ICMP"},
  "ping": {"intervalMs": 1000, "count": 0, "alertThresholdMs": 500}
}
```

## 5. 子 feature 清单

### Layer 0：工具框架（无依赖）

| # | slug | 描述 | 最小闭环 | depends_on |
|---|---|---|---|---|
| 1 | `tools-core` | IToolModule/IRemoteToolModule 接口、ToolRegistry 注册表、ToolConfigBase 配置基类、RemoteCommandResult 模型 | ✅ 定义完接口+注册机制，UI 菜单能看到空工具列表 | core-models |

### Layer 1：独立工具（依赖 tools-core，互不依赖，可并行）

| # | slug | 描述 | 最小闭环 | depends_on |
|---|---|---|---|---|
| 2 | `tools-cert-installer` | 证书安装器——本地 certutil 安装 + 远程 SSH 推送+安装，支持 PEM/DER/PFX/P12，可配置证书存储路径和信任链策略 | 本地安装一个 .cer 文件成功 | tools-core, terminal-emulator |
| 3 | `tools-time-sync` | 时间同步——本地 w32tm + 远程 ntpdate/chrony，可配置 NTP 服务器列表/同步间隔/偏移阈值 | 本地同步 NTP 成功 | tools-core, terminal-emulator |
| 4 | `tools-repo-config` | 仓库配置——远程 SSH 配置 yum/apt/zypper 源，可配置源 URL/GPG key/目标路径，备份原文件 | 远程写入一个 yum repo 文件成功 | tools-core, terminal-emulator |
| 5 | `tools-port-scanner` | 端口扫描——TcpClient 异步连接，端口范围/常用预设/超时/并发配置，结果按开放/关闭/过滤分类 | 扫描 localhost 22,80,443 返回结果 | tools-core |
| 6 | `tools-net-scanner` | 网络侦察合集——IP 扫描/DNS 查询/Traceroute/Ping 监控/子网计算器/WHOIS，6 个子工具共享一个项目 | DNS 查询一个域名返回 A 记录 | tools-core |

### Layer 2：UI 集成（依赖所有工具）

| # | slug | 描述 | 最小闭环 | depends_on |
|---|---|---|---|---|
| 7 | `tools-ui` | 工具箱 UI——工具菜单/面板注册、工具列表展示、配置对话框、执行结果展示，集成到 MainForm 工具菜单 | 菜单点击打开工具面板，执行一个操作返回结果 | tools-core, tools-cert-installer, tools-time-sync, tools-repo-config, tools-port-scanner, tools-net-scanner |

### 最小闭环

**第一条做 `tools-core`**——定义完接口 + 注册机制后，UI 工具菜单能显示空列表。后续每加一个工具，菜单自动多一项。

### 排期建议

```
Layer 0:  tools-core（1 条）
Layer 1:  5 条独立工具可并行
Layer 2:  tools-ui（1 条，最后集成）
总计: 7 条子 feature
```

## 6. 观察项

1. **端口扫描结果导出**：用户可能需要导出扫描结果为 CSV/JSON，v1 不做但后续可加
2. **工具执行审计日志**：工具操作（特别是远程执行）是否接入 IAuditLogger？建议接入
3. **SSH 连接复用**：远程工具是否复用已有的 Terminal SSH 会话，还是独立建立？当前设计是独立会话
4. **并发扫描限制**：IP 扫描和端口扫描的并发数需要在低性能机器（Win7）上测试
5. **ICMP 权限**：Win7 上 Ping 需要管理员权限或防火墙放行，非管理员可能无法使用 IP 扫描的 ICMP 模式
6. **NetScanner 合集 vs 独立项目**：6 个网络工具合在一个项目里减少项目数量，但如果某个工具特别复杂可能需要拆出
