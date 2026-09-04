# UI 缩放与字体规范（WinForms 手写布局）

> 适用范围：`src/Gdterm.UI` 下所有 Form / UserControl / 面板。
> 目标：任意 DPI（100% / 125% / 150% / 200%）与全局 UI 字号下，界面不出现文字溢出、控件拥挤、按钮过小。

## 背景

本项目窗体大多为手写 `InitializeComponent()`（无 Designer），**不能依赖 WinForms 的
AutoScaleMode 自动缩放**（该机制只在 Designer 生成的、构造期即设置字体的窗体上可靠）。
历史上大量窗体硬编码 `"Microsoft YaHei" 9f` 与固定像素 `Size`，用户把全局字号调到 11pt、
屏幕 144dpi 时出现"凭据"按钮文字溢出等问题。

## 核心规则（必须遵守）

### 规则 1：文本控件禁止固定像素 Size

按钮/标签/复选框/单选框等**承载文字的控件一律 `AutoSize = true`**，
用 `Padding`/`Margin` 控制留白，用 `MinimumSize` 保证最小可点区域：

```csharp
// ✗ 错误：96x26 是按 9pt@96dpi 设计的，11pt@144dpi 必然溢出
var btn = new Button { Text = "凭据", Size = new Size(96, 26) };

// ✓ 正确：随文字与 DPI 自动伸缩
var btn = new Button { Text = "凭据", AutoSize = true, Padding = new Padding(2) };
```

TextBox/NumericUpDown/ComboBox 等输入控件：**只给宽度、不给高度**
（高度由字体决定），或干脆交给布局面板。

### 规则 2：布局用 Dock / TableLayoutPanel / FlowLayoutPanel，禁止绝对坐标

- 表单类 → `TableLayoutPanel`（标签列 AutoSize，值列 Percent(100)）
- 按钮条 → 底部 `FlowLayoutPanel`（`FlowDirection = RightToLeft` 实现"确定在右"）
- 全屏覆盖层等特殊场景可用 `Dock = Fill`

仅当控件不含文字且语义就是"固定装饰"时才允许绝对 `Location`。

### 规则 3：禁止硬编码 UI 字体

```csharp
// ✗ 错误：绕开全局字号，且 "Microsoft YaHei" 前缀匹配之外的写法会漏改
Font = new Font("Microsoft YaHei", 9f)
Font = new Font("微软雅黑", 9f)

// ✓ 正确：继承 Form.Font（FormFontPolicy.Apply 已统一设置为全局 UI 字体）
var lbl = new Label { Text = "…", AutoSize = true };   // 不写 Font 即继承

// ✓ 非继承场景（面板/UserControl 构造期、非雅黑系显式需求）：用全局字体工厂
Font = Services.FormFontPolicy.UiFont();                    // 全局字号常规
Font = Services.FormFontPolicy.UiFont(+5f, FontStyle.Bold); // 标题强调（相对全局 +N）
Font = Services.FormFontPolicy.UiFont(-1f);                 // 次要文字（相对全局 -N）
```

> 注意：`UiFont` 在 `Gdterm.UI` 程序集内可用；跨程序集（如 Gdterm.Tools）
> 不可引用 UI 层，改用环境字体 + `ParentChanged` 延迟相对缩放。

例外（允许显式设字体）：
- **等宽语义**（代码、路径、密码、终端）：`new Font("Consolas", …)` 可保留，
  但优先从外观设置读取；
- **图标字形**（如 Segoe UI Emoji 大号符号）可保留，但需按 DPI 缩放字号；
- 标题强调：用 `new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold)`
  这类**相对当前字体**的写法，不得写死字族和磅值。

### 规则 4：不可避免 的固定值必须经 DPI 缩放

列表列宽、图标尺寸、分隔间距、覆盖层尺寸等确实需要具体数值的，
统一走 `Gdterm.UI.Services.DpiScale`：

```csharp
float dpi = DpiScale.Factor(this);          // 当前 DPI / 96
panel.Height = DpiScale.V(this, 42);        // 数值缩放
ctrl.Location = DpiScale.P(this, 20, 75);   // 坐标缩放
ctrl.Size = DpiScale.S(this, 120, 26);      // 尺寸缩放
iconFont = new Font(iconFamily, DpiScale.V(this, 28)); // 字号缩放
```

注意：`CreateGraphics()` 在句柄创建前后均可调用；每个窗体/面板构造开头取一次
`float dpi = DpiScale.Factor(this)` 存局部变量循环使用，避免反复取。

### 规则 4b：表单行距必须字体驱动（RowStep）

手写 `y += 35` 固定步进是"字体重叠"的直接根因——步进按 9pt 设计，字号调到 12pt
后行高超过步进，上下控件互相叠压。改法：

```csharp
// ✗ 错误：固定步进，9pt 设计基准
y += 35;

// ✓ 正确：行距 = 当前字体实测行高 + 9 间距（保底 30）
y += FormFontPolicy.RowStep(this);
```

窗体自身高度同步按字号比例放大：

```csharp
float grow = FormFontPolicy.UiFontSize / 9f;
Size = DpiScale.S(this, 500, (int)(480 * Math.Max(1f, grow)));
```

### 规则 4c：Win7/2008R2 字体兼容（禁用裸 "Microsoft YaHei UI"）

`Microsoft YaHei UI` 字族 Win8 才引入。Win7 上 `new Font(...)` 不抛异常而是静默
回退宋体，中文度量失控 → 挤压/重叠。所有 UI 字体获取必须走
`FormFontPolicy.UiFont()/UiFontName`（内置安装探测回退链：
YaHei UI → YaHei → Segoe UI → 系统默认），禁止再手写字体名构造。

### 规则 5：Form 统一收口

- 构造末尾调用 `Gdterm.UI.Services.FormFontPolicy.Apply(this)`（现有约定，保持）；
- 手写窗体不要设 `AutoScaleMode`（无 Designer 基准时它不起作用甚至双倍缩放）；
- 对话框默认 `FormBorderStyle.FixedDialog` + `StartPosition.CenterParent`。

## 已知遗留与豁免清单

- `TerminalControl`：单元格渲染自算字体度量，不走本规范；
- `MainForm`：由 `ApplyGlobalUIFont` 单独管理；窗体自身 `Size`（如 `new Size(1200,800)`）由 PerMonitorV2 自动缩放，不套 DpiScale；
- `SplitPaneControl`：splitter 交互条 `splitterSize=4` 与面板尺寸均为**相对 Width/Height 布局**（非固定的子控件尺寸），不走 DpiScale；
- 临时弹出的收集窗体自身 `Size`（如 `SnippetSearchPanel` 的变量填写窗）由 PerMonitorV2 自动缩放；
- 第三方/原生窗口嵌入区不受影响。

注意：**Labels / TextBoxes / ComboBoxes 等子控件的固定高度数值**（如 `new Size(labelW, 22)`、`new Size(w, 25)`）必须走 `DpiScale.V(...)`，宽高不可整体豁免（宽度可为布局变量，高度按 DPI 缩放）。

## 自查命令（提交前运行）

```bash
# 不应再有新增命中（存量见 git blame 豁免清单）
# 期望命中 = 0（全部走 DpiScale 或属豁免）
grep -rn 'Size = new Size(' src/Gdterm.UI/Forms src/Gdterm.UI/Controls \
  | grep -v 'DpiScale\|MinimumSize\|ClientSize' \
  | grep -vE 'MainForm.cs|SplitPaneControl.cs|SnippetSearchPanel.cs'
grep -rn 'new Font("Microsoft YaHei\|new Font("微软雅黑' src --include='*.cs' \
  | grep -v 'FormFontPolicy.cs\|MainForm.cs'
# 两者命中应为 0（MainForm 由 ApplyGlobalUIFont 豁免；FormFontPolicy 是策略本身）
```
