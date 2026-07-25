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

### 安全约束

- 剪贴板密码复制经 ClipboardProtector，默认 30s TTL 自动清空（内容未变时）
- AI 对话历史默认 MaxHistoryMessages=40；AiModelStore ApiKey 落盘优先 gdk2:（主密码 PBKDF2+AES-CBC，可移植），无主密码时 gdk1: XOR；兼容旧明文；长期可再迁 KeePass 条目

- 密码库随绿色版走（U盘便携），强制高密码强度策略
- 主密码会话级解锁，闲时自动锁定
- Jump Server 手动跳板链，连接配置显式声明

### 运行与本地起服务

### 测试

### 命令与脚本陷阱

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
- finding-10 拆分进度：SidePanelFactory / SessionStateCoordinator / TabSessionLifecycle / ActiveSessionBridge / CredentialResolver；MainForm 与 TabContainer 仍是布局+建签枢纽，禁止再堆业务到这两类
