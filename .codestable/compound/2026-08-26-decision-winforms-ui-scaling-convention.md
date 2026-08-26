---
doc_type: decision
category: convention
date: 2026-08-26
slug: winforms-ui-scaling-convention
status: active
area: src/Gdterm.UI 全部窗体与面板
tags: [winforms, dpi, font, layout, ui]
---

# WinForms 手写布局：字体与 DPI 缩放规范

## 背景

gdterm 的窗体大多为手写 `InitializeComponent()`（无 Designer 生成基准），历史上大量控件
硬编码 `"Microsoft YaHei" 9f` 与固定像素 `Size`。当用户把全局字号调到 11pt、屏幕为
144dpi（150%）时，出现"凭据"按钮等文字溢出、对话框拥挤的问题。只修单个按钮不解决系统性
问题——任何新增的固定值都会在下一个高 DPI 用户处复发。

## 决定

所有 UI 代码必须遵守以下规则（完整版含代码示例见仓库
`docs/UI-SCALING-CONVENTIONS.md`，本条为其索引与决策记录）：

1. **文本承载控件禁止固定像素 Size** —— 一律 `AutoSize = true` + `Padding`/
   `MinimumSize` 调整留白；输入框只给宽度不给高度。
2. **布局用 Dock / TableLayoutPanel / FlowLayoutPanel，禁止绝对坐标**
   （例外：不含文字的装饰元素、全屏覆盖层）。
3. **禁止硬编码 UI 字体**（`new Font("Microsoft YaHei", …)` /
   `new Font("微软雅黑", …)`）—— 控件继承 `Form.Font`，由
   `FormFontPolicy.Apply(this)` 统一设置为全局字号。例外：Consolas 等宽语义、
   图标字形（需按 DPI 缩放）、标题用相对写法
   `new Font(Font.FontFamily, Font.Size + N, FontStyle.Bold)`。
4. **不可避免的固定数值必须经 DPI 缩放** —— 列宽/图标/间距用
   `Gdterm.UI.Services.DpiScale`：`DpiScale.Factor(this)`、`.V(this, px)`、
   `.P(this, x, y)`、`.S(this, w, h)`；构造开头取一次 factor 复用。
5. **Form 收口** —— 构造末尾调用 `FormFontPolicy.Apply(this)`；手写窗体不要设
   `AutoScaleMode`（无 Designer 基准时不起作用甚至双倍缩放）。

## 理由

- WinForms 的 `AutoScaleMode` 只对 Designer 生成、构造期即设置字体的窗体可靠；
  手写窗体依赖它等于没有缩放。
- `FormFontPolicy` 按字体名前缀（"Microsoft YaHei"/"微软雅黑"，忽略大小写）
  替换子控件字体，所以硬编码雅黑的**名字**会被运行时纠正，但硬编码的**固定像素尺寸**
  不会被纠正——溢出正是来自后者。
- 面板类（SidePanelFactory 创建的 17 个 UserControl 等）不在 Form 字体继承链上时，
  显式字体导致与主窗体全局字号不一致。

## 考虑过的替代方案

- **全面迁移 Designer + AutoScaleMode**：改动面太大，且现有手写逻辑（动态增删控件）
  迁移成本高，放弃。
- **只修报告问题的控件**：已被本次审计否定——152 处硬编码字体、大量固定 Size，
  逐个被动修复无法收敛。

## 后果

- 新增 UI 代码按上述规则编写；存量违规按模块逐步清理（SetupWizardForm、
  DangerousCommandDialog、TransferProgressDialog、TerminalSearchBar 及各 SidePanel 优先）。
- 提交前自查命令见 `docs/UI-SCALING-CONVENTIONS.md` 末尾的 grep。
- 高 DPI/大字号回归测试方法：系统 150% 缩放 + 全局字号 11pt 打开各对话框检查溢出。

## 第二轮清理（2026-08-26 完成）

全仓硬编码 UI 字体清零。最终形态：

1. **Form 场景**：构造末尾 `Gdterm.UI.Services.FormFontPolicy.Apply(this)`；子控件不写字体即继承。
2. **非 Form 场景**（面板/UserControl/构造期）：`Services.FormFontPolicy.UiFont(delta)` / `UiFont(delta, FontStyle.Bold)`，delta 相对全局字号。
3. **跨程序集**（Gdterm.Tools 等不引用 Gdterm.UI）：环境字体 + `ParentChanged` 延迟相对缩放，禁止 import Gdterm.UI.Services。
4. **等宽语义**（Consolas/Courier）保留，字号跟随 `Program.GlobalAppearance.UIFontSize`。
5. **尺寸**：面板内固定尺寸用 `DpiScale.V/S/P`；主窗体自身 Size 交 PerMonitorV2，不手工缩放。

覆盖：SetupWizard、DangerousCommand 对话框+配置页、TransferProgress、TerminalSearchBar、
全部 14 个 SidePanel、AiSettings、SshKeyManager、Appearance、PasswordHealth、KeePass 全家、
QuickCommandEditor、ChangeMasterPassword、PasswordGenerator、ConnectionDialog、ToastNotifier、
MasterPasswordPrompt、ViewModeController、ConnectionTreeControl、ConnectionQuickJump、ToolPanelHelper。
豁免：MainForm.cs（ApplyGlobalUIFont 接管）、FormFontPolicy.cs 本体。

教训：`UiFont(FontStyle.Bold)` 会把枚举塞进 float 参数——正确写法 `UiFont(0f, FontStyle.Bold)`；
C# 7.3 无 `with {}`；MainForm 尺寸手工缩放会与 PerMonitorV2 叠加导致双重放大。
