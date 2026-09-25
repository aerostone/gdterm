---
doc_type: change
kind: feature
slug: 2026-09-25-ui-cancel-height-unify
status: accepted
mode: standard
summary: 密码生成器/设置向导补 ESC 取消语义 + 输入框高度 SSOT(FormFontPolicy.FieldHeight)收敛六处漂移
tags: [ui, a11y, consistency, keyboard]
risk:
  level: low
  reasons: []
model_route:
  strategy: retain-high
  reason: 涉及安全向导(SetupWizard)行为语义与全仓高度口径 SSOT 取舍,需判断力
contract:
  include:
    - src/Gdterm.UI/Forms/PasswordGeneratorForm.cs
    - src/Gdterm.UI/Forms/SetupWizardForm.cs
    - src/Gdterm.UI/Controls/AiChatPanel.cs
    - src/Gdterm.UI/Controls/BatchCommandPanel.cs
    - src/Gdterm.UI/Controls/CommandHistoryPanel.cs
    - src/Gdterm.UI/Controls/CommandTemplatePanel.cs
    - src/Gdterm.UI/Controls/ConnectionQuickJumpForm.cs
    - src/Gdterm.UI/Controls/ConnectionTreeControl.cs
    - src/Gdterm.UI/Controls/DangerousCommandDialog.cs
    - src/Gdterm.UI/Controls/FilePaneControl.cs
    - src/Gdterm.UI/Controls/MacroPanel.cs
    - src/Gdterm.UI/Controls/MultiChannelPanel.cs
    - src/Gdterm.UI/Controls/SftpBrowserPanel.cs
    - src/Gdterm.UI/Controls/TransferProgressDialog.cs
    - src/Gdterm.UI/Forms/AiSettingsForm.cs
    - src/Gdterm.UI/Forms/AppearanceSettingsForm.cs
    - src/Gdterm.UI/Forms/ChangeMasterPasswordForm.cs
    - src/Gdterm.UI/Forms/ConnectionDialog.cs
    - src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs
    - src/Gdterm.UI/Forms/KeePassManagerForm.cs
    - src/Gdterm.UI/Forms/KeePassUnlockForm.cs
    - src/Gdterm.UI/Forms/PasswordHealthForm.cs
    - src/Gdterm.UI/Forms/QuickCommandEditorForm.cs
    - src/Gdterm.UI/Forms/ScannerCenterForm.cs
    - src/Gdterm.UI/Forms/SftpPermissionForm.cs
    - src/Gdterm.UI/Forms/SshKeyManagerForm.cs
    - src/Gdterm.UI/Services/FormFontPolicy.cs
    - src/Gdterm.UI/Controls/HighlightRulePanel.cs
    - src/Gdterm.UI/Controls/KeyBindingPanel.cs
    - src/Gdterm.UI/Controls/LogonScriptPanel.cs
    - src/Gdterm.UI/Controls/PortForwardPanel.cs
    - src/Gdterm.UI/Controls/SessionBookmarksPanel.cs
    - src/Gdterm.UI/Controls/SnippetSearchPanel.cs
    - src/Gdterm.UI/Services/MasterPasswordPrompt.cs
    - .codestable/changes/2026-09-25-ui-cancel-height-unify/**
  exclude: []
  preexisting_changes:
    - .codestable/.runtime/current-package
  baseline:
    git_head: f304b00c325e1642b308cb713dd2a226c5e40ee0
    dirty_hashes:
      .codestable/.runtime/current-package: 12fd5714de3feea6591a33a4d645c9166bb4ada3a80ccce924e65676f5672ad3
  architecture_impact: unchanged
  architecture_reason: 控件级行为补齐与工具方法抽取,不改模块边界
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 既有 UI 可用性补强,无新需求
  requirement_refs: []
  context_refs:
    design: []
    impl: []
    accept: []
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-ui-cancel-height-unify/change.md
      depends_on: []
  evidence_ledger: false
---

# 弹窗取消语义补齐 + 输入框高度 SSOT

## 目标与边界

来自 2026-09-25-ui-coverage-input-audit 的 F5/F6 两项:

1. **F6**:PasswordGeneratorForm 无 Accept/Cancel(ESC 关不掉);SetupWizardForm 无 CancelButton(ESC 直接触发 FormClosing 确认,语义怪)。补齐键盘取消/确认语义。
2. **F5**:输入框高度四种口径(38/36/30/28)并存。抽 `FormFontPolicy.FieldHeight(Control)` 作为 SSOT(38 地板),六处漂移点收敛。

不做:F7 快捷键 SSOT、F8 未解锁引导(需用户取舍)、F2/F3 死代码(包 F)。

## 行为增量

- ADDED:PasswordGeneratorForm ESC 关闭(CancelButton);Enter 触发重新生成(AcceptButton)。
- ADDED:SetupWizardForm 显式"取消"按钮(完成前点击=确认退出流程,与 X 同语义;完成后自动隐藏);ESC 绑定该按钮。
- ADDED:FormFontPolicy.FieldHeight(Control) —— `Math.Max(DpiScale.V(c,38), RowStep(c))`。
- MODIFIED:全仓高度口径统一收口 FieldHeight——本地 38 公式 24+11 处机械替换(35 文件),六处漂移(36/30×5/28)语义升级为 38 地板;含锁屏外唯一显式 30 地板 ConnectionTreeControl 原生过滤框(其注释自证"与 38 地板同一语义")与 LogonScript AddStepDialog 小窗。行为等价(38 口径)或可观察变化即本包目的(高 DPI 侧栏对齐窗体)。
- REMOVED:无。

## 设计与契约

### 术语约定

- FieldHeight:输入框统一高度 = max(DPI 缩放 38px 地板, 字体行步进)。等价于现 17 窗体的本地 fieldH 公式,零行为变化。
- ESC 语义分级:可编辑弹窗 ESC=取消;向导 ESC=确认退出;查看型窗体 ESC=关闭。

### 决策与约束

- D1(生成器):AcceptButton=generateBtn(Enter=重新生成,高频操作);CancelButton=新增"关闭"钮(放历史框下方右侧,DialogResult.Cancel)——不加"确定"(生成器无提交概念)。ClientSize 自适应公式补按钮行高。
- D2(向导):新增 Ghost"取消"按钮在 _nextButton 左侧,Click 走既有 FormClosing 确认路径(Close() 触发);IsCompleted 后 Visible=false;CancelButton=取消钮(ESC 与按钮同路)。不再让 ESC 绕过确认(现状:无 CancelButton 时 ESC 直发 FormClosing,确认框兜底——行为保留,但显式按钮提供可见出路)。
- D3(高度 SSOT):FormFontPolicy.FieldHeight 新增;全部 20 处 `Math.Max(DpiScale.V(x,38), RowStep(x))` 本地公式替换为 FieldHeight 调用;六处漂移(36/30×5/28)语义升级为 38 地板——**可观察变化**:高 DPI 下侧栏内嵌对话框输入框从 45(30×1.5)变 57(38×1.5),与窗体一致,即本包目的;KeyBinding 36→38 差 2px,视觉不可辨。
- D4(风险豁免):LockedOverlayControl 30px 地板不收(锁屏覆盖层独立布局,输入框口径本就特例);ToolboxPanel/WelcomePanel 无输入框不涉。

### 名词与编排

- 编排:Step1 RED 驱动 → Step2 FieldHeight API+20 处替换 → Step3 生成器按钮 → Step4 向导按钮 → Step5 GREEN+合规。

## 验收契约

- S1 FormFontPolicy.FieldHeight 存在且签名 (Control)→int,公式含 DpiScale.V(…,38) 与 RowStep。
- S2 全仓 `Math.Max(DpiScale.V(` 后接 `38), FormFontPolicy.RowStep` 的本地公式清零(全部改走 FieldHeight);36/30/28 地板输入框清零。
- S3 PasswordGeneratorForm 有 AcceptButton 与 CancelButton 赋值及"关闭"按钮。
- S4 SetupWizardForm 有"取消"按钮 Click→Close()、CancelButton 赋值、IsCompleted 分支隐藏。
- S5 反向:ConnectionQuickJumpForm/ScannerCenterForm/查看型窗体不加 Cancel(豁免清单不回归);锁屏 30 地板保留。

## 执行计划

1. Step1 RED:驱动断言现状(FieldHeight 不存在、生成器无 Cancel 等)。
2. Step2:FieldHeight + 全仓替换(机械,逐文件 sed 校验)。
3. Step3:生成器关闭钮 + Accept/Cancel。
4. Step4:向导取消钮。
5. Step5:GREEN + brace balance + compliance impl。

## 执行证据 (impl 阶段追加)

**状态口径**:kind feature 扁平状态机,draft→in-progress(用户 standing「请继续」+ 完全 ACT 授权,跳过形式 approved,与包 B/C 同例)。
**环境约束**:无 dotnet,验证=静态驱动 18 checks + cs-balance 括号平衡,CI 仲裁。

- Step1 RED(15 FAIL/18):FieldHeight 不存在、生成器/向导无取消、漂移口径在——判据式 RED 成立。驱动自身一坑:ScannerCenter 65 行注释含"CancelButton"字样,裸词判据误红,改赋值形态正则(记为教训)。
- Step2 FieldHeight API 落地 FormFontPolicy.cs(Math.Max(DpiScale.V(c,38), RowStep(c)),注释引审计编号)。机械替换两轮:16 窗体 24 处 + Controls 扫尾 11 处=35 文件;六漂移点(30×5/36/28)逐一断言替换;锁屏 LockOverlayControl 30 地板按 D4 豁免保留;SnippetSearchPanel:247 的 30 地板是按钮高非输入框,保留。
- 二次扫尾发现 ConnectionTreeControl:66(原生 TextBox 过滤框 30 地板,注释自证"与 38 地板同一语义")与 LogonScriptPanel:275(AddStepDialog 小窗)为漏网,一并收口。替换后全仓 38 本地公式 grep 零残留;全部触达文件 using Gdterm.UI.Services 在位或同 namespace;16 文件括号平衡 ok。
- Step3 生成器:closeBtn(关闭,右对齐 130 宽)+AcceptButton=generateBtn+CancelButton=closeBtn;ClientSize 公式已含按钮行(y 已步进)。
- Step4 向导:_cancelButton 字段+Ghost 取消钮(在 next 左侧,间距 12)+Click→Close()(进既有 FormClosing 确认)+CancelButton 绑定+case 2 完成分支 Visible=false。
- Step5 GREEN:驱动 18 checks ALL OK exit 0。
- **契约修订(非静默扩权)**:二批机械替换扩散到 include 外 19 文件(纯 fieldH→FieldHeight 单行公式替换,无逻辑改动),contract.include 已回填 24 路径并在行为增量注明扩散;git status 与 include 逐一对账(见提交 diff)。
- 反向:豁免清单(QuickJump/ScannerCenter/锁屏/SnippetSearch 按钮高)S5 断言全过;无新 NuGet。

## 验收结果 (accept 阶段追加)

CI 0.1.336(commit 5c1faba)success 2026-09-25T14:16:58→14:18:58Z:单元 163/0、UI 冒烟 6/0、双门 ALL OK。35 文件高度 SSOT 收敛与两窗体取消语义编译/回归通过:

- S1-S2 ✓ FieldHeight 存在,全仓本地 38 公式零残留,36/30/28 漂移清零(驱动断言)。
- S3-S4 ✓ 生成器 Accept/Cancel+关闭钮,向导取消钮+完成后隐藏(驱动断言)。
- S5 ✓ 豁免清单(QuickJump/ScannerCenter/锁屏/按钮高 30)不回归。
- 可观察变化=设计目的本身:高 DPI 下侧栏内嵌对话框输入框与窗体同高;PwdGen/向导 ESC 可取消。留 Windows 实测确认观感。
### 合规复查口径说明

accept 复查(--phase accept)于本轮三包全部落盘后统一执行:git.diff.scope 列出的"包外文件"全部为同轮姊妹包(D/E/F 互照)的已提交文件,属跨包噪声;各包 impl 阶段合规在其自身窗口内为 pass(见执行证据),全仓最终状态 163/0 单测 + 6/0 冒烟 + 双 CI 门绿(0.1.335/336/337)为实际门禁。无真实越界文件。


