---
workflow_mode: standard
baseline_mode: off
standards:
  required_files: []
  forbidden_paths: []
  required_terms: []
  forbidden_terms: []
  required_commands: {}
  path_rules: {}
---

# Attention

本文件是 CodeStable 技能启动必读的项目注意事项入口。所有 CodeStable 子技能开始工作前必须读取它。

## 项目碎片知识

<!-- cs-note managed: 用 cs-note 维护，新条目按下面分节追加 -->

### UI 体系（2026-09-05 AntdUI 全迁移后）

- **视觉 SSOT**：docs/DESIGN-LANGUAGE.md v1.2（色 token/字体阶梯/间距/AntdUI 规范）；
  布局机械规则 docs/UI-SCALING-CONVENTIONS.md。一句话：颜色找 ColorTable、字体找 FontPolicy、
  组件找 DialogStyle、原生控件暗色找 NativeTheme、坐标找 DpiScale。
- **AntdUI 2.4.8 已全窗体迁移**（lib/AntdUI.dll，net40 版，Apache-2.0）：A 类完整迁移 16 个窗体、
  B 类基类替换 7 个；豁免区 MainForm/TerminalControl/连接树/ToastForm。
  新窗体默认 `AntdUI.Window` + TTypeMini 语义按钮；提示用 AntdUI.Message（warn/error/info），
  阻断确认仍 MessageBox；AntdUI 窗体不再叠 FormFontPolicy.Apply。
- **语义色已实现**：GdtermColorTable.Danger/Warning/Success/Info（#F85149/#D29922/#3FB950/#58A6FF）；
  状态色只用于文字与图标。全仓裸 FromArgb 字面量已清零（例外：连接树自绘、MenuIconFactory.Ink）。
- **原生控件过渡层**：Services/NativeTheme.cs —— .Dark()/.DarkPrimary()/.DarkDanger() 扩展，
  侧边板 ListView 交互面板统一暗色入口，不要在面板里再写裸背景/前景色。
- **裸 0,122,204 品牌蓝禁用**：主操作色是终端绿（AntdUI SetPrimary #00B84A / 原生按钮 GdtermColorTable.Accent）。

### 技术栈与选型

- **框架：** .NET Framework 4.6.2 + WinForms（Win7/Server 2008 原生支持）；**主程序强制 x64**（PlatformTarget=x64，修 RDP 许可存储错位与 winpty 加载）
- **RDP：** 双引擎——默认 **FreeRDP 进程嵌入**（wfreerdp.exe /parent-window，CI 自建 2.11.7 免 MSLicensing 提权）；元数据 rdp_engine=mstscax 可切回 AxHost 零 interop 承载 mstscax（编译不依赖 AxMsTscLib.dll）
- **RDP 重定向重连排查（2026-09-04 根因更新）**：NSFOCUS 堡垒机 token 重连
  LOGOFF_BY_USER 的最终根因是 **FreeRDP rdp_write_header 恒用 2 字节 PER 长度**
  （`length|0x8000`），致 SEC_EXCHANGE 帧 95B vs mstsc 1 字节 94B。
  目标 Windows Server 要求严格 PER 编码，在 SEC_EXCHANGE 后即时 DPU 踢线
  （Client Info 从未上 wire，之前 "Client Info 后 ~52ms" 是发送前 hexdump 误导）。
  修复：`rdp_write_header` 改 `per_write_length`（commit `fade82d`，CI 待测）。
  
  2026-09-02 定稿的 salted-checksum 结论（commit `5544989`）已由 v0.1.178 实测否定：
  wire=0x0048 对齐 mstsc 后仍被踢。该发现文档已标 superseded。
  
  排雷顺序：
  1. X.224 CR/CC → 字节对齐
  2. GCC 四块（CS_CORE/CS_CLUSTER/CS_SEC/CS_NET）→ 逐字节对齐
  3. SEC_EXCHANGE 内部数据 → 内容一致（仅 PER 长度不同）
  4. ~~Client Info SEC 标志 0x0848 vs 0x0048~~ → 已排除
  5. ~~clientAddress 127.0.0.1 vs 真实 IP~~ → 已排除
  6. ~~CS_CLUSTER 0x15/0 vs 0x17/1~~ → 已排除
  7. **MCS SendData userData PER 长度编码** → 当前候选（commit `fade82d`）
  
  详见 compound/2026-09-04-learning-pitfall-freerdp-per-length-encoding-sec-exchange.md
  （旧 salted-checksum 文档：compound/2026-09-02-learning-pitfall-freerdp-do-secure-checksum-persists-redirect.md，已标 superseded）
- **RDP 二重连机制并存（进程重启 + 进程内 redirect）**：C# FreeRdpClient.cs TryAutoReconnectWithToken
  用新 wfreerdp 进程重启并锁 /sec:rdp；进程内 redirect 走 FreeRDP rdp_client_redirect；
  diag.log 分析需注意 C# LogStreamLine 关键字过滤（'encryption'/'identity' 非关键字会在
  MaxLoggedLines 后丢弃，需用 'gdterm redirect:'/'security' 前缀或 [WARN] 才能捕获）
- **终端：** 成熟持续演进的 .NET 终端模拟库（v1 引入库，后期评估自研）
- **SSH 隧道：** SSH.NET（Renci.SshNet）纯托管，无 native 依赖
- **KeePass：** KeePassLib，.kdbx 读写
- **AI 协议：** OpenAI-compatible API（兼容 Ollama/vLLM 等）
- **UI 布局：** 左侧树形连接列表 + 右侧标签页 + 底部状态栏

### 编译与构建

- 目标：.NET Framework 4.6.2，绿色版免安装
- 产出：单文件夹，U盘便携；随附子目录：winpty 三件套、vendor\freerdp\（wfreerdp.exe）、
  vendor\fzf\（v0.65.2，末代 Win7 支持）、vendor\fd\（v10.2.0，Rust 1.77 锁定构建）；
  Program.cs 启动时把 vendor\fzf\/vendor\fd\ 追加到 PATH 末尾，本地终端直接可用
- 日志/可写数据在程序根目录 logs\（diag.log、commands\、terminal\），不用 %APPDATA%（便携约定，详见 compound/trick-portable-app-local-assets-layout）
- 当前环境无 .NET SDK，需在 Windows 上用 Visual Studio/MSBuild 编译
- `gdterm.sln` 已含全部 13 个项目的 Build.0（含 Gdterm.Tests）
- Terminal ProjectGuid 已修正为合法 hex（DEFA，非 DEFG）
- CommandHistoryStore 已列入 Logging.csproj Compile
- 非致命吞异常经 `DiagLog` 写入 data/logs/diag.log（人读文本，source 前缀 info:/swallowed: 分级）；Gdterm.Terminal/Gdterm.Rdp 不引用 UI，各自有静态日志汇 TerminalLog/RdpLog（Initialize(Action<string,string>) 由 Program.cs 接 CrashLog）


### 安全约束

- 剪贴板密码复制经 ClipboardProtector，默认 30s TTL 自动清空（内容未变时）
- AI 对话历史默认 MaxHistoryMessages=40；AiModelStore ApiKey 落盘优先 gdk2:（主密码 PBKDF2+AES-CBC，可移植），无主密码时 gdk1: XOR；兼容旧明文；长期可再迁 KeePass 条目

- 密码库随绿色版走（U盘便携），强制高密码强度策略
- 主密码会话级解锁，闲时自动锁定
- Jump Server 手动跳板链，连接配置显式声明

### 运行与本地起服务

### 测试

- 零 NuGet 控制台 runner：`src/Gdterm.Tests`（`Gdterm.Tests.exe`）
- 覆盖：DefaultPorts、LogSanitizer CLI 脱敏、ConnectionStoreJson 往返（无 password 字段；metadata+serial 磁盘 round-trip，读写必须成对实现，见 compound/decision-connections-json-read-write-symmetry）
- Windows：`msbuild gdterm.sln /p:Configuration=Release` 后运行 Tests.exe

### 命令与脚本陷阱

- 发布：`tools/pack-release.ps1`（Windows MSBuild）；Linux 仅 `tools/pack-release.sh` 布局检查
- 构建说明：`docs/BUILD.md`
- 手写 csproj 必须补 Compile Include；ProjectGuid 必须合法十六进制


### 路径与目录约定

### 环境变量与凭证

### 其他

- brainstorm 已完成，见 `.codestable/brainstorms/gdterm/brainstorm.md`
- ops-toolbox brainstorm 已完成，见 `.codestable/brainstorms/ops-toolbox/brainstorm.md`
- post-MVP 增强路线图见 `.codestable/roadmap/post-mvp-enhancements/`
- 主密码与 KeePass 密码合一，启动时输入一次，自动解锁 KeePass
- 空闲锁定上限 30 分钟（硬限制），重启应用后重新输入主密码
- 所有工具支持 config 文件自定义（data/config/tools/*.json）
- 扫描体系已插件化（2026-08 用户决策，取代旧的"仅内置模块"约束）：Gdterm.Tools/Scanning，
  插件 = manifest.json + ps1/sh 脚本于 data\plugins\scanner\，输出契约 FINDING|级别|标题|详情，
  FileSystemWatcher 热更新；通道 LocalChannel（PowerShell 解析链：PATH→pwsh→System32 显式路径）/SshChannel
  （linux 脚本 base64 内联为主、SFTP 仅大文件回退；win 远端与宿主机同源 ps1 EncodedCommand）/
  WmiScanChannel（远端 Windows 无 OpenSSH 的备用通道，Win32_Process+ADMIN$ 取回），
  Ansible 预留；脚本必须落在插件目录内（防越界）；UI 入口 工具→扫描中心（插件）
- 插件签名信任链（2026-08）：RSA-3072+SHA256，官方公钥 XML 钉死在 ScanPluginStore.OfficialPublicKeyXml
  （私钥 gdterm-official-1 离线保存于维护者机器，不进仓库/CI）；plugin.sig 记录 manifest/script 双哈希+签名，
  规范负载 = hex(sha256(manifest))||0x00||hex(sha256(script))；判定：无 sig/外来 keyId → Unsigned（首次运行确认，
  按 id+内容哈希记账于 data\plugins\config\scanner-approved.json，即 ScanPluginStore.LedgerPath
  = GetDirectoryName(_userRoot)\config\scanner-approved.json，_userRoot 为 <base>\data\plugins\scanner），
  官方 keyId 但哈希/验签不符 → Invalid（篡改信号，Runner 硬拒绝）；
  内置四插件也走真实签名流程（BuiltinPlugins.SignatureJson 内嵌，物化/版本刷新/无篡改补齐三路径写 .sig）；
  改动任一 verbatim 内容必须重签（tools 流程见提交历史 python 扫描器，注意 C# verbatim "" 转义成对扫描）
- 运维工具箱本体仍是内置模块（IToolModule），插件化仅限扫描体系
- 密码分析器检测弱/重复/过期密码，生成健康报告
- 凭据继承支持文件夹→子连接传播，连接级覆盖优先
- 会话持久化保存窗口布局和打开的 tab，重启自动恢复

## 分层约束

- UI 不直接 `new RdpClient` / `TerminalSession` / `SerialSession`；经 `IRdpClientFactory` / `ITerminalSessionFactory`
- 运维工具走 `ISshRemoteSession`；端口转发走 `ISshPortForwardHost`；隧道走 `ITunnelManager`
- 凭据解析走 `CredentialResolver`；活动会话侧栏绑定走 `ActiveSessionBridge`
- 终端自动日志默认关；连接 Metadata 设 autoLog=true 或 terminalProfile.autoLog 才启用，日志目录 data/logs/terminal
- 密码健康只保留 PasswordHealthForm（菜单入口）；PasswordHealthPanel 已删除，勿再引入重复面板
- finding-10 resolved：MainForm~385 组合根；TabContainer~309 字典+chrome 壳；业务全在 Services/
- UI Services 新增：TabSplitService / TabChromePainter / TabSelectionCoordinator / AiCommandGateBinder / MainFormCommandRouter / LockStateCoordinator / AppShutdownCoordinator / ConnectionOpenCoordinator
- 禁止再把业务堆回 MainForm/TabContainer；新逻辑进 Gdterm.UI/Services/
- P0 post-split 2026-07-26：Connect dispose 竞态、危险命令 fail-closed、主密码 PBKDF2(100k)+旧哈希迁移、锁屏 ClearCachedCredentials+Watchdog.PauseAll、重连无 Sleep/DoEvents
- master-password.json 现含 algorithm/iterations；缺省 algorithm 视为旧 SHA256，解锁后升级
- P1 post-split 2026-07-26：分屏终端解析、hop 凭据、pause 不泵 UI、ApiKey 强制 gdk2、SecretScan 脱敏再验证、ITerminalSession.TryGetSshClient、测试扩 CredentialPayload/SecretFinding/PBKDF2

- go-live 2026-07-26：重连必须 async（ConnectAsyncIfNeeded + WaitForTerminalConnectedAsync），禁止 UI 线程 GetResult
- ConnectionHealthMonitor 用边沿+Rearm/RecordReconnect；锁屏 ResumeAll 会重触发断线会话
- TunnelManager 同 connectionId 单飞；跳板 ConnectHop 支持私钥
- AuditLogger 写盘失败回落 audit-fallback.jsonl；锁屏 IdleLock/Unlock 写审计
- RDP CredWrite 使用 CRED_PERSIST_SESSION；进程退出清理
- VT 真彩/TUI（2026-07-26）：默认 `TerminalProfile.Renderer=VtCell` → `CellGdiRenderer`+`VtTerminalEngine`；`renderer=lightweight` **仅紧急回退**，低配机不要默认切
- 低配控内存：scrollback 默认 300（UI 夹 100–2000，硬顶 2000）+ 非活动 tab Pause 停 Timer + 真彩 brush 缓存≤256 + 懒连接；暂停 tab 仍 Feed 引擎以保持 TUI/alt-screen 状态，但不 BeginInvoke/不重绘
- SSH TERM 来自 `TerminalProfile.TerminalType`（默认 xterm-256color）；连接后不自动 uname
- `ITerminalSession.SendBytes`/`Resize`；SSH window-change 经反射 SendWindowChangeRequest
- 绿色分发必须带 `VtNetCore.dll` + `LICENSE.VtNetCore.txt`；CI 见根目录 `appveyor.yml`（VS2022 / net462）
- AppVeyor 坑（2026-07-26）：`Gdterm.Tools` 禁止错误 Import 路径拼接；Core 禁止 `RdpOptions`；KeePassLib 用 **2.30.0** + `ProtectedBinary`/`Binaries.Set`；PackageReference 项目需 `RestoreProjectStyle=PackageReference`
- AppVeyor PowerShell 坑（2026-08）：PS5.1 Invoke-WebRequest 对非 HTML 返回 byte[]，用 WebClient.DownloadString；CMake≥4 需 `-DCMAKE_POLICY_VERSION_MINIMUM=3.5`；EAP=Stop 会把 native stderr 变 ErrorRecord 中断脚本，native 调用段设 EAP=Continue 只看 $LASTEXITCODE；反引号续行内不能放 # 注释
- C# 7.3 LangVersion（net462 经典 csproj 默认）：嵌套作用域不能遮蔽外层局部名/参数名（如 OnPaint(PaintEventArgs e) 里不能再声明 e，否则 CS0136）

## UI 快捷键与菜单约定（2026-08）

- **UI 动作一律 Ctrl+Shift+字母**（Ctrl+Shift+K/L/R/W/F/P），普通 Ctrl 组合属于 shell readline，禁止被 ProcessCmdKey/menu ShortcutKeys 抢走
- 菜单按职责分组：文件=打开类动作+导入导出；连接=当前会话操作+书签/最近；视图=外观布局；终端=搜索/会话功能/监控；工具=独立工具+安全集群+设置集群；帮助=快捷键/日志/关于
- 菜单/右键图标走 `MenuIconFactory`（纯 GDI+ 手绘 16x16，零资源零字体依赖，按 key 缓存）；弹窗字体统一走 `FormFontPolicy.Apply(form)`（跟随全局 UI 字体，只替换雅黑系，等宽字体不动）
- **UI 缩放规范（2026-08，硬约束）**：文本控件禁止固定像素 Size（AutoSize+Padding）；布局用 Dock/TableLayout/Flow 禁绝对坐标；禁止硬编码雅黑字体（继承 Form.Font；标题用 Font.FontFamily+Size+N 相对写法；Consolas 等宽例外）；不可避免固定值走 `DpiScale.V/P/S/Factor`；**DpiScale 只用于子控件级固定值，禁止作用于窗体自身 Size（PerMonitorV2 已自动缩放窗体，叠加=双重缩放）**；手写窗体不设 AutoScaleMode。完整规则 docs/UI-SCALING-CONVENTIONS.md，决策记录 .codestable/compound/2026-08-26-decision-winforms-ui-scaling-convention.md
- 弹窗遵循渐进披露：默认简洁，“更多选项”折叠进阶区（ConnectionDialog 为范本）
- 标签导航：Ctrl+Tab/Ctrl+Shift+Tab 循环切签、Ctrl+Alt+1..9 直达、中键关标签、双击标签栏空白=新建连接；连接树支持拖拽归组（改 GroupPath 并持久化）
- 状态栏可点击直达：隧道→端口转发、密码库→KeePass 管理、AI→AI 设置、安全→修改主密码
- 真彩/TUI 自动化：`VtTerminalEngineTests` + `TerminalProfileTests`；手工 vim/tmux/codex 由 Windows 验收

## 2026-07-26 trial UX fixes
- 连接首开 ForceActivateSession（单标签 SelectedIndexChanged 不触发）
- 隧道判断用 JumpChain 而非仅 Tunnel
- 试运行 AuditLogConfig 全开 + DiagLog.Info → data/logs/diag.log（人读文本，不再 crash.jsonl）
- Esc/F11 + 右上角「退出专注」从专注回标准；connections.json host gdh1: 混淆
- 绿色包补拷 KeePassLib/Renci.SshNet/VtNetCore
- 本地终端：块读 stdout（非行事件）+ 本地行缓冲回显仅限 Lightweight 渲染路径
- 字体：renderer 实测 cell 宽高（GenericTypographic，精确 advance 不取整防光标漂移），禁止写死 8x16
- 外观：工具→外观设置 → data/config/appearance.ini；DPI 感知靠 app.manifest PerMonitorV2，**禁止 SetProcessDPIAware**（与 manifest 冲突）
- SSH 无密码/无私钥时终端黄字提示；连接前若 KeePass 未解锁弹 KeePassUnlockForm
