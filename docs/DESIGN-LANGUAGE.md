# gdterm 设计语言（C/S 客户端 · WinForms）

> 版本：1.2（2026-09-05）—— v1.2 决议：**全窗体 AntdUI 迁移完成**（除 MainForm/TerminalControl/
> 连接树/ToastForm 豁免区），语义色四 token 落地 `GdtermColorTable`，裸 FromArgb 色值全仓清零
> （保留连接树自绘与 MenuIconFactory 图标专用色），新增原生控件过渡层 `NativeTheme`。
> v1.1 决议：引入 **AntdUI 2.4.8**（Apache-2.0，net40+，纯 GDI）作为组件底座，
> 与本设计语言对齐使用（`Config.IsDark=true` + `Style.SetPrimary(终端绿)`，见 §9）。


> 约束前提：.NET Framework 4.6.2 WinForms、Windows 7 / Server 2008 R2 起步、单 EXE、无第三方 UI 框架。
> 执行规则：本文是唯一视觉事实来源（SSOT）。代码以 `GdtermColorTable` + `DialogStyle` + `FormFontPolicy`
> 三个类为实现载体；**写新 UI 前先读本文，评审 UI 代码时以本文为准。**

---

## 0. 设计立场

gdterm 是**安全运维终端**，不是内容消费应用。视觉目标排序：

1. **信息密度优先**——运维要在最短视距内分辨 20+ 会话/凭据/告警；
2. **暗色为默认**——长时间盯屏；GitHub Dark 基调，终端绿做唯一强调色；
3. **键盘优先**——所有高频操作必须有快捷键；鼠标路径是补充；
4. **Win7 可跑**——任何视觉手段不得依赖 Win8+ 字体/API（详见 §5）。

---

## 1. 色彩 Token

实现：`GdtermColorTable`（支持 `ApplyTheme` 切换，默认 Dark）。

| Token | 值 (Dark) | 语义 | 典型用途 |
|---|---|---|---|
| `Background` | `#0D1117` | 最底层背景 | 窗体底色、列表区 |
| `Surface` | `#161B22` | 卡片/控件面 | 输入框底、工具栏、悬浮面板 |
| `Border` | `#30363D` | 1px 描边 | 分隔线、输入框描边、表格线 |
| `Accent` | `#00FF41` | 唯一强调色（终端绿） | 主按钮、选中态、连接正常 |
| `Foreground` | `#E6EDF3` | 主文字 | 标题、正文 |
| `Muted` | `#8B949E` | 次级文字 | 说明、占位、标签 |
| `Hover` | `#25292F` | 悬浮态面 | 按钮/行 hover |
| `Pressed` | `#35393F` | 按压态面 | 按钮 pressed |

### 1.1 语义扩展色（功能色）

| 语义 | 值 | 用途 |
|---|---|---|
| Danger | `#F85149` | 删除/危险按钮、错误文字 |
| Warning | `#D29922` | 告警文字 |
| Success | `#3FB950` | 成功状态文字（区别于 Accent 按钮绿） |
| Info | `#58A6FF` | 链接/信息（蓝色仅在"可点击文字"场景出现） |

**规则**：
- 界面上**同时出现的强调色不超过 2 种**；大色块只允许 Accent（主操作）与 Danger（破坏性操作）。
- `#58A6FF`（蓝）不参与按钮体系；需要蓝色系按钮的旧窗体（如 KeePass 解锁）属遗留，逐步迁移。
- 状态色（Success/Warning/Danger）只用于**文字与图标**，不做大面积底色。

### 1.2 禁止项

- ❌ 手写 `Color.FromArgb(35,35,35)` 这类裸值——现有 60+ 处是历史债，禁止新增，见 §7 迁移计划。
- ❌ 纯黑 `#000000` 做背景（与终端仿真底色冲突的视觉跳变）。
- ❌ 亮色主题下直接反色（当前只有 Dark 一套 token；Light 立项时单独评审）。

---

## 2. 字体体系

实现：`FormFontPolicy`。用户可通过外观设置调 `UIFontSize`（8–24pt）。

### 2.1 两套字体，各司其职

| 类别 | 字体 | 谁能改 |
|---|---|---|
| **UI 字体**（菜单/窗体/树/状态栏） | 探测回退链：YaHei UI → YaHei → Segoe UI → 系统默认 | 用户可调字号、可换字族（探测存在才生效） |
| **终端字体**（VtNetCore 会话区） | 默认 Consolas，等宽 | 用户自由设置，设计语言不约束 |

### 2.2 字号阶梯（相对基准，非绝对 pt）

以 `UiFontSize`（默认 9pt）为基准，用 `UiFont(delta)` 派生：

| 阶梯 | API | 场景 |
|---|---|---|
| 小 | `UiFont(-0.75f)` | 状态栏、辅助角标 |
| 基准 | `UiFont()` | 正文、标签、按钮 |
| 中 | `UiFont(+1f)` | 输入框、次级标题、强调按钮 |
| 大 | `UiFont(+2f)` ~ `+3f)` | 窗体主标题 |
| 特大 | `UiFont(+5f)` | 密码生成器等展示型标题 |

- ❌ 禁止 `new Font("Microsoft YaHei UI", …)` 裸构造（Win7 无此字族会静默回退宋体，见 §5）。
- ❌ 禁止在字体阶梯之外自造 pt 值；新增场景先归入阶梯。

### 2.3 行距与布局步进

`FormFontPolicy.RowStep(control)`：**行高 = 当前 UI 字体实测行高 + 9px 间距，下限 30px**。
所有手写纵向排布必须用它，禁止 `y += 35` 类固定步进（字体重叠的直接根因）。
窗体整体高度按 `UiFontSize / 9f` 比例伸缩（见 UI-SCALING-CONVENTIONS 规则 4b）。

---

## 3. 排版与间距

### 3.1 间距标尺（4px 基）

`4 / 8 / 12 / 16 / 24 / 32`（经 `DpiScale.V` 换算）。

| 场景 | 值 |
|---|---|
| 控件内边距（按钮左右） | 8–12 |
| 相关控件间距（标签↔输入框） | 4–8 |
| 组内行距 | `RowStep()`（字体驱动） |
| 组间距 | 16–24 |
| 窗体内边距 | 12–16 |

### 3.2 布局容器优先级

```
TableLayoutPanel / FlowLayoutPanel / Dock > 手写坐标
```

手写坐标仅允许：已列入豁免名单的复杂宿主（MainForm、SplitPaneControl、TerminalControl、
SnippetSearchPanel）以及 TableLayoutPanel 表达不了的精确对齐；且纵向必须走 `RowStep`。

### 3.3 对齐

- 表单标签**左列右对齐**（`TextAlign = MiddleRight`），值区左对齐；
- 底部按钮条右对齐，主按钮最右（Windows 惯例），用 `DialogStyle.ButtonStrip`；
- 标题与正文的左边距统一（12 或 16，同窗体内不得混用）。

---

## 4. 组件规范

实现：`DialogStyle`。新窗体一律走 `DialogStyle.*`，不手搓样式。

### 4.1 按钮

| 级别 | 工厂 | 视觉 | 用途（每窗体≤1 Primary） |
|---|---|---|---|
| Primary | `MakePrimary` | Accent 实心、黑字 | 窗体主操作（连接/解锁/保存） |
| Secondary | `MakeSecondary` | Surface 底 + Border 描边、白字 | 次操作（刷新/浏览…） |
| Danger | `MakeDanger` | Danger 实心、白字 | 删除/断开（破坏性） |

- 按钮文字 2–6 字动词优先（"连接"、"解锁"、"删除"），禁止"确定/OK"类无语义文案做 Primary。
- 按钮 `AutoSize` + 水平 Padding，高度由字体决定；`FlatStyle.Flat` + `BorderSize=0`。
- 悬浮/按压用 `Hover/Pressed` token。

### 4.2 输入控件

- `DialogStyle.ApplyInput`：Surface 底 + Foreground 字 + 1px FixedSingle 边。
- 密码框等宽 Consolas 展示；普通文本输入用 UI 字体。
- 聚焦反馈：背景切换到 `#1C2128`（Surface 加亮一档）。

### 4.3 标签/标题

- 字段标签：`DialogStyle.FieldLabel`（Muted、基准字号、右对齐列）。
- 组标题：`DialogStyle.GroupTitle`（Foreground、+0.5 粗体）。
- 窗体主标题：+2~+5 粗体白字，可带 emoji 图标（密码生成器 🔑 风格）。

### 4.4 弹窗与对话框

- 外壳：`DialogStyle.ApplyChrome(form, w, h)`（Dark 底、FixedDialog、无任务栏按钮）。
- 底部：`ButtonStrip(btnCancel, btnPrimary)` 传入顺序=视觉左→右。
- 错误行：Danger 色 AutoSize 标签，占位在按钮条上方，不弹 MessageBox 打断流。
- 中断式确认（危险操作）才用 MessageBox，且用 `YesNo` 并写明后果。

### 4.5 列表/树

- 连接树：目录=Info 蓝 `#58A6FF` 可选；节点选中=`Surface` 底 + Accent 左标线。
- 文件列表（SFTP 双栏）：目录=Info 蓝，可执行/敏感文件按功能色点缀。
- 行高：字体驱动（`RowStep` 派生），禁止固定 `ItemHeight`。

---

## 5. Windows 7 / 2008R2 兼容红线

| 项 | 规则 |
|---|---|
| 字体 | YaHei UI 不存在于 Win7 → 一切字体经 `FormFontPolicy` 回退链，禁止裸 `new Font(字族名…)` |
| DPI | PerMonitorV2 不可用 → 自动降 System DPI；布局值必须过 `DpiScale`，不依赖 PMv2 行为 |
| 控件 | 只用 BCL WinForms 控件集 + GDI/GDI+；不依赖 VisualStyles 新主题、DWM 毛玻璃 |
| 颜色 | 不用 `SystemColors.*` 做主题（跟随系统会破坏 Dark 统一） |

---

## 6. 动效与反馈

WinForms 下动效克制到三个：

1. **即时色彩反馈**——hover/pressed 换 token（100ms 内感知即可，无需计时器）；
2. **进度**——传输用 `ProgressBar` 连续条 + 文字（文件名/百分比），不做装饰性动画；
3. **状态持久**——连接状态用状态栏色点（绿/黄/红）+ 文字，切换 <200ms 内反映。

❌ 禁止：淡入淡出、滑动面板、闪烁强调（运维场景降低可读性）。

---

## 7. 存量迁移与验收

### 7.1 现状（2026-09-05 基线）

- `DialogStyle` 已落地并在 KeePassUnlock 等窗体使用；
- 历史裸颜色 ~65 处（`FromArgb(35/50/60,…)`）、裸字体构造 2 处残留；
- 绝对布局窗体已基本完成 RowStep/AutoSize 化（本轮 7 窗体）。

### 7.2 迁移规则

- **改到哪，迁到哪**：触碰旧窗体时顺手把裸颜色换成 token、按钮换成 `DialogStyle` 工厂；
- **禁止新增违规**：新代码出现裸 `FromArgb`/裸字体/固定行距 → 评审直接打回；
- 豁免名单（UI-SCALING-CONVENTIONS.md）不变。

### 7.3 预提交自检（grep 快查）

```bash
# 新增违规颜色（忽略注释行）
git diff -U0 | grep "^+" | grep -v "^+++" | grep "Color.FromArgb(3[05], Color\|Color.FromArgb(5[06]0,"
# 裸字体构造
git diff -U0 | grep "^+" | grep -v "^+++" | grep 'new Font("' | grep -v Consolas
# 固定行距
git diff -U0 | grep "^+" | grep -v "^+++" | grep "y += 3[0-9]"
```

---

## 8. Token 到代码的映射总表

| 设计 Token | 代码入口 |
|---|---|
| 全部颜色 | `GdtermColorTable.XXX` |
| Danger/Warning/Success/Info | `GdtermColorTable.Danger/Warning/Success/Info`（v1.2 已实现） |
| 原生控件暗色 | `NativeTheme.Dark/DarkPrimary/DarkDanger/DarkRecursive`（AntdUI 过渡层，v1.2） |
| 字体阶梯 | `FormFontPolicy.UiFont(±delta[, style])` |
| 字族回退 | `FormFontPolicy.UiFontName` |
| 行距 | `FormFontPolicy.RowStep(ctrl)` |
| 按钮 | `DialogStyle.MakePrimary/Secondary/Danger` |
| 按钮条 | `DialogStyle.ButtonStrip(...)` |
| 字段标签/组题 | `DialogStyle.FieldLabel/GroupTitle` |
| 输入样式 | `DialogStyle.ApplyInput` |
| 窗体壳 | `DialogStyle.ApplyChrome` |
| DPI 换算 | `DpiScale.V/P/S` |

**一句话标准：颜色找 ColorTable、字体找 FontPolicy、组件找 DialogStyle、坐标找 DpiScale——四者之外无样式。**

---

## 9. AntdUI 接入规范（v1.1 起）

### 9.1 为什么是 AntdUI

| 硬约束 | 结果 |
|---|---|
| net462 | ✅ 最低 net40（lib/AntdUI.dll 用 net40 版，兼容 4.6.2） |
| Win7/2008R2 | ✅ 纯 GDI 绘图、零图片依赖、无 Win8+ API |
| 可商用 | ✅ Apache-2.0（对比 SunnyUI：GPL-3 + 商用授权，禁用） |
| 单 EXE 便携 | ✅ 单 DLL ~3.2MB 随包 |

落选者：SunnyUI（协议）、ReaLTaiizor（要 net48）、Krypton 新版（要 4.7.2，旧 LTS 才 4.6.2）、
DarkUI（作者离世停更）、AcrylicUI/Beep（net Core/net8，Win7 出局）。

### 9.2 共存策略（分区渐进）

1. **存量原生窗体不动**——Terminal/MainForm/连接树等核心区保持 GdtermColorTable 体系；
2. **新窗体/重构窗体默认 AntdUI**——继承 `AntdUI.Window`，用 Button/Input/Label/Table/Tabs；
3. **新旧视觉对齐**：AntdUI 初始化已钉死 `IsDark=true` + `SetPrimary(终端绿调暗档 #00B84A)`
   （纯 #00FF41 在暗底上做大面积按钮底色对比度不足）；
4. **改到哪迁到哪**：触碰旧窗体时如果该窗体布局要大改，直接迁 AntdUI；小修小补维持原生；
5. **设计 Token 映射**：§1 色彩/§2 字体的语义不变，实现载体从 DialogStyle 逐场景过渡到 AntdUI 控件
   （Primary=Type.Primary、Muted=AntdUI 默认次级色、RowStep 仅存留于原生窗体）。

### 9.3 代码规范

- AntdUI 窗体基类：`AntdUI.Window`（自带暗色边框/自绘拖拽/圆角阴影）；**不要**再叠 FormFontPolicy.Apply
  （AntdUI 控件自带 DPI/字体处理，Config.Font 全局设置）；
  **例外**：B 类混合窗体（AntdUI.Window 底 + 原生 ListView 等子控件）必须保留 Apply——
  原生子控件不继承 AntdUI 的 Config.Font，去掉会回退系统字体；
- 消息提示用 `AntdUI.Message.success/error/warn(form, text)` 取代 MessageBox（窗体内非阻断提示）；
  阻断式确认仍用 MessageBox；
- AntdUI 控件属性名与本设计语言 Token 的对应关系写在 PR 描述里，评审按 §1-§4 语义审。

### 9.4 首个试点

- `KeePassUnlockForm`（2026-09-05）：AntdUI.Window + Input + Button + Message；
  验证点：Win7 下窗体边框/拖拽、Input 密码框回车提交、Message 提示样式、高 DPI 缩放。

### 9.5 迁移完成状态（v1.2，2026-09-05）

**A 类完整迁移（AntdUI 控件体系）16 个**：KeePassUnlock、AppearanceSettings、PasswordGenerator、
ChangeMasterPassword、AiSettings、QuickCommandEditor、SshKeyManager、DangerousCommandRuleEdit、
TextInputForm、ConnectionQuickJump、TransferProgress、DangerousCommandDialog、SftpDualPane（局部）、
FilePane（局部）等。

**B 类基类替换（AntdUI.Window + 保留原生布局）7 个**：ConnectionDialog、KeePassManager(+EntryEdit)、
ScannerCenter、SetupWizard、PasswordHealth、KeePassEntryPicker、DangerousCommandConfig。

**豁免区（永久原生）**：MainForm、TerminalControl、SplitPaneControl、ConnectionTreeControl、
ToastNotifier.ToastForm（无边框角落弹窗）。

**原生控件过渡层**：`Services/NativeTheme.cs` —— ListView/ListBox/TreeView/TextBox/ComboBox/Button
的 `.Dark()/.DarkPrimary()/.DarkDanger()` 扩展，供侧边板（KeyBinding/PortForward/LogonScript 等十余个
ListView 交互核心面板）在不动交互逻辑的前提下统一暗色；色值取运行时 token，主题切换即时生效。

**裸色值清零**：全仓 `FromArgb(数字,数字,数字)` 字面量已全部替换为 token 映射
（30,30,30→Background / 35,50→Surface / 60→Hover / 80→Border / 204→Foreground /
0,122,204 品牌蓝→Accent 主绿 / VS teal 78,201,176→Success / 255,80,80→Danger 等）。
保留例外：连接树自绘组头/健康点三色、MenuIconFactory.Ink 图标墨色。
