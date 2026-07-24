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
