# gdterm - 安全增强与运维工具箱路线图

> 版本: post-MVP-enhancements v1.0
> 日期: 2026-07-25
> 来源: 对标 25+ 终端工具产品分析 + 用户需求

---

## 1. 路线图总览

本路线图覆盖三大方向、21 个子特性，分 5 层依赖：

| 层 | 特性 | 优先级 | 预计工作量 |
|----|------|--------|-----------|
| **0** | password-analyzer | P0 | 中 |
| **1** | credential-inheritance | P0 | 小 |
| **1** | session-persistence | P1 | 中 |
| **1** | quick-commands | P1 | 小 |
| **1** | tools-core | P0 | 中 |
| **2** | reconnect-button | P1 | 小 |
| **2** | reauth-sensitive-ops | P0 | 中 |
| **2** | connection-templates | P1 | 中 |
| **2** | ai-multi-model | P1 | 中 |
| **2** | tools-cert-installer | P1 | 大 |
| **2** | tools-time-sync | P1 | 中 |
| **2** | tools-repo-config | P1 | 大 |
| **2** | tools-port-scanner | P2 | 中 |
| **2** | tools-net-scanner | P2 | 大 |
| **3** | sftp-enhancements | P1 | 大 |
| **3** | macro-recording | P2 | 大 |
| **3** | ai-streaming | P1 | 中 |
| **3** | command-templates | P1 | 中 |
| **4** | terminal-enhancements | P2 | 中 |
| **4** | multi-channel-enhancements | P2 | 中 |
| **4** | tools-ui | P1 | 中 |

---

## 2. 现状分析（对标产品）

对标产品：WindTerm、MobaXterm、Tabby、WezTerm、Warp、Ghostty、Kitty、
SecureCRT、Royal TS、NETworkManager、electerm、Xshell、ZOC、iTerm2、
PuTTY、Bitvise、Xterminal、FinalShell、堡垒机产品。

### gdterm 已实现（32 项）
完整 SSH、RDP（含磁盘重定向）、SFTP、KeePass 集成、AI 助手、
安全锁定、审计日志、多通道输入、危险命令检测、分屏、
串口、Zmodem、会话书签、全局热键、终端配色方案、
密码生成器、密码强度验证、Auto-Type、SSH 密钥管理、RDP cmdkey 注入、
多通道终端就绪检测、命令历史记录、焦点模式、便携目录、初始化向导。

### 对标发现的缺口（30 项）
| # | 缺口 | 对标产品 | gdterm 现状 |
|---|------|---------|------------|
| 1 | 会话状态保存/恢复 | Tabby、iTerm2、ZOC | 无 |
| 2 | 会话搜索/过滤 | MobaXterm、SecureCRT | 无 |
| 3 | 连接继承（凭据/隧道） | SecureCRT、Royal TS | 无 |
| 4 | 重连按钮 | 全部 | 无 |
| 5 | 连接模板 | Royal TS、Xshell | 无 |
| 6 | 透明窗口 | WezTerm、Ghostty | 无 |
| 7 | 快捷命令面板 | Xshell、FinalShell | 无 |
| 8 | 宏录制 | SecureCRT、ZOC | 无 |
| 9 | 密码分析器 | KeePassXC | 无 |
| 10 | 敏感操作二次验证 | 堡垒机产品 | 无 |
| 11 | AI 多模型 | ChatGPT Desktop、Cursor | 单模型 |
| 12 | AI 流式响应 | 所有 AI 工具 | 无 |
| 13 | 命令模板库 | Xshell、FinalShell | 无 |
| 14 | SFTP 文件预览 | WinSCP、FileZilla | 无 |
| 15 | SFTP 权限编辑 | WinSCP | 无 |
| 16 | 多通道同步录制 | WindTerm | 无 |
| 17 | 凭据继承 UI | SecureCRT | 有字段无 UI |
| 18 | 证书安装工具 | 运维需求 | 无 |
| 19 | 时间同步工具 | 运维需求 | 无 |
| 20 | 软件仓库配置 | 运维需求 | 无 |
| 21 | 端口扫描 | NETworkManager | 无 |
| 22 | 网络扫描套件 | NETworkManager | 无 |

---

## 3. 产品差距分析

### 3.1 凭据管理差距

**现状**：
- KeePass 集成完整（存储、Auto-Type、SSH 密钥、RDP cmdkey、密码生成器）
- `CredentialRefId` 字段存在但 UI 无绑定
- 连接无继承机制，每个连接独立配置凭据

**差距**：
- ❌ 无连接继承（SecureCRT: 文件夹级凭据+隧道继承）
- ❌ 无敏感操作二次验证（堡垒机: sudo/密码修改需再认证）
- ❌ 无密码分析器（KeePassXC: 重复/弱/过期密码检测）
- ❌ 无连接模板（Royal TS: 预置连接模板）

### 3.2 会话管理差距

**现状**：
- 双击连接打开终端，书签+最近连接持久化
- 关闭 tab 丢失会话状态

**差距**：
- ❌ 无会话状态保存/恢复（Tabby: 窗口布局+所有 tab 重启恢复）
- ❌ 无重连按钮（所有工具: 断线后一键重连）
- ❌ 无会话搜索（MobaXterm: 实时过滤树形）

### 3.3 AI 能力差距

**现状**：
- 单模型 API，同步请求，无流式

**差距**：
- ❌ 无多模型切换（Cursor: GPT-4/Claude/Gemini）
- ❌ 无流式响应（所有 AI: 逐字显示）

### 3.4 运维工具差距

**现状**：无独立运维工具

**差距**：
- ❌ 无证书管理、时间同步、仓库配置、端口扫描、网络扫描

---

## 4. 接口契约定义

### 4.1 凭据继承体系

```csharp
namespace Gdterm.Core.Models
{
    /// <summary>
    /// 凭据继承规则，支持文件夹和连接级别
    /// </summary>
    public class CredentialInheritance
    {
        /// <summary>父级路径（文件夹 GroupPath）</summary>
        public string ParentPath { get; set; }

        /// <summary>是否继承用户名</summary>
        public bool InheritUsername { get; set; }

        /// <summary>是否继承密码（KeePass 引用）</summary>
        public bool InheritPassword { get; set; }

        /// <summary>是否继承 SSH 密钥</summary>
        public bool InheritSshKey { get; set; }

        /// <summary>是否继承隧道配置</summary>
        public bool InheritTunnel { get; set; }

        /// <summary>是否继承连接选项</summary>
        public bool InheritOptions { get; set; }
    }

    /// <summary>
    /// 连接模板，可复用的连接配置预设
    /// </summary>
    public class ConnectionTemplate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public ProtocolType Protocol { get; set; }
        public string DefaultUsername { get; set; }
        public int? DefaultPort { get; set; }
        public TunnelConfig DefaultTunnel { get; set; }
        public RdpOptions DefaultRdpOptions { get; set; }
        public Dictionary<string, string> CustomFields { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// 密码健康报告
    /// </summary>
    public class PasswordHealthReport
    {
        public int TotalEntries { get; set; }
        public List<WeakPasswordEntry> WeakPasswords { get; set; }
        public List<DuplicatePasswordGroup> DuplicatePasswords { get; set; }
        public List<ExpiredPasswordEntry> ExpiredPasswords { get; set; }
        public List<ReusedPasswordEntry> ReusedPasswords { get; set; }
        public double OverallScore { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    public class WeakPasswordEntry
    {
        public string EntryId { get; set; }
        public string Title { get; set; }
        public List<string> Violations { get; set; }
    }

    public class DuplicatePasswordGroup
    {
        public string PasswordHash { get; set; }
        public List<string> EntryIds { get; set; }
        public List<string> Titles { get; set; }
    }

    public class ExpiredPasswordEntry
    {
        public string EntryId { get; set; }
        public string Title { get; set; }
        public DateTime LastChanged { get; set; }
        public int DaysExpired { get; set; }
    }

    public class ReusedPasswordEntry
    {
        public string EntryId { get; set; }
        public string Title { get; set; }
        public int ReuseCount { get; set; }
    }
}
```

### 4.2 会话持久化

```csharp
namespace Gdterm.Core.Models
{
    /// <summary>
    /// 会话状态快照
    /// </summary>
    public class SessionSnapshot
    {
        public string Id { get; set; }
        public string ConnectionId { get; set; }
        public string Hostname { get; set; }
        public ProtocolType Protocol { get; set; }
        public bool IsActive { get; set; }
        public int TabIndex { get; set; }
        public DateTime SavedAt { get; set; }
        public List<string> RecentCommands { get; set; }
        public string LastDirectory { get; set; }
    }

    /// <summary>
    /// 窗口布局快照
    /// </summary>
    public class WindowLayout
    {
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public int SplitterDistance { get; set; }
        public ViewMode ViewMode { get; set; }
        public List<SessionSnapshot> OpenSessions { get; set; }
        public int ActiveTabIndex { get; set; }
        public DateTime SavedAt { get; set; }
    }
}
```

### 4.3 运维工具框架

```csharp
namespace Gdterm.Tools.Core
{
    /// <summary>
    /// 工具模块接口
    /// </summary>
    public interface IToolModule
    {
        string ToolId { get; }
        string DisplayName { get; }
        string Description { get; }
        string Category { get; }
        Panel CreatePanel();
        void LoadConfig(string configPath);
        void SaveConfig(string configPath);
    }

    /// <summary>
    /// 远程工具接口（需要 SSH 会话）
    /// </summary>
    public interface IRemoteToolModule : IToolModule
    {
        void SetSshSession(SshClient client);
        void ClearSshSession();
        bool HasSession { get; }
    }

    /// <summary>
    /// 工具配置基类
    /// </summary>
    public abstract class ToolConfigBase
    {
        public string ConfigFilePath { get; set; }
        public abstract void LoadFromFile(string path);
        public abstract void SaveToFile(string path);
        public abstract void ResetDefaults();
    }

    /// <summary>
    /// 远程命令执行结果
    /// </summary>
    public class RemoteCommandResult
    {
        public string Command { get; set; }
        public string Stdout { get; set; }
        public string Stderr { get; set; }
        public int ExitCode { get; set; }
        public bool IsSuccess => ExitCode == 0;
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// 工具注册表
    /// </summary>
    public class ToolRegistry
    {
        public void Register(IToolModule tool);
        public IToolModule GetTool(string toolId);
        public IEnumerable<IToolModule> GetAllTools();
        public IEnumerable<IToolModule> GetToolsByCategory(string category);
        public void LoadAllConfigs(string configDir);
        public void SaveAllConfigs(string configDir);
    }
}
```

---

## 5. 子特性清单

### Layer 0: 密码安全分析

#### 5.1 password-analyzer
**目标**：密码健康审计，检测弱/重复/过期密码
**依赖**：无（仅依赖 KeePassLib）
**接口**：
- `IPasswordAnalyzer.Analyze(PwDatabase db) → PasswordHealthReport`
- 检测维度：弱密码（复用现有 PasswordStrengthValidator）、重复密码（hash 分组）、过期密码（可配置策略，默认 90 天）、密码长度分布

**验收标准**：
1. `Analyze()` 对 100 条记录返回完整报告
2. 重复密码检测基于 SHA256 hash 比对，分组展示
3. 过期策略可配置（默认 90 天，可设 30/60/180/365）
4. 总体评分 0-100，加权计算

**排除范围**：在线密码泄露查询（HaveIBeenPwned）、密码自动修复

---

### Layer 1: 凭据/会话/命令/工具基础

#### 5.2 credential-inheritance
**目标**：连接从文件夹/父级继承凭据和配置
**依赖**：password-analyzer（使用相同的数据模型）
**接口**：
- `ConnectionConfig.CredentialInheritance` 属性
- `IConnectionStore.ResolveCredential(ConnectionConfig config) → CredentialPayload`
- 继承链：连接自身 → 所在文件夹 → 祖先文件夹 → 全局默认
- 任一级别设置 `InheritXxx = false` 则断开继承

**验收标准**：
1. 文件夹级凭据自动传播到子连接
2. 连接级覆盖优先于继承
3. UI 文件夹属性页可配置继承选项
4. 继承解析链深度无限制

**排除范围**：LDAP/AD 凭据源、SCIM 协议

#### 5.3 session-persistence
**目标**：关闭时保存窗口布局和打开的会话，重启恢复
**依赖**：无
**接口**：
- `ISessionStore.SaveLayout(WindowLayout layout)`
- `ISessionStore.LoadLayout() → WindowLayout`
- 文件：`data/window-layout.json`

**验收标准**：
1. 保存窗口位置、大小、分割条位置、视图模式
2. 保存所有打开的 tab（连接 ID + 活跃状态）
3. 重启后自动恢复布局（可配置开关）
4. 连接丢失时显示提示而非崩溃

**排除范围**：终端内容缓存、跨设备同步

#### 5.4 quick-commands
**目标**：可自定义的快捷命令面板
**依赖**：无
**接口**：
- `QuickCommand` 模型（Name/Command/Category/Shortcut/IsGlobal）
- `IQuickCommandStore` CRUD + 按分类查询
- 文件：`data/config/quick-commands.json`

**验收标准**：
1. 支持分类管理命令（系统运维/网络/自定义）
2. 每个命令可绑定快捷键
3. 命令可发送到当前终端或多通道选中的会话
4. 支持变量替换（{host}、{user}、{date}）

**排除范围**：命令录制回放、脚本引擎

#### 5.5 tools-core
**目标**：运维工具基础框架
**依赖**：无
**接口**：见 4.3
**验收标准**：
1. `ToolRegistry` 注册/查询/加载配置
2. `ToolConfigBase` JSON 文件读写
3. `IToolModule` 面板创建
4. `IRemoteToolModule` SSH 会话注入

**排除范围**：插件动态加载、热插拔

---

### Layer 2: 核心增强（并行）

#### 5.6 reconnect-button
**目标**：断线后一键重连
**依赖**：session-persistence
**验收标准**：
1. 断线后 tab 标题变红，显示重连按钮
2. 重连使用原有配置和凭据
3. 重连失败显示错误，可再次重试

#### 5.7 reauth-sensitive-ops
**目标**：敏感操作二次验证
**依赖**：password-analyzer
**接口**：
- `ISecurityManager.AuthenticateSensitive() → bool`
- 触发场景：删除连接、导出配置、查看密码、修改安全设置、批量操作

**验收标准**：
1. 触发时弹出密码确认对话框
2. 验证后 5 分钟内免重复验证（可配置）
3. 失败 3 次锁定 5 分钟
4. 所有敏感操作尝试记入审计日志

#### 5.8 connection-templates
**目标**：预置连接模板，快速创建标准化连接
**依赖**：无
**接口**：
- `IConnectionTemplateStore` CRUD + 按分类查询
- 预置模板：CentOS SSH、Ubuntu SSH、Windows RDP、Cisco 串口、网络设备

**验收标准**：
1. 内置 5+ 常用模板
2. 用户可创建/编辑/删除自定义模板
3. 从模板创建连接时预填所有字段
4. 模板支持导入/导出

#### 5.9 ai-multi-model
**目标**：支持多个 AI 模型提供商切换
**依赖**：无
**接口**：
- `AiModelProvider`（Id/Name/ApiEndpoint/DefaultModel/RequiresApiKey）
- `IAiAssistantService.SetProvider(AiModelProvider provider)`
- 内置：OpenAI、DeepSeek、Ollama（本地）

**验收标准**：
1. 支持至少 3 个提供商（OpenAI/DeepSeek/Ollama）
2. 每个提供商独立 API Key 配置
3. 运行时切换模型无需重启
4. 本地 Ollama 支持免 API Key

#### 5.10 tools-cert-installer
**目标**：本地+远程证书安装
**依赖**：tools-core
**接口**：
- `InstallLocalCert(string certPath, CertStore store)`
- `InstallRemoteCert(SshClient client, string certPath, string destPath)`
- 支持格式：PEM/DER/PFX/P12

**验收标准**：
1. 本地：certutil 导入 Windows 证书存储
2. 远程：SCP 推送 + update-ca-certificates/update-ca-trust
3. 自动检测系统类型（Debian/RHEL/SUSE）选择命令
4. 安装后验证证书链

#### 5.11 tools-time-sync
**目标**：本地+远程时间同步
**依赖**：tools-core
**接口**：
- `SyncLocalTime(string ntpServer)`
- `SyncRemoteTime(SshClient client, string ntpServer)`

**验收标准**：
1. 本地：w32tm /resync
2. 远程：ntpd/chrony/timedatectl 自动检测
3. 同步前后时间差显示
4. 可配置 NTP 服务器列表

#### 5.12 tools-repo-config
**目标**：远程软件仓库配置
**依赖**：tools-core
**接口**：
- `ConfigureYumRepo(SshClient client, RepoConfig config)`
- `ConfigureAptSource(SshClient client, string sourceLine)`
- `ConfigureZypperRepo(SshClient client, RepoConfig config)`

**验收标准**：
1. 支持 yum/dnf、apt、zypper 三种包管理器
2. 自动备份原有配置
3. 支持添加/删除/列出仓库
4. 原地更新后自动 refresh

#### 5.13 tools-port-scanner
**目标**：异步 TCP 端口扫描
**依赖**：tools-core
**接口**：
- `IPortScanner.ScanAsync(string host, int[] ports, ScanOptions options) → ScanResult`

**验收标准**：
1. 异步 TcpClient Connect 扫描
2. 可配置超时（默认 2000ms）和并发数（默认 100）
3. 内置常用端口预设（Web/数据库/常用服务）
4. 结果实时流式更新到 UI

#### 5.14 tools-net-scanner
**目标**：网络扫描工具套件（6 个子工具）
**依赖**：tools-core
**接口**：
- `IPScanner.ScanAsync(IPScanOptions) → IPScanResult`
- `IDnsLookup.Lookup(string host) → DnsResult`
- `ITraceroute.TraceAsync(string host) → TracerouteResult`
- `IPingMonitor.StartMonitor(string host, PingOptions) → PingMonitorResult`
- `ISubnetCalculator.Calculate(string cidr) → SubnetInfo`
- `IWhoisLookup.Lookup(string domain) → WhoisResult`

**验收标准**：
1. IP 扫描：Ping 探测 + 端口扫描 + 主机名解析
2. DNS：A/AAAA/MX/TXT/NS/CNAME/SOA 记录查询
3. Traceroute：逐跳延迟 + 域名解析
4. Ping 监控：实时图表 + 统计（丢包率/平均延迟/抖动）
5. 子网计算器：CIDR → 网络地址/广播/可用主机数
6. WHOIS：域名注册信息查询

---

### Layer 3: 文件/命令/流式

#### 5.15 sftp-enhancements
**目标**：SFTP 文件预览和权限编辑
**依赖**：无
**接口**：
- `ISftpService.ReadFilePreviewAsync(string remotePath, int maxBytes) → FilePreview`
- `ISftpService.SetPermissionsAsync(string remotePath, UnixFilePermissions permissions)`
- `UnixFilePermissions`（Owner/Group/Other 的 R/W/X + SetUid/SetGid/Sticky）

**验收标准**：
1. 文本文件预览（前 100 行，检测编码）
2. 图片文件缩略图预览
3. 权限以 rwxrwxrwx 和八进制双格式显示
4. 权限修改支持八进制和符号模式

#### 5.16 macro-recording
**目标**：终端宏录制和回放
**依赖**：无
**接口**：
- `IMacroRecorder.StartRecording(string name)`
- `IMacroRecorder.StopRecording() → Macro`
- `IMacroRecorder.PlayMacro(Macro macro, PlaybackOptions options)`
- `Macro`（Name/Steps/DelayBetweenSteps/CreatedAt）

**验收标准**：
1. 录制键盘输入和延迟
2. 回放可调整速度（0.5x/1x/2x/5x）
3. 宏持久化到 `data/macros/` 目录
4. 宏可绑定快捷键

#### 5.17 ai-streaming
**目标**：AI 响应流式显示
**依赖**：ai-multi-model
**接口**：
- `IAiAssistantService.SendMessageStreamAsync(...)` → `IAsyncEnumerable<string>`

**验收标准**：
1. 逐 token 流式显示
2. 支持中断（Stop 按钮）
3. 流式过程中的命令提取实时高亮
4. 回退到同步模式（流式失败时）

#### 5.18 command-templates
**目标**：跨会话命令模板库
**依赖**：quick-commands
**接口**：
- `ICommandTemplateStore` CRUD + 按分类/标签查询
- 模板变量：`{host}`、`{user}`、`{date}`、`{prompt}`、`{env:VAR}`

**验收标准**：
1. 模板支持变量替换（运行时解析）
2. 按标签分类
3. 模板可导出/导入
4. 模板可从命令历史一键创建

---

### Layer 4: 终端/多通道/UI

#### 5.19 terminal-enhancements
**目标**：透明窗口 + 终端改进
**依赖**：无
**接口**：
- `TerminalControl.SetOpacity(double opacity)`（0.3-1.0）
- `TerminalControl.SetBackgroundImage(Image img)`

**验收标准**：
1. 窗口透明度可调（30%-100%）
2. 透明度变化实时生效
3. 可配置快捷键切换透明/不透明

#### 5.20 multi-channel-enhancements
**目标**：多通道增强（同步录制、会话标签）
**依赖**：macro-recording
**接口**：
- `MultiChannelManager.StartSyncRecording(IEnumerable<string> sessionIds)`
- `MultiChannelManager.StopSyncRecording() → SyncRecording`
- `SyncRecording`（Sessions/Commands/Timeline/CreatedAt）

**验收标准**：
1. 同步录制多会话的输入和输出
2. 录制结果按时间线回放
3. 支持导出为文本/HTML 报告

#### 5.21 tools-ui
**目标**：运维工具 UI 集成
**依赖**：所有 tools-* 特性
**接口**：
- `ToolPanel` 基类（标题/状态栏/结果区域）
- `ToolCategoryMenu` 工具分类菜单

**验收标准**：
1. 工具 → 工具箱菜单分类显示
2. 每个工具独立 tab 打开
3. 工具配置对话框
4. 执行结果支持复制

---

## 6. 依赖关系图

```
Layer 0:
  password-analyzer ─────────────────────┐
                                         │
Layer 1:                                 ▼
  credential-inheritance ──────────► reauth-sensitive-ops
  session-persistence ─────────────► reconnect-button
  quick-commands ───────────────────► command-templates
  tools-core ─────────────────────┬► tools-cert-installer
                                  ├► tools-time-sync
                                  ├► tools-repo-config
                                  ├► tools-port-scanner
                                  └► tools-net-scanner

Layer 2:
  ai-multi-model ─────────────────► ai-streaming
  macro-recording ─────────────────► multi-channel-enhancements

Layer 3:
  sftp-enhancements (独立)
  terminal-enhancements (独立)

Layer 4:
  tools-ui (依赖所有 tools-* 完成)
```

---

## 7. 实施建议

### 优先级排序
1. **P0 必做**：password-analyzer、credential-inheritance、reauth-sensitive-ops、tools-core
2. **P1 应做**：session-persistence、quick-commands、connection-templates、ai-multi-model、tools-*、sftp-enhancements、command-templates、tools-ui
3. **P2 可做**：terminal-enhancements、macro-recording、multi-channel-enhancements

### 预估总工作量
- Layer 0：~1 周
- Layer 1：~2 周
- Layer 2：~3 周
- Layer 3：~2 周
- Layer 4：~1 周
- **总计：~9 周**

---

## 8. 排除范围

- 插件动态加载系统
- CVE 漏洞检查
- SNMP/LLDP/CDP 协议
- X11 转发
- 导出功能（v1）
- 在线密码泄露查询（HaveIBeenPwned）
- LDAP/AD 凭据源
- 终端内容缓存
- 跨设备同步
- 脚本引擎

---

## 9. 策略：里程碑与节奏

### 里程碑

| M0 | 2026-07-31 | 密码安全体系（password-analyzer + credential-inheritance + reauth） |
|----|-----------|------------------------------------------------------------------|
| M1 | 2026-08-07 | 会话增强（session-persistence + reconnect + quick-commands + templates） |
| M2 | 2026-08-14 | AI 增强（multi-model + streaming） |
| M3 | 2026-08-28 | 运维工具箱（tools-core + 5 个工具模块 + tools-ui） |
| M4 | 2026-09-04 | 终端增强（sftp-enhancements + terminal + macro + multi-channel） |

### 迭代节奏
- 每周一个迭代
- 每个迭代结束有内部演示
- M0 最重要（安全基础），优先投入

---

## 10. 验证矩阵

| 特性 | 验证命令 | 期望结果 |
|------|---------|---------|
| password-analyzer | `Analyze()` 100 条记录 | 报告含弱/重复/过期分组 |
| credential-inheritance | 创建文件夹级凭据，子连接继承 | 子连接自动获得凭据 |
| session-persistence | 打开 5 个 tab，关闭重启 | 5 个 tab 自动恢复 |
| reconnect-button | 断开网络，点击重连 | 自动重建连接 |
| reauth-sensitive-ops | 删除连接 | 弹出密码确认 |
| connection-templates | 从 CentOS 模板创建 | 预填 SSH/22/root |
| quick-commands | 添加命令并执行 | 发送到当前终端 |
| ai-multi-model | 切换到 Ollama | 使用本地模型 |
| ai-streaming | 发送问题 | 逐字显示响应 |
| tools-cert-installer | 安装 PEM 证书 | certutil 成功 |
| tools-time-sync | 同步 NTP 时间 | 时间差 < 1s |
| tools-repo-config | 添加 yum repo | /etc/yum.repos.d/ 更新 |
| tools-port-scanner | 扫描 22,80,443 | 返回开放/关闭状态 |
| tools-net-scanner | IP 扫描 192.168.1.0/24 | 返回在线主机列表 |
| sftp-enhancements | 预览文本文件 | 显示前 100 行 |
| macro-recording | 录制 + 回放 | 步骤精确重现 |
| command-templates | 使用模板发送 | 变量被替换 |
| terminal-enhancements | 设置透明度 50% | 窗口半透明 |
| tools-ui | 打开工具箱菜单 | 分类显示所有工具 |
