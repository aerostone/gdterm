---
title: gdterm 全功能客户端脑暴
date: 2026-07-23
status: confirmed
type: brainstorm
participants:
  - user
  - ai
---

# gdterm 全功能客户端脑暴

## 背景与真问题

运维/开发人员每天面对大量远程机器，工具散落各处（MSTSC、PuTTY、KeePass、各种隧道工具），没有统一入口。在公共场所/共享机器上切换用户时密码暴露风险大。部分机器在内网需要通过 jump server 串联访问。

**核心诉求：** 一个绿色便携、低内存、支持 Win7/Server 2008 的统一远程运维客户端。

## 已敲定的设计点

### 技术栈
- **框架：** .NET Framework 4.6.2 + WinForms
- **理由：** Win7/2008 原生支持，GDI 渲染开销最低，内存 30-80MB，绿色版免安装
- **渲染：** GDI（CPU-based），对老旧集成显卡零压力

### UI 布局
- **模式：** 左侧树形连接列表 + 右侧标签页
- **类比：** MobaXterm / Royal TS 风格
- **Status Bar：** 连接状态 | 隧道状态 | 密码库锁定状态 | AI 状态

### RDP 远程桌面
- **实现：** AxMsTscLib（ActiveX 嵌入控件）
- **Jump Server：** 手动跳板链，连接配置显式声明 `jump → target`
- **流程：** Client →[SSH Tunnel]→ JumpServer →[RDP]→ Target

### 终端模拟
- **方案：** 引入成熟且持续演进的 .NET 终端模拟库
- **策略：** v1 用库快速上线，后期评估是否自研
- **候选：** 待 spike 验证（需 ANSI/VT100 完整支持、活跃维护、低内存）

### KeePass 密码管理
- **库：** KeePassLib（原生 .NET .kdbx 读写）
- **集成深度：** 自动填充 + 连接关联
- **交互：** 双击连接条目 → 自动从关联密码条目读取凭据填入
- **安全：**
  - 主密码会话级解锁，闲时自动锁定
  - 密码库文件随绿色版走（U盘便携）
  - **强制高密码强度策略**（创建/修改密码时校验长度、复杂度）
  - 用户必须手动建立连接↔密码的关联

### SSH 隧道代理
- **库：** SSH.NET（Renci.SshNet）纯托管代码
- **能力：** SSH2、端口转发（本地/远程）、动态 SOCKS 代理
- **绿色友好：** 无 native 依赖，纯 .NET DLL
- **内存：** ~2-5MB per tunnel

### AI 对话
- **协议：** OpenAI-compatible API（兼容 Ollama、vLLM 等所有兼容端点）
- **集成深度：** 中等 — 连接感知 + 建议执行
  - AI 知道当前连接的 hostname、OS、最近 N 条命令
  - 终端选中文本/输出可作为上下文发送给 AI
  - "执行此命令" 按钮将建议发送到活动终端
  - 用户始终控制执行，AI 不自动执行
- **UI：** 右侧标签页中的 AI 面板
- **内存：** ~15-30MB（HTTP 客户端 + 上下文缓冲）

### 日志轮转
- **范围：** 连接审计 + 操作日志 + 密码使用记录 + AI 交互
- **策略：** 待 design 阶段确定（按大小/按天、保留天数）

### 闲时锁定
- **触发：** 无操作 N 分钟后自动锁定
- **范围：** 锁定密码库 + 可选锁定所有活动会话
- **解锁：** 需要主密码

### 绿色版约束
- **目标平台：** Windows 7 SP1+ / Server 2008 R2+
- **形态：** 单文件夹免安装，U盘带走
- **内存目标：** 30-80MB 基础占用
- **依赖：** .NET Framework 4.6.2（Win7/2008 可装），无其他外部依赖

## 架构草图

```
gdterm (WinForms .NET 4.6.2)
├── Gdterm.Core              # 核心模型、接口定义
├── Gdterm.Connections       # 连接管理、Jump Chain 配置
├── Gdterm.Rdp               # RDP (AxMsTscLib) + Jump 隧道
├── Gdterm.Terminal          # 终端模拟（封装库）
├── Gdterm.Tunnel            # SSH.NET 隧道/代理
├── Gdterm.KeePass           # KeePassLib 密码管理
├── Gdterm.AI                # OpenAI API 客户端 + 上下文管理
├── Gdterm.Logging           # 日志 + 轮转
├── Gdterm.Security          # 闲时锁定 + 密码强度策略
└── Gdterm.UI                # WinForms 主界面（树 + Tabs + Status Bar）

模块依赖：
  KeePass ──auto-fill──→ RDP / SSH Terminal
  SSH.NET ──tunnel──→ RDP (jump chain) / SSH Terminal
  AI Chat ──context──→ Terminal (send selection/output)
  Log     ──audit──→ 全部连接 + 密码使用 + AI 交互
  IdleLock──lock──→ KeePass + 全部活动会话
```

## 待 spike / 待 design 解决

1. **终端库选型** — 需要 spike 验证候选库的 ANSI 支持完整度、内存占用、维护活跃度
2. **日志轮转策略** — 按大小 vs 按天，保留多少
3. **密码强度策略具体规则** — 最小长度、字符类别要求、字典检查
4. **密码库文件加密保护** — 除主密码外是否需要密钥文件
5. **连接配置存储格式** — JSON / XML / SQLite
