# gdterm 架构总入口

> 状态：已回填（与 2026-07-25 代码现状对齐）  
> 创建日期：2026-07-24  
> 回填日期：2026-07-25  
> 依据：`src/` 12 个 csproj、`Program.cs` 组合根、近期 audit 修复

## 1. 项目简介

gdterm 是 **Windows 绿色便携** 运维客户端，目标环境含 **Win7 / Server 2008**。  
技术栈：**.NET Framework 4.6.2 + WinForms**，单文件夹部署，常驻内存目标约 **30–80MB**（纯 SSH 多标签场景）。

核心能力：

- SSH 终端（懒连接、GDI 轻量渲染、危险命令闸、快捷栏/多通道）
- 跳板隧道（SSH.NET，手动 hop 链）
- RDP（AxMsRdpClient8 / ActiveX，CredWrite 注入 TERMSRV 凭据）
- 串口 / 本地 shell
- SFTP 浏览
- KeePass 密码库（主密码与库密码合一）
- 审计日志 / 崩溃落盘 / 闲时锁定
- 内置运维工具箱（证书、时间同步、仓库、端口/网络扫描）

**不做**：插件 DLL 热加载、第三方 JSON 库、高 GPU 依赖渲染。

## 2. 核心概念 / 术语表

| 术语 | 含义 |
|------|------|
| ConnectionConfig | 连接配置 POCO（主机/协议/隧道/Metadata），无明文密码字段 |
| CredentialPayload | 运行时凭据（用户名/密码/SSH 私钥字节+口令），来自 KeePass 解析 |
| CredentialRefId | 连接到 KeePass 条目的引用；1 凭据可服务多主机 |
| Folder credential inheritance | `folder-credentials.json` 沿 GroupPath 向上解析 |
| TunnelManager | 按 `connectionId` 管理跳板会话；同 Id 复用活跃隧道 |
| TunnelEndpoint | 本地转发后的 `127.0.0.1:port` 端点 |
| ITerminalSession | SSH / Serial / Local 统一会话面（Connect / SendInput / OutputReceived） |
| Lazy connect | 标签创建时才真正 Connect；非活动标签 Pause 渲染 |
| LightweightRenderer | GDI+ 终端渲染，300 行缓冲、16ms 节流、Pause 停表 |
| DangerousCommandDetector | 可配置黑/白名单；确认次数随危险等级 |
| Local line buffer | 有 detector 时键入先本地缓冲，Enter 确认后再整行下发 |
| data/ | 便携数据根：connections、kdbx、config、logs 同目录可拷走 |
| 组合根 | `Gdterm.UI/Program.cs`：创建目录、构造服务、注入 MainForm |
| CrashLog | `data/logs/crash.jsonl`，全局未处理异常落盘 |

## 3. 子系统 / 模块索引

依赖方向：**只允许向下**。`Gdterm.Core` 零依赖；`Gdterm.UI` 可引用全部库，**库不得引用 UI**。

```
                    ┌─────────────┐
                    │  Gdterm.UI  │  WinForms 壳 / 组合消费
                    └──────┬──────┘
       ┌───────────┬───────┼───────┬───────────┬──────────┐
       ▼           ▼       ▼       ▼           ▼          ▼
  Connections  Terminal  Tunnel  Sftp/Rdp   KeePass   Tools/AI
  Logging      Security    │       │          │
       │           │       │       │          │
       └───────────┴───────┴───────┴──────────┘
                           │
                     Gdterm.Core
```

| 项目 | 职责 | 主要对外表面 | 依赖 |
|------|------|--------------|------|
| **Gdterm.Core** | 纯数据模型与枚举 | `ConnectionConfig`, `CredentialPayload`, `ProtocolType`, `TerminalProfile`, `HighlightRule`, … | 无 |
| **Gdterm.Connections** | 连接/书签/模板/高亮/键位/登录脚本 持久化 | `IConnectionStore`, `IBookmarkStore`, `*Store` JSON 手写 | Core |
| **Gdterm.Tunnel** | SSH 跳板与本地转发 | `TunnelManager`, `TunnelSession`, `PortForwardManager` | Core, SSH.NET |
| **Gdterm.Terminal** | 会话、渲染、多通道、健康、重连、宏 | `ITerminalSession`, `TerminalSessionFactory`, `SshConnectionInfoFactory`, `MultiChannelManager`, `AutoReconnectWatchdog`, `LightweightRenderer` | Core, Tunnel |
| **Gdterm.Sftp** | SFTP 传输与增强 | `ISftpService`, `SftpServiceFactory`, `SftpEnhancements` | Core, Tunnel |
| **Gdterm.Rdp** | RDP ActiveX 封装 | `IRdpClient`, `RdpClient`, `RdpOptions` | Core, Tunnel |
| **Gdterm.KeePass** | .kdbx、健康分析、RDP CredWrite | `IKeePassService`, `PasswordStrengthValidator` | Core, KeePassLib |
| **Gdterm.Logging** | 审计 JSONL、命令历史、脱敏 | `IAuditLogger`, `AuditLogger`, `CommandHistoryStore` | Core |
| **Gdterm.Security** | 主密码、闲锁、危险命令、密文扫描 | `ISecurityManager`, `DangerousCommandDetector`, `SecretScanner` | Core |
| **Gdterm.AI** | OpenAI 兼容对话/流式/多模型 | `IAiAssistantService`, `AiModelStore` | Core, Terminal |
| **Gdterm.Tools** | 内置运维工具模块 | `IToolModule`, `IRemoteToolModule`, `ToolRegistry`, 5 个 Modules | Core（远程面目前泄漏 `Renci.SshNet.SshClient`） |
| **Gdterm.UI** | 壳、菜单、标签、对话框、诊断 | `Program`, `MainForm`, `TabContainerControl`, `TerminalControl`, panels/forms | 全部库 |

### 3.1 组合根（`Program.cs`）

启动顺序（简化）：

1. 创建 `data/`、`data/config/`、`data/logs/`…
2. `CrashLog.Initialize` + 全局异常钩子
3. 主密码首次向导 / `SecurityManager`
4. `new` 各 Store / Manager / Detector / `ToolRegistry.Register(...)`
5. `MainForm(...)` 一次性注入
6. `FormClosed` / `ProcessExit`：存配置、Dispose watchdog/scanner、清理 RDP 凭据

**约定**：新服务优先在 `Program` 构造并注入；避免在控件里 `new` 长生命周期单例（`PortForwardManager` 等历史问题除外，见已知债）。

### 3.2 会话与标签生命周期

```
ConnectionTree 双击
    → TabContainerControl.OpenConnection
        → ResolveCredential（RefId → 文件夹继承 → smart match）
        → CreateSshTab / CreateRdpTab / CreateSerialTab
            → 懒连接：选中标签 ResumeRendering / PendingConnect
            → WireHealthAndReconnect（HealthMonitor + Watchdog）
    → CloseTab
        → Unwatch + HealthMonitor.Dispose
        → 若无其他同 connectionId 标签 → TunnelManager.CloseAsync
        → RDP：CleanupRdpCredential + RdpClient.Dispose
        → SessionClosed → MultiChannelManager.Unregister
```

### 3.3 凭据与安全边界

- **落盘**：密码只进 `gdterm.kdbx`；`connections.json` 仅 `CredentialRefId` / 元数据
- **内存**：解锁后 `SecurityManager` 持有主密码字符串（供 KeePass Unlock）
- **SSH**：`SshConnectionInfoFactory` 优先 `PrivateKeyConnectionInfo`，否则密码
- **RDP**：ActiveX `ClearTextPassword` + 可选 `CredWrite(TERMSRV/host)`（**禁止** cmdkey 命令行带 `/pass:`）
- **危险命令**：所有经 `TerminalControl.SendInput` / 多通道 / 批量 的入口应过 detector；键入路径本地行缓冲

### 3.4 数据布局（便携）

```
<app>/
  Gdterm.UI.exe
  data/
    connections.json
    gdterm.kdbx
    master-password.json          # hash+salt，非明文
    folder-credentials.json
    session-state.json
    quick-commands.json
    config/
      dangerous-commands.json
      keybindings.json
      highlights.json
      tools/*.json
      ai-models.json
    logs/
      audit-*.jsonl
      crash.jsonl
      commands/
      terminal/
```

## 4. 关键架构决定

| ID | 决定 | 理由 |
|----|------|------|
| D1 | .NET 4.6.2 + WinForms，非 .NET Core/WPF | Win7/2008；GDI 低配友好 |
| D2 | 手写 JSON，禁止 Newtonsoft/STJ | 绿色依赖面最小 |
| D3 | 库内置模块 `IToolModule`，非插件加载 | 无反射插件攻击面；Win7 简单 |
| D4 | 手动 hop 链，非自动 ProxyJump 魔法 | 运维显式可控 |
| D5 | 终端渲染可替换（`IRenderer`），UI 当前钉死 LightweightRenderer | 内存优先；专业库可后换 |
| D6 | 审计与崩溃双通道 | `IAuditLogger` 业务事件；`CrashLog` 保证早期/晚期也能写 |
| D7 | 隧道按 connectionId 复用 | 同连接 SSH+SFTP 不互相拆 hop |
| D8 | 主密码 = KeePass 密码 | 一次解锁会话 |

## 5. 已知约束 / 硬边界

### 硬约束

1. **目标框架**必须保持 net462 API 面（无 Span 热依赖、无 netstandard2.1-only）
2. **禁止**为便利引入外部 JSON/DI/MVVM 重框架
3. **敏感秘密**不得写入可同步的明文 config（API Key 现状仍有债，见下）
4. **Linux 开发机**无 dotnet SDK：`.csproj` 手改 Compile Include，**Windows 上 MSBuild 才是真相**
5. **空闲锁**超时硬顶 30 分钟
6. **SecretScanner** 默认 `EnableBackgroundScan=false`；不得默认全盘扫
7. **UI 不得**在库项目中反向引用

### 已知技术债（审计 2026-07-25，部分已修）

| 债 | 状态 | 说明 |
|----|------|------|
| `LogConnection` 签名与 `ProtocolType` 大小写 | **已修** | `ConnectionAction` + `SSH/RDP` |
| 危险命令旁路 / 先发后审 | **已修** | 本地行缓冲 + UI 入口收口 |
| SSH 私钥未用于 Connect | **已修** | `SshConnectionInfoFactory` |
| cmdkey 明文 `/pass:` | **已修** | `CredWrite` + ProcessExit 清理 |
| 关标签不关隧道 / 多通道不 Unregister | **已修** | 引用计数式 Close + SessionClosed |
| 无全局异常钩子 | **已修** | CrashLog + SecurityEvent |
| ARCHITECTURE 空骨架 | **已修（本文）** | |
| MainForm / TabContainer 上帝对象 | 未修 | 可维护性；建议 cs-refactor |
| PortForward / Toolbox 未注入活动 SshClient | 未修 | 菜单能开、执行空转 |
| `IRemoteToolModule.SetSshSession(SshClient)` 泄漏 SSH.NET | 未修 | 应改为会话抽象 |
| AI history 无上限、ApiKey 明文 JSON | 未修 | 内存 + 信息泄露 |
| 剪贴板密码无 TTL | 未修 | 安全 |

### 内存与磁盘门禁（运行时默认）

| 项 | 默认 |
|----|------|
| 终端渲染缓冲 | 300 行 |
| 会话输出缓冲 | ~500 行 |
| Health 历史 | 120，非活动 `IsPaused` |
| Auto-log | 默认关；10MB × 3 |
| Secret 后台扫 | 关 |
| 端口扫描并发 | 收紧（约 40） |
| 子网扫描并发 | 收紧（约 30） |

**会冲破 30–80MB 的操作**：多 RDP ActiveX、手动全盘 SecretScan、多侧栏 RichTextBox 同时开。

## 6. 改动导航（给后续 agent）

| 若要改… | 先读 |
|---------|------|
| 连接模型 / 协议枚举 | `Gdterm.Core` |
| 存盘格式 | `Gdterm.Connections/*Store*`（手写 JSON 解析） |
| SSH 登录方式 | `SshConnectionInfoFactory`（Terminal + Sftp 各一份，改时同步） |
| 隧道复用 / 关闭 | `TunnelManager`, `TabContainerControl.CloseTab` |
| 终端输入与危险命令 | `UI/Controls/TerminalControl` |
| 菜单与侧栏 | `MainForm` |
| 凭据解析链 | `TabContainerControl.ResolveCredential` |
| 审计事件 | `IAuditLogger` + 调用方；崩溃看 `Diagnostics/CrashLog` |
| 便携路径 | 只动 `Program.cs` 的 dataDir 约定 |

## 7. 相关产物

- 需求/路线图：`.codestable/roadmap/`
- 接线内存评审：`.codestable/reviews/p0-p2-wiring-memory-review.md`
- 运行时审计：`.codestable/audits/2026-07-25-wiring-runtime/`
- 启动必读碎片：`.codestable/attention.md`
