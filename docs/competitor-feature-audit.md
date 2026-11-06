# gdterm vs 主流终端/SSH 管理工具 逐项排查（2026-11）

> 对比对象：WindTerm 2.7 / MobaXterm / Xshell 7 / Tabby / FinalShell / Termius / SecureCRT。
> 方法：先用 as-search 拉取各家特性清单（Tavily，2025-2026 版本），再在代码里逐项核对**本项目到底有没有、有没有接通**，最后按「成本 × 价值」排序。
> 说明：`docs/nyaterm-feature-comparison.md` 是单向竞品（nyaterm）研究；本文补「通用终端管理工具」维度。

## 0. 一句话结论

gdterm 的功能密度已经**高于 Xshell/Tabby 的一般水平**，安全域（主密码 + KeePass + OTP + 密码健康 + 密文扫描 + 危险命令闸）是全场最强；真正的短板只剩三处**「连接前后的一公里」**：代理、目录联动、远端资源/通知——都不大，但都是竞品的招牌卖点。**不存在需要重写的大窟窿。**

## 1. 逐项排查矩阵

图例：✅ 已有且接通｜🟡 有但不完整/缺联动｜❌ 无｜🚫 明确不做（库/定位不支持）

### 1.1 协议与连接

| 项 | 竞品 | 本项目 | 证据 |
|---|---|---|---|
| SSH2 / Telnet / Serial / 本地 Shell | 全有 | ✅ | `TerminalSession.cs` / `TelnetSession.cs` / `SerialSession.cs` / `LocalTerminalSession.cs` |
| RDP | MobaXterm / (Termius 无) | ✅ 原生 | `Gdterm.Rdp` + FreeRDP 打包 |
| 跳板机 / 多跳 | MobaXterm、Xshell、Tabby、WindTerm 全有 | ✅ **链式已实现** | `Tunnel/TunnelManager.cs:98` `EstablishWithJumpChain`，`ConnectionConfig.JumpChain`，`CredentialResolver.cs:84` 逐跳解凭据 |
| 端口转发 L/R/D(SOCKS5) | 全有 | ✅ | `PortForwardPanel.cs:143` Local/Remote/Dynamic |
| **HTTP / SOCKS 代理** | MobaXterm、Xshell、Tabby、WindTerm 有 | ❌ **真缺口** | SSH.NET 2026 `ConnectionInfo.ProxyType/ProxyHost/ProxyPort` 反射确认存在，但全仓 `ProxyTypes` 0 命中 |
| X11 转发 | MobaXterm(内置 X)、WindTerm、Tabby | 🚫 | SSH.NET 无 x11 channel（导出类型里无 X11），自建通道成本≫收益 |
| SSH agent 转发 / Pageant | WindTerm、Xshell、Tabby | 🚫 | SSH.NET 无 agent 请求类型；已有 KeePass + 私钥文件覆盖同类需求 |
| GSSAPI/Kerberos | WindTerm、Xshell 企业版 | 🚫 | 库不支持 |
| VNC / XDMCP / FTP / Rlogin | MobaXterm 独有 | 🚫 | 与定位不符（Win7 终端/远程管理，非一体化远程工作台） |
| 连接复用（ControlMaster） | WindTerm | ❌ 低优先 | 同主机多标签各自建连；SSH.NET 支持多 channel，但收益主要在认证往返 |

### 1.2 认证与凭据（本项目最强项）

| 项 | 本项目 | 证据 |
|---|---|---|
| 密码 / 私钥 / 私钥口令 / 键盘交互(MFA) | ✅ | `SshConnectionInfoFactory.cs`，`ConnectionInfo.AuthenticationMethods` |
| 主密码 AES 加密存储 | ✅ | `gdo1:` 前缀，`CredentialPayload` |
| KeePass 集成 | ✅ | `Gdterm.KeePass` + `KeePassEntryPicker` |
| TOTP/HOTP 管理器 | ✅ | `OtpManagerForm` / `AddOtpForm`（竞品仅 nyaterm 2026-11 新加） |
| 密码健康扫描 / 明文密文扫描 | ✅ | `PasswordHealthForm` / `SecretScanPanel` |
| 危险命令闸 + 审批 | ✅ | `DangerousCommandDialog` / `DangerousCommandConfigForm`（Tabby/WindTerm 无） |

> 结论：这一域不用动。

### 1.3 会话管理

| 项 | 竞品 | 本项目 | 证据 |
|---|---|---|---|
| 会话树/分组/书签 | 全有 | ✅ | `ConnectionTreeControl` / `SessionBookmarksPanel` |
| 标签 + 分屏 | 全有 | ✅ | `TabContainerControl` / `SplitPaneControl` |
| 启动恢复上次窗口+标签 | Xshell/MobaXterm | ✅ | `SessionStateCoordinator` + `SessionStateStore`（`OpenTabs`/`ActiveTabIndex`） |
| 导入 Xshell/MobaXterm/WindTerm 会话 | 部分 | ✅ | `ConnectionImporterExporter` |
| 自动重连 / 心跳 / 健康度 | 部分 | ✅ | `AutoReconnectWatchdog` / `ConnectionHealthMonitor` / `HealthMonitorPanel` |
| 命令面板（模糊直达） | WindTerm、Tabby | ✅ | `CommandPaletteForm` |
| 快速命令栏 / 片段 | Xshell 招牌 | ✅ | `SnippetSearchPanel` / `QuickCommandEditorForm` / `CommandTemplatePanel` |
| 命令历史 | Xshell、SecureCRT | ✅ | `CommandHistoryPanel` |
| 宏录制 | SecureCRT、MobaXterm | ✅ | `MacroRecorder` / `MacroPanel` |
| 多机批量执行 / 同步输入 | Xshell、FinalShell、WindTerm | ✅ | `BatchCommandExecutor` / `MultiChannelPanel` / `BroadcastInput` |

### 1.4 终端保真（本轮刚补齐）

| 项 | 状态 | 证据 |
|---|---|---|
| 真彩 / 256 色 / 调色板 OSC 4/5/10-19 | ✅ | `VtStreamPreprocessor` 自吞 + 自查答 |
| 宽字符列宽（CJK/emoji） | ✅ | `WideText` PUA 占位 + `Strip`（本轮） |
| 图形：Sixel / Kitty / iTerm2 | ✅ | `GraphicsDecoder` / `KittyGraphicsParser` |
| CPR / DA / XTGETTCAP / DECRQM(私有) | ✅ | 本轮补齐 |
| 光标形状/焦点事件/鼠标模式 | ✅ | 本轮补齐 |
| 剪贴板 OSC 52（读写） | ✅ | `ClipboardSetRequested` |
| Zmodem | ✅ | 14 个文件命中 |
| tmux 兼容（TERM 贯通、重绘） | ✅ | 本轮补齐 |
| 超链接 OSC 8 | 🟡 吞掉不渲染（防串字，但也没做成可点击） | `VtStreamPreprocessor.cs:555` |
| **OSC 7 当前目录 / OSC 9·777·99 通知** | ❌ 不处理 | preprocessor 只接了 0/2/4/5/8/52/10-19；通知中心 UI 已有（`MainForm.cs:568`）却没桥 |
| **URL/路径 Ctrl+Click 打开** | ❌ | 只有「选中文本 → 右键在线动作」（`TerminalControl.cs:436`），没有点击识别 |
| **回滚行数上限** | 🟡 **硬编码 2000 行** | `VtTerminalEngine.cs:55,182` `if (value > 2000) value = 2000`；Xshell/Tabby 万行级、MobaXterm 可设无限 |

### 1.5 文件传输

| 项 | 状态 | 证据 |
|---|---|---|
| SFTP 双栏 / 权限 / 传输中心 | ✅ | `SftpDualPanePanel` / `SftpPermissionForm` / `TransferCenterPanel` |
| 断点续传（上传/下载手工 Seek 实现） | ✅ | `SftpService.UploadResumeAsync/DownloadResumeAsync` |
| 目录同步计划 | ✅ | `SftpSyncRunner` |
| **跟随终端当前目录** | ❌ **真缺口** | 全仓无 OSC 7 解析、无提示符取 cwd；`SftpDualPanePanel` 无 follow 逻辑。MobaXterm 招牌「follow terminal folder」，FinalShell/Xshell 同款 |
| FTP / SCP / 云存储 | 🚫 | 不做 |

### 1.6 监控与通知

| 项 | 竞品 | 本项目 | 证据 |
|---|---|---|---|
| 连接健康（延迟/在线时长/重连次数/曲线） | 少 | ✅ | `HealthMonitorPanel` |
| **远端主机资源（CPU/内存/磁盘/网络）** | FinalShell 招牌、MobaXterm 部分 | ❌ | 现有面板只画连接指标，不采远端 `/proc` |
| 站内通知中心 | Termius 有 | ✅ | `NotificationCenterPanel` |
| 终端触发的桌面通知（OSC 9/777/99） | iTerm2/WezTerm/tmux 生态 | ❌ | 见 1.4 |

### 1.7 自动化 / AI / 同步

| 项 | 状态 | 证据 |
|---|---|---|
| 登录脚本（步骤 + 等提示符） | ✅ | `LogonScriptEngine` + `TerminalReadyDetector`（已内置 bash/zsh/`[user@host dir]$` 正则） |
| 关键字高亮规则 | ✅ | `HighlightRulePanel` / `HighlightEngine` |
| **触发器（关键字 → 自动动作/命令）** | ❌ | Xshell「触发器」、SecureCRT trigger 同类；本仓 `Trigger` 0 命中（高亮引擎可直接复用） |
| AI 助手面板 | ✅ 差异化 | `AiChatPanel` / `Gdterm.AI` |
| 配置加密同步 / 多端 | ❌ | `docs/nyaterm-feature-comparison.md` B2 已列路线 |
| 会话录制回放（审计用） | 🟡 只有宏录制 | `MultiChannelRecorder` 可扩成 asciinema 式回放 |

### 1.8 体验与工程

| 项 | 状态 | 证据/说明 |
|---|---|---|
| 便携版 / 免安装 | ✅ | CI 产出 `gdterm-portable.zip` |
| 主题/字体/透明度/DPI | ✅ | `AppearanceSettingsForm` + 缩放约定文档 |
| 快捷键自定义 | ✅ | `KeyBindingPanel` |
| **多语言 i18n** | ❌ 仅中文 | 无 resx/Localization；WindTerm/MobaXterm/Xshell/Tabby 均多语言 |
| **更新检查** | ❌ | 无 `/releases` 查询 |
| 渲染性能 | 🟡 整屏重绘 | 已有 16ms 合并定时器（`CellGdiRenderer.ScheduleRedraw`），但每帧 `SnapshotVisible()` 全页复制 + 整窗 `Invalidate`，无脏格差分/快照池 |

## 2. 结论：按优先级的补齐清单

### P0（真缺口、竞品招牌、成本低）

1. **SFTP 跟随终端目录**
   抓手现成：`TerminalReadyDetector` 的提示符正则已能取 `~/path`，再加 OSC 7 优先；SFTP 面板订阅「cwd 变化」事件即可。
   验收：`cd /var/log` → 双栏右侧自动落到 `/var/log`；`tmux` 内切窗口也要跟（靠 OSC 7）。
2. **HTTP / SOCKS5 代理连接**
   SSH.NET 2026 原生字段，`SshConnectionInfoFactory.Create` 多传 4 个参数 + 「连接属性→代理」分节。企业内网刚需，代码量最小。
3. **终端通知桥：OSC 9 / 777 / 99 → 通知中心 + 托盘气泡**
   UI 已有（`NotificationCenterPanel`），只差 preprocessor 一个分支 + 一条事件。

### P1（体验落差明确）

4. **远端主机监控**（FinalShell 招牌）：一次 `cat /proc/stat /proc/meminfo /proc/net/dev` 采样 → 复用 `HealthMonitorPanel` 画图；5s 间隔、可关。
5. **回滚行数可配**：`2000` 硬顶放开到 1k–100k（默认 10k），并给出内存提示；顺手把 `MaximumHistoryLines` 的 clamp 从两处收敛到一处。
6. **URL / 路径 Ctrl+Click**：终端的 `TryHitTest` 已有，加一层「当前行 token 化 + hover 下划线 + Ctrl 点击 `Process.Start`」。
7. **触发器**：`HighlightEngine` 的匹配结果上加 action（发命令 / 通知 / 切标签），Xshell 式。

### P2（性能/长尾）

8. **渲染脏格差分**：`SnapshotVisible` 全页复制 → dirty row 位图 + 快照池；大 `cat` 场景 CPU 大幅下降（可对齐 WindTerm 那 20-30%）。
9. **更新检查**（GitHub releases，Win7 友好，静默失败）。
10. **i18n**（中/英）：工程量大，等外部用户进来再做。

### 明确不做（写进文档，避免反复讨论）

X11 转发、SSH agent 转发、GSSAPI、VNC/XDMCP/FTP/Rlogin、移动端/跨平台、LSP 插件生态、连接复用。

## 3. 需要澄清的两个「看似缺口」

- **跳板机**：已实现（`TunnelManager.EstablishWithJumpChain`），别再当缺口排期。
- **启动恢复标签**：已实现（`SessionStateCoordinator.OpenTabs`）。

## 4. 落地状态（0.1.367）

| # | 项 | 状态 | 落点 |
|---|---|---|---|
| P0-1 | SFTP 跟随终端目录 | ✅ | `VtStreamPreprocessor` OSC 7 → `VtTerminalEngine.DirectoryChanged` → `TabContainerControl.FollowSftpTo` → `SftpDualPanePanel.FollowTo`（默认开，栏头有开关） |
| P0-2 | 代理（HTTP/SOCKS4/SOCKS5） | ✅ | `ProxyConfig` + `ConnectionConfig.Proxy`；`SshConnectionInfoFactory.ApplyProxy`（Terminal/Sftp/Tunnel/采样共用）；连接属性「代理」分节；`ConnectionStoreJson` 走 ProtectField |
| P0-3 | OSC 9 / 777 / 99 桌面通知 | ✅ | `TerminalNotification` + `VtStreamPreprocessor.NotificationSink` → 通知中心 + 托盘气泡（error/warn 用 Toast，info 只入中心） |
| P1-4 | 远端主机资源面板 | ✅ | `RemoteStatsSampler`（独立 SSH 连接，只读 `/proc`）+ `RemoteMonitorPanel`（术语菜单/命令面板） |
| P1-5 | 回滚行数可配 | ✅ | `TerminalLimits`（0..100k，默认 10k）收敛引擎与 profile 两处 clamp；连接属性「终端」分节新增「回滚行数」 |
| P1-6 | URL / 路径 Ctrl+Click | ✅ | `TerminalControl.Links.cs`：URL → 浏览器、本地路径 → 资源管理器、远端路径 → 同主机 SFTP 跳转（无则新开） |
| P1-7 | 触发器（发命令/通知/切标签） | ✅ | `HighlightRule.Action|ActionArgument|ActionCooldownSeconds` + `HighlightEngine.MatchTriggers` + `TerminalControl.Triggers.cs`（按行扫流，暂停标签也生效）+ 高亮规则编辑框三行新控件 + 持久化 |
| P2-8 | 渲染脏格差分 | ⏸ 未做 | 需 GPU/逐格 diff 基线，风险收益比留待有性能实测后再评估 |
| P2-9 | 更新检查 | ✅ | `UpdateChecker`（GitHub releases，TLS1.2 显式开，静默失败）→ 帮助菜单「检查更新…」 |
| P2-10 | i18n | ⏸ 未做 | 需全量文案抽取，等外部用户 |
