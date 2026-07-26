# Attention

本文件是 CodeStable 技能启动必读的项目注意事项入口。所有 CodeStable 子技能开始工作前必须读取它。

## 项目碎片知识

<!-- cs-note managed: 用 cs-note 维护，新条目按下面分节追加 -->

### 技术栈与选型

- **框架：** .NET Framework 4.6.2 + WinForms（Win7/Server 2008 原生支持）
- **RDP：** AxMsTscLib ActiveX 嵌入
- **终端：** 成熟持续演进的 .NET 终端模拟库（v1 引入库，后期评估自研）
- **SSH 隧道：** SSH.NET（Renci.SshNet）纯托管，无 native 依赖
- **KeePass：** KeePassLib，.kdbx 读写
- **AI 协议：** OpenAI-compatible API（兼容 Ollama/vLLM 等）
- **UI 布局：** 左侧树形连接列表 + 右侧标签页 + 底部状态栏

### 编译与构建

- 目标：.NET Framework 4.6.2，绿色版免安装
- 产出：单文件夹，U盘便携
- 当前环境无 .NET SDK，需在 Windows 上用 Visual Studio/MSBuild 编译
- `gdterm.sln` 已含全部 13 个项目的 Build.0（含 Gdterm.Tests）
- Terminal ProjectGuid 已修正为合法 hex（DEFA，非 DEFG）
- CommandHistoryStore 已列入 Logging.csproj Compile
- 非致命吞异常经 `DiagLog` 写入 data/logs/crash.jsonl（source 前缀 swallowed:）


### 安全约束

- 剪贴板密码复制经 ClipboardProtector，默认 30s TTL 自动清空（内容未变时）
- AI 对话历史默认 MaxHistoryMessages=40；AiModelStore ApiKey 落盘优先 gdk2:（主密码 PBKDF2+AES-CBC，可移植），无主密码时 gdk1: XOR；兼容旧明文；长期可再迁 KeePass 条目

- 密码库随绿色版走（U盘便携），强制高密码强度策略
- 主密码会话级解锁，闲时自动锁定
- Jump Server 手动跳板链，连接配置显式声明

### 运行与本地起服务

### 测试

- 零 NuGet 控制台 runner：`src/Gdterm.Tests`（`Gdterm.Tests.exe`）
- 覆盖：DefaultPorts、LogSanitizer CLI 脱敏、ConnectionStoreJson 往返（无 password 字段）
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
- 运维工具采用内置模块而非插件系统（.NET 4.6.2 + Win7 限制）
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
- AppVeyor 坑（2026-07-26）：`Gdterm.Tools` 禁止错误 Import 路径拼接；Core 禁止 `RdpOptions`；KeePassLib 用 **2.30.0** + `ProtectedBinary`/`Binaries.Set`；RDP 反射加载、编译不依赖 AxMsTscLib.dll；PackageReference 项目需 `RestoreProjectStyle=PackageReference`
- 真彩/TUI 自动化：`VtTerminalEngineTests` + `TerminalProfileTests`；手工 vim/tmux/codex 由 Windows 验收
