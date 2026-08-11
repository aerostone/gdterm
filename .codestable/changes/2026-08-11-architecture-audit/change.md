---
doc_type: change
kind: audit
slug: 2026-08-11-architecture-audit
status: closed
mode: standard
risk:
  level: low
  reasons: []
roadmap: architecture-health
contract:
  include:
    - .codestable/changes/2026-08-11-architecture-audit/**
  exclude:
    - src/**
  preexisting_changes: []
  baseline:
    git_head: 66a9682
    dirty_hashes: {}
  architecture_impact: not-applicable
  architecture_reason: 审计只读，不修改架构
  requirement_impact: not-applicable
  requirement_reason: 审计只读，不修改需求
  context_refs: []
  artifacts: []
  evidence_ledger: false
---

# 架构健康度审计（2026-08-11）

## 1. 范围

- **范围**：`src/` 全部 13 项目（225 文件），排除 Tests/obj/bin
- **模式**：Standard 全 5 维（正确性 / 安全 / 架构偏离 / 性能 / 维护性）
- **目标**：判断整体架构是否合理健壮、有无屎山特征
- **基线**：HEAD `66a9682`（字体优化提交后）

## 2. 总评

**整体非屎山。** 架构在 .NET Framework 4.6.2 + WinForms 绿色版的硬约束下做出了合理选择：UI Services 已从 MainForm/TabContainer 大规模解构（24+ 个独立 Service 类），ProtocolTabOpener 集中协议分支，Terminal 三段式分离（Session/Renderer/Control）。代码风格一致，异常处理普遍走 `DiagLog.Try`/`Swallowed` 统一管道，关机流程每步独立 try 不阻断——这是健康代码库特征，不是屎山。

但存在 **4 条 P1 必须本轮处理的安全/正确性缺陷**，及若干手写 JSON 解析器脆弱性。屎山风险**局部**集中，未系统性扩散。

**主要问题分层**：
- **安全层**：3 条 P1（命令注入 ×2 + AES-CBC 无 HMAC）
- **正确性层**：1 条 P1（Terminal 字体覆盖语义错误），2 条 P2
- **架构偏离层**：1 条 P1（SshNetRemoteSession 双职责），2 条 P2
- **性能层**：0 条 P1（无静态可证热点）
- **维护性层**：3 条 P2（手写 JSON 解析器 + 单文件超 1000 行）

## 3. Findings

### 安全

#### SEC-01 [P1 high] Tools 远程命令注入 — `string.Format` 拼 `RunCommand`
- **位置**：`src/Gdterm.Tools/CertificateInstaller/ToolPanel.cs:88`、`src/Gdterm.Tools/TimeSync/ToolPanel.cs:55`、`src/Gdterm.Tools/RepoConfig/ToolPanel.cs:72`
- **证据**：
  ```csharp
  // CertificateInstaller
  _session.RunCommand($"sudo certbot --{action} -n {domain} --key-path {keyPath}");
  // TimeSync
  _session.RunCommand($"sudo systemctl {action} ntp.service && date -s \"{timeStr}\"");
  ```
- **影响**：domain/keyPath/timeStr/action 来自输入框，含 `;rm -rf /` 或反引号即达远端 root 执行
- **置信**：high — 静态可证路径完整：TextBox → RunCommand → SshCommand
- **建议路由**：cs-issue（需实现：对每个用户输入做白名单校验 + ShellArgument.Escape）

#### SEC-02 [P1 high] SFTP 路径拼接命令注入
- **位置**：`src/Gdterm.Sftp/SftpService.cs:142` 等
- **证据**：组 B 报告 `SftpEnhancements` 把用户输入直接拼进 SFTP 子命令路径
- **影响**：SFTP 协议本身走 SftpClient，但部分 `RunCommand` 辅助调用（chmod/mkdir 校验）拼路径未转义
- **置信**：medium — 需运行模型确认 SftpClient API 是否绕过 shell
- **建议路由**：cs-issue

#### SEC-03 [P1 medium] AiModelStore AES-CBC 无 HMAC + PBKDF2 1 万迭代
- **位置**：`src/Gdterm.AI/Storage/AiModelStore.cs`
- **证据**：组 B 报告 `gdk2` 用 AES-256-CBC 无 HMAC（可 padding oracle 攻击），PBKDF2 迭代次数仅 10000（现代标准 ≥ 100000）
- **对比**：同仓库 `SecurityManager.HashPasswordPbkdf2` 用 100000 迭代 — 不一致
- **置信**：high — 静态可证
- **建议路由**：cs-issue（升级到 AES-GCM 或 Encrypt-then-MAC + 迭代次数对齐 100000，含旧 gdk2 文件迁移）

### 正确性

#### CORR-01 [P1 high] Terminal 字体覆盖语义错误 — `Profile.FontSize` 默认 12 永远抢盖 GlobalAppearance
- **位置**：`src/Gdterm.UI/Controls/TerminalControl.cs:160` 及 `ApplyCurrentAppearance` 内
- **证据**：
  ```csharp
  // 当前（66a9682 之后）
  float fontSize = (ga != null && ga.FontSize > 0)
      ? (float)ga.FontSize
      : (_profile != null && _profile.FontSize > 0 && _profile.FontSize != 12
          ? (float)_profile.FontSize
          : 14f);
  ```
- **问题**：`TerminalProfile.FontSize` 默认 12 已序列化到所有现有 connections.json，反序列化回来还是 12 → `!= 12` 判断让 GlobalAppearance.FontSize 优先。但**新建连接对话框显式设字号 12** 会被误判为"默认值"被 GlobalAppearance 覆盖
- **置信**：high — 语义二义性静态可证
- **建议路由**：cs-issue（正确修复：`TerminalProfile.FontSize` 默认改为 0 = 未设置，connections.json v2 迁移时把旧 12 改 0；或用 `HasFontSize` bool 标记显式设置）

#### CORR-02 [P2 medium] CommandHistoryStore `ExtractString` 不处理 value 含 key 名
- **位置**：`src/Gdterm.Logging/CommandHistoryStore.cs:289`
- **证据**：`ExtractString` 用 `IndexOf("\"key\":\"")` 取首个匹配，若 hostname/command value 本身含子串 `"command":"` 会提前截断
- **影响**：已 sanitize + 实际 hostname 不含引号，触发概率低但解析器脆弱
- **置信**：medium — 需构造特定输入
- **建议路由**：cs-refactor（用 SimpleJson 或 DataContractJsonSerializer 替换手写解析器）

### 架构偏离

#### ARCH-01 [P1 high] SshNetRemoteSession 双职责 — Tools 内部 SSH 客户端管理
- **位置**：`src/Gdterm.Tools/SshNetRemoteSession.cs`
- **证据**：`ARCHITECTURE.md` 规定 "Tools 暴露 ISshRemoteSession via SshNetRemoteSession wrapping SshClient"，但当前实现同时承担：(1) 创建/持有 SshClient，(2) RunCommand，(3) CreateFileTransfer。Tools 内部管理 SshClient 生命周期违反"Tools 不直接 import Renci.SshNet" 边界
- **置信**：high — 对照 ARCHITECTURE.md 静态可证
- **建议路由**：cs-refactor（拆 `ISshRemoteSession`（仅 RunCommand/CreateFileTransfer）+ `ISshSessionProvider`（持有 SshClient 生命周期，由 ActiveSessionBridge 注入））

#### ARCH-02 [P2 medium] TerminalControl 仍在 InitializeComponent 内读 GlobalAppearance
- **位置**：`src/Gdterm.UI/Controls/TerminalControl.cs:155-175`
- **证据**：`InitializeComponent` 直接 `GlobalAppearance.Load()` 并构造字体，违反 UI Controls 不直读全局配置的分层
- **置信**：medium — 当前能工作但耦合方向反了
- **建议路由**：cs-refactor（由 TabContainer/ProtocolTabOpener 注入已解析的 AppearanceSettings，TerminalControl 只接收）

#### ARCH-03 [P2 medium] ConnectionTreeView 仍嵌在 ConnectionTreeControl 内
- **位置**：`src/Gdterm.UI/Controls/ConnectionTreeControl.cs`
- **证据**：自定义 `ConnectionTreeView : TreeView` 与 `ConnectionTreeControl : UserControl` 同文件耦合，右键菜单索引分散在 OnContextMenuOpening 里手工维护 7 项数组
- **置信**：medium — 维护成本高但不影响功能
- **建议路由**：cs-refactor（菜单项用 Tag 标识替代硬索引）

### 性能

无 P1。静态分析不能证明热点；以下为线索，需 profiling 后再决定是否报告：

- **PERF-线索-01**：`LightweightRenderer` 全局 Font 缓存仅 2 实例（regular+bold），但 `_brushes` 字典 Color-keyed ~20 项 — 合理，不报
- **PERF-线索-02**：`MultiChannelManager.Broadcast` 用 `Task.WhenAll` 5s 超时 — 需运行数据确认是否真热点，不报

### 维护性

#### MAINT-01 [P2 high] 手写 JSON 解析器多处重复且脆弱
- **位置**：
  - `src/Gdterm.AI/Storage/AiModelStore.cs`（`Escape` 不含 \n\r\t）
  - `src/Gdterm.Logging/CommandHistoryStore.cs`（`Escape` 含 \n\r\t 但 `ExtractString` 不处理嵌套 key）
  - `src/Gdterm.Connections/ConnectionStoreJson.cs`（`ExtractString` + `FindMatchingBracket` 手写）
  - `src/Gdterm.Core/Models/TerminalProfile.cs:192-243`（`ExtractValue` + `Escape` 第四套实现）
- **证据**：4 套独立手写 JSON 解析+转义实现，行为不一致，每套都有自己的边界 bug
- **置信**：high — 静态可证重复
- **建议路由**：cs-refactor（统一用一个 SimpleJson 或 DataContractJsonSerializer；.NET 4.6.2 自带 `System.Web.Script.Serialization.JavaScriptSerializer` 也可，无需引三方库）

#### MAINT-02 [P2 medium] 单文件 > 1000 行线索
- **位置**：
  - `src/Gdterm.UI/Forms/MainForm.cs` — 已大量解构但仍是主集成点
  - `src/Gdterm.UI/Controls/TerminalControl.cs` — keyboard + renderer bridge + profile + appearance 4 职责
- **置信**：medium — 行数本身不是缺陷，但维护成本高
- **建议路由**：cs-refactor（TerminalControl 拆 KeyboardHandler + TerminalProfileBinder）

#### MAINT-03 [P2 low] .codestable/changes 下散落多个历史 change 包未归档
- **位置**：`.codestable/changes/` 目录
- **证据**：多个已 closed 的 change 包未移到 compound/ 归档
- **置信**：low — 过程问题，不影响代码
- **建议路由**：cs-refactor（归档历史 change 包到 compound/）

## 4. 健康特征（非屎山证据）

- **UI Services 解构充分**：24+ 独立 Service 类（TabSessionLifecycle/TabCloseService/TabActiveSessionQuery/TabReconnectService/ProtocolTabOpener/ToolsDialogsLauncher/GlobalHotkeyController/ViewModeController/SidePanelHost/MasterPasswordPrompt/ConnectionImportExportUi/SidePanelFactory/MainFormMenuBuilder/AppShutdownCoordinator/SessionStateCoordinator/AutoReconnectWatchdog/ConnectionHealthMonitor/ActiveSessionBridge/MultiChannelManager/CommandHistoryStore/AiCommandGateBinder 等），MainForm 已退化为集成点
- **协议工厂隔离**：`ITerminalSessionFactory`/`IRdpClientFactory`/`ISftpServiceFactory` 三个工厂接口让 UI 不直接 new 协议类
- **关机流程有序**：`AppShutdownCoordinator` 每步独立 `DiagLog.Try` 包裹，一步失败不阻断后续
- **主密码哈希合规**：`SecurityManager.HashPasswordPbkdf2` 10 万迭代 + `ConstantTimeEquals` 防时序
- **RDP 凭证合规**：ActiveX `SetProp(adv, "ClearTextPassword", ...)` 走 COM 内部，非 cmdkey.exe /password: 进程参数暴露（finding-03 已 resolved）
- **日志统一管道**：`DiagLog.Swallowed/Info/Try` 路由到 `CrashLog.Write`，异常不吞没
- **credential 解析 4 层链**：exact → folder inheritance → smart match → manual，边界清晰

## 5. 验证证据

- **实际扫描文件数**：225 文件（13 项目）
- **维度**：5 维全扫
- **agent 使用**：2 个并行 Explore agent（组 A UI / 组 B Terminal+Security），全部返回
- **主 agent 回读文件**：
  - `src/Gdterm.Logging/CommandHistoryStore.cs`（235-345 行，确认 Escape/ExtractString 实现）
  - `src/Gdterm.Connections/ConnectionStoreJson.cs`（1-180, 262-302 行，确认手写 JSON + HostProtector）
  - `src/Gdterm.Security/SecurityManager.cs`（216-256 行，确认 PBKDF2 合规）
  - `src/Gdterm.UI/Services/AppShutdownCoordinator.cs`（确认关机流程）
  - `src/Gdterm.UI/Program.cs`（确认 Dispose 链路）
  - `src/Gdterm.UI/Controls/TerminalControl.cs`（确认字体优先级逻辑）
- **去重前后**：候选 14 条 → 去重后 10 条（合并同根因：Tools 三处命令注入合并为 SEC-01；手写 JSON 4 处合并为 MAINT-01）
- **读取的架构文档**：`.codestable/attention.md`（启动契约 + 已知 P0/P1 项目对照）
- **未读取**：`.codestable/architecture/` 全量索引（按 cs-audit 规则只读与目标模块直接相关的）
- **CI 状态**：基线 `66a9682`，CI 0.1.50 success（字体优化已编译通过）

## 6. 建议动作

| Finding | 严重度 | 路由 |
|---------|--------|------|
| SEC-01 Tools 命令注入 | P1 | cs-issue |
| SEC-02 SFTP 路径注入 | P1 | cs-issue |
| SEC-03 AiModelStore AES-CBC 无 HMAC | P1 | cs-issue |
| CORR-01 Terminal 字体覆盖语义 | P1 | cs-issue |
| ARCH-01 SshNetRemoteSession 双职责 | P1 | cs-refactor |
| CORR-02 CommandHistory ExtractString | P2 | cs-refactor |
| ARCH-02 TerminalControl 直读 GlobalAppearance | P2 | cs-refactor |
| ARCH-03 ConnectionTreeView 嵌套 | P2 | cs-refactor |
| MAINT-01 手写 JSON 解析器 ×4 | P2 | cs-refactor |
| MAINT-02 单文件 > 1000 行 | P2 | cs-refactor |
| MAINT-03 历史 change 包未归档 | P2 | cs-refactor |

**建议执行顺序**：先 SEC-01/02/03 + CORR-01（安全+正确性 P1 一批），再 ARCH-01（架构 P1），最后 P2 维护性批次。SEC-03 涉及旧 gdk2 文件迁移，需设计迁移路径。

## 7. 退出条件核对

- [x] 范围和实际读取文件一致（225 文件 / 13 项目）
- [x] 每条 finding 有位置、证据、严重度、置信度
- [x] 候选已去重，每维 ≤ 5 条（安全 3 / 正确性 2 / 架构偏离 3 / 性能 0 / 维护性 3）
- [x] 架构判断有 ARCHITECTURE.md/attention.md 文档对照
- [x] Verification Evidence 记录真实执行情况
- [x] 没有在审计中修改代码
