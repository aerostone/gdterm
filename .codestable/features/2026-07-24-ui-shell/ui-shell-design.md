---
doc_type: feature-design
feature: 2026-07-24-ui-shell
requirement: null
roadmap: gdterm
roadmap_item: ui-shell
status: approved
summary: 实现 WinForms 主界面——左侧 TreeView 连接面板、右侧 TabControl 标签页容器、底部 StatusBar、全局菜单和工具栏、闲时锁定遮罩，集成所有模块
tags: [ui, winforms, shell, integration]
---

# ui-shell — WinForms 主界面

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| 连接面板（Connection Panel） | 左侧 TreeView 显示连接树 | 不同于连接配置 |
| 标签页容器（Tab Container） | 右侧 TabControl 承载各种面板 | 不同于单个标签页 |
| 状态栏（StatusBar） | 底部显示连接/隧道/密码库/AI 状态 | 不同于 RDP 状态 |
| 锁定遮罩（Lock Overlay） | 锁定时覆盖整个窗口的半透明面板 | 不同于 RDP 锁定 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.UI` WinForms 可执行项目，实现主窗口，集成所有模块（Connections、Tunnel、Terminal、SFTP、KeePass、AI、Security、Logging、RDP）。

**为谁**：最终用户

**成功标准**：
- 左侧 TreeView 显示连接树（按 GroupPath 分组）
- 右侧 TabControl 支持打开多个标签页（SSH 终端、RDP、SFTP、AI）
- 底部 StatusBar 显示连接/隧道/密码库/AI 状态
- 全局菜单栏（文件、连接、工具、帮助）
- 右键菜单（连接面板：新建、编辑、删除、连接）
- 闲时锁定遮罩覆盖整个窗口

**明确不做**：
- 主题/皮肤系统
- 多语言支持
- 插件系统

### 关键决策

1. **UI 框架**：WinForms（.NET Framework 4.6.2）
2. **布局**：左 TreeView + 右 TabControl + 底 StatusBar
3. **模块集成**：通过构造函数注入各模块服务
4. **锁定遮罩**：Panel 覆盖整个窗口，显示密码输入框

### 前置依赖

- 所有其他模块（done ✅）

## 2. 名词与编排

### 2.1 名词层

```
Gdterm.UI/
├── Forms/
│   └── MainForm.cs              # 主窗口
├── Controls/
│   ├── ConnectionTreeControl.cs # 连接面板（TreeView + 右键菜单）
│   ├── TabContainerControl.cs   # 标签页容器（TabControl）
│   ├── StatusBarControl.cs      # 状态栏
│   └── LockOverlayControl.cs    # 锁定遮罩
├── Program.cs                   # 入口点
└── Properties/AssemblyInfo.cs
```

### 2.2 编排层

```
Program.Main()
  → 初始化各模块服务（Connections、Tunnel、Terminal、SFTP、KeePass、AI、Security、Logging）
  → 创建 MainForm
  → MainForm 加载连接树
  → 用户双击连接 → 打开标签页（SSH 终端 / RDP / SFTP / AI）
  → Security.LockStateChanged → 显示/隐藏锁定遮罩
```

### 2.3 推进策略

```
1. 创建 Gdterm.UI 项目，引用所有模块
2. 实现 Program.cs 入口点
3. 实现 MainForm 主窗口骨架
4. 实现 ConnectionTreeControl 连接面板
5. 实现 TabContainerControl 标签页容器
6. 实现 StatusBarControl 状态栏
7. 实现 LockOverlayControl 锁定遮罩
8. 集成所有模块到 MainForm
```

## 3. 验收契约

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | 启动程序 | 主窗口显示 |
| 2 | 左侧 TreeView | 连接树正确显示 |
| 3 | 双击连接 | 打开标签页 |
| 4 | 底部 StatusBar | 状态正确显示 |
| 5 | 闲时超时 | 锁定遮罩显示 |
