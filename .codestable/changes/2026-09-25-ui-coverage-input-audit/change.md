---
doc_type: change
kind: audit
slug: 2026-09-25-ui-coverage-input-audit
status: closed
mode: standard
risk:
  level: low
  reasons: []
contract:
  include:
    - .codestable/changes/2026-09-25-ui-coverage-input-audit/**
  exclude: []
  preexisting_changes: []
  baseline:
    git_head: ""
    dirty_hashes: {}
  architecture_impact: unchanged
  architecture_reason: 只读审计,不改架构
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 审计只读,不修改需求文档
  requirement_refs: []
  context_refs:
    design: []
    impl: []
    accept: []
  artifacts:
    - id: audit-report
      path: .codestable/changes/2026-09-25-ui-coverage-input-audit/change.md
      depends_on: []
  evidence_ledger: false
---

# UI 覆盖与输入框一致性审计(2026-09-25)

## 范围

用户命题四项:①每个界面都有功能 ②每个界面的布局都有提交和取消 ③每个功能界面的子功能都有正常界面与功能(递归) ④输入框高度一致、长度自适应或统一。

- 界面全集:src/Gdterm.UI/Forms 17 窗体 + Controls 37 控件/面板 + Services 侧栏工厂/宿主/启动器 ≈ 54 文件
- 审计方式:standard 静态审计(主 agent),以菜单构建器(MainFormMenuBuilder.Callbacks 43 项)为功能权威清单,静态台账 + 定点回读
- 未运行程序(Linux 无 dotnet),动态行为未验证

## Findings

### P1(high) F1 LogonScriptPanel 步骤上移/下移按钮无 Click 接线——死按钮

- 位置:`src/Gdterm.UI/Controls/LogonScriptPanel.cs:184-186`
- 证据:btnUp("↑")/btnDown("↓") 与 btnAddStep/btnDelStep 一起 AddRange 进步骤编辑对话框,但全文件对 btnUp/btnDown 零 Click 订阅(grep "btnUp|btnDown" 全文仅 3 行:声明、声明、AddRange);btnAddStep.Click(205)/btnDelStep.Click(210) 均已接线,唯独排序两钮漏接。
- 影响:登录脚本步骤无法排序——界面上呈现可点击,实际无任何效果,且用户无错误反馈。子功能"步骤排序"有界面无功能,正中本次审计命题③。
- 建议:接 LogonScript 步骤交换逻辑(cs-issue)。

### P1(high) F2 SftpBrowserPanel 640 行零实例——被 SftpDualPanePanel 取代后未删

- 位置:`src/Gdterm.UI/Controls/SftpBrowserPanel.cs`(全文件 640 行)
- 证据:全仓 `new ...SftpBrowserPanel` 0 处;唯一两处引用是 FilePaneControl.cs:504/507 的注释("SftpBrowserPanel 经 PreviewBoxShim 调用")。SFTP 实际入口是 ProtocolTabOpener.cs:460 `new SftpDualPanePanel(...)`(文件→SFTP 浏览器菜单 → ConnectionOpenCoordinator.OpenSftpFromActive:87)。
- 影响:640 行功能完整的旧面板(同步上传/下载、PreviewBox)躺在编译集里,csproj:162 仍参与编译;误导维护与搜索,属死 UI。
- 建议:确认后删除(删除前另行确认,cs-refactor)。

### P1(high) F3 DarkMenuRenderer 死类

- 位置:`src/Gdterm.UI/Controls/SharedMenuTip.cs:22-41`
- 证据:全仓 `DarkMenuRenderer` 引用 0 处(SharedMenuTip.cs 自身之外);BottomBarPanel.cs:247/511 的 ContextMenuStrip 只设 BackColor,未挂 Renderer。注释自称"BottomBarPanel 的 …/右键菜单仍在用"与事实不符(ToolTipText2 扩展确实 10 处在用,该文件不是死文件——但 DarkMenuRenderer 类本体已死)。
- 影响:同 F2,死代码混入编译集;类内注释陈述过时事实。
- 建议:删类、修注释(cs-refactor)。

### P1(high) F4 主密码验证框 28px 输入框 + 无 CancelButton + 裸固定布局

- 位置:`src/Gdterm.UI/Services/MasterPasswordPrompt.cs:47-60`
- 证据:pwdBox `Size = DpiScale.S(dialog, 335, 28)` —— 全仓输入框高度口径仅此一处 28(其余见 F5 台账:38/36/30);dialog 是手写 `new Form()`(N4 证据),只设 AcceptButton=okBtn(:86),无 CancelButton,ESC 不可取消(对话框只能点"验证"或标题栏 X);标签/按钮全部绝对坐标。
- 影响:①高度与全仓统一口径(≥38 或 RowStep 地板)不一致,且 28px 在大字号下裁字;②安全确认类弹窗没有取消语义(点 X 语义上等同取消但无键盘路径);③违反 UI-SCALING-CONVENTIONS "输入控件只给宽度不给高度"精神——虽有 DpiScale 缩放,但硬编码高度本身就是该文档 135 行点名要消除的形态。
- 建议:fieldH 对齐 Math.Max(DpiScale.V(38), RowStep) + CancelButton(cs-issue 或随 cs-refactor)。

### P2(high) F5 输入框高度 4 种口径并存(命题④核心结论)

全仓 80 处 AntdUI.Input/InputNumber 创建点的高度口径台账:

| 口径 | 定义 | 使用文件 |
|---|---|---|
| 38px 地板(主流,正确) | `Math.Max(DpiScale.V(this,38), FormFontPolicy.RowStep(this))` | 全部 17 Forms + AiChatPanel + CommandTemplatePanel + TransferProgressDialog(20 文件) |
| 36px | 同式但地板 36 | KeyBindingPanel.cs:317 |
| 30px | 同式但地板 30 | HighlightRulePanel:181 / LogonScriptPanel:154 / PortForwardPanel:127 / SessionBookmarksPanel:283 / SnippetSearchPanel:230(5 面板) |
| 28px 硬编码 | `DpiScale.S(dialog,335,28)` | MasterPasswordPrompt.cs:47 |

- 位置证据:F5 表格各 file:line。
- 影响:同一屏并发打开"终端菜单侧栏面板(30px)"与"任一窗体(38px)"时输入框高度肉眼不一致;侧栏面板互相之间一致、窗体互相之间一致,断层在"窗体 vs 侧栏面板"之间。docs/UI-SCALING-CONVENTIONS.md:30 规定"只给宽度不给高度(高度由字体决定)",四种口径都是"给高度"的实现,但地板值没有 SSOT。
- 建议:抽 `FormFontPolicy.FieldHeight(Control)`(38 地板)替换四处漂移;30px 五处与 28px 一处为存量,cs-refactor 收敛。

### P2(high) F6 提交/取消绑定台账(命题②结论)——2 窗体缺、3 窗体语义豁免

| 窗体 | Accept | Cancel | 判定 |
|---|---|---|---|
| AiSettings/Appearance/ChangeMasterPwd/Connection/DangerousCmd(内嵌 2 对话框)/KeePassEntryPicker/KeePassManager(内嵌 TextInputForm:643-644)/KeePassUnlock/QuickCommandEditor/SftpPermission | ✓ | ✓ | 合规 |
| SshKeyManagerForm | — | CancelButton=关闭(:121) | 合规(查看型) |
| PasswordHealthForm | — | CancelButton=关闭 | 合规(报告型) |
| ScannerCenterForm | AcceptButton=运行 | — | 豁免(ESC 已接 KeyPreview 双态守卫,2026-09-25 包) |
| **PasswordGeneratorForm** | — | — | **缺**:4 按钮全接 Click 但无 Accept/Cancel;FormBorderStyle.FixedDialog+MaximizeBox=false,关闭仅标题栏 X;ESC 无效 |
| **SetupWizardForm** | — | — | **半缺**:FormClosing 拦截+确认(:175-187),但无 CancelButton,ESC 直接弹确认框语义怪;_nextButton 独扛三步 |
| MasterPasswordPrompt(手写 Form) | ✓ | — | 缺 Cancel(F4 已并) |
| ConnectionQuickJumpForm | — | — | 豁免(命令面板型:Enter 执行/Esc 取消/双击确认,三处 Escape 处理) |
| DangerousCommandDialog | ✓ | ✓("取消 (Esc)" :155-160) | 合规(倒计时防误触设计) |
| TransferProgressDialog | — | — | 合规(单钮"取消"→完成后变"关闭") |

- 建议:PasswordGeneratorForm 补 CancelButton(ESC 关)与 Enter=重新生成;SetupWizardForm 评估给"取消"显式语义(cs-refactor/feat)。

### P2(medium) F7 帮助菜单"快捷键列表"与实际不符 + ESC 语义未列入

- 位置:`src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:166-186`
- 证据:ShowHotkeysHelp 硬编码文本。与 MainFormMenuBuilder/attention.md 比对:列出的 Ctrl+Shift+M(tmux)、Alt+8 均在;但 Ctrl+Shift+L 文本写"切换连接面板"而菜单是 Ctrl+Shift+K 快速跳转+视图菜单切换面板(Ctrl+Shift+L 在快捷键正文里另有绑定,两处描述口径需对拍);新增的扫描中心 ESC/密码生成器等无键位提示。文本硬编码无 SSOT,与菜单实际绑定靠人肉同步。
- 影响:低危但属"界面信息与功能漂移"类。
- 建议:从菜单 ShortcutKeys 反射生成或建立对拍检查(cs-refactor)。

### P2(medium) F8 菜单入口三连弹"密码库未解锁" MessageBox 无行动路径

- 位置:`src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:37-41,55-59,72-76`(OpenKeePassManager/OpenPasswordHealth/OpenSshKeyManager 三处同构)
- 证据:`if (_keepassService == null || !_keepassService.IsUnlocked) { MessageBox.Show("密码库未解锁"...) return; }` —— 用户点了菜单只得到一句提示,界面不给"去解锁"动作(尽管 KeePassUnlockForm 存在且解锁流程完整)。
- 影响:功能可达但 UX 断路:未解锁场景下三个菜单项的实际功能为零,且不引导恢复。属命题①"有界面无功能"的降级形态(有意为之,但缺出路)。
- 建议:提示框加"立即解锁"按钮直达 KeePassUnlockForm(cs-feat)。

### P2(low) F9 AppearanceSettingsForm.MakeNumber 裸 86px 宽

- 位置:`src/Gdterm.UI/Forms/AppearanceSettingsForm.cs:239`
- 证据:`Size = new Size(86, _fieldHeight)` —— 数字输入框宽度 86 不走 DpiScale.V(高度走了)。UI-SCALING-CONVENTIONS:135 明确"固定高度数值必须走 DpiScale.V,宽高不可整体豁免";宽度同为固定数值,按同条规则也应缩放。
- 影响:DPI>100% 时数字框相对变窄,4 位数(如 scrollback 2000)在放大字号下可能显示拥挤。
- 建议:`new Size(DpiScale.V(this,86), _fieldHeight)`(cs-refactor,一行)。

### 零发现维度(正面结论)

- **命题①菜单→界面可达性:全部通过。** 43 个菜单 handler 在 MainForm.cs:442-510 一处对象初始化器全部接线,无空委托;Toolbox/SecretScan/History/AiChat/CommandTemplate/Highlight/KeyBinding/LogonScript/Bookmarks 九个工厂在依赖缺失时返回 Unavailable 标签(SidePanelFactory:372-377)——降级有文案,不算无功能。
- **命题③子功能→动作落地:除 F1 外全部通过。** 38 个含按钮文件逐一比对"按钮创建数 vs Click 订阅数",差异点全部回读核实为合法形态(SftpPermissionForm 用 DialogResult 声明式绑定:99-120;SnippetSearchPanel 内嵌参数对话框 AcceptButton/Cancel:248-252;LogonScript 内嵌对话框步骤按钮 205/210 已接,唯独 F1 两钮)。宏/多通道/底栏等 Click 订阅数高于按钮数均因 ListView/键盘事件,非死按钮。
- **无原生 TextBox 残留**(全控件化合规,grep new TextBox 0 处);ConnectionDialog 输入宽度走 TableLayout Percent 100(自适应正范本,143-150)。

## Verification Evidence

- 实际读取:菜单构建器 MainFormMenuBuilder.cs 全文(249 行);SidePanelFactory.cs 全文(380);ToolsDialogsLauncher.cs 全文(285);SidePanelHost.cs 前 80 行;定点回读 LogonScriptPanel(186 行级)、PasswordGeneratorForm/SetupWizardForm/ConnectionQuickJumpForm/MasterPasswordPrompt/SftpPermissionForm/SnippetSearchPanel/HighlightRulePanel/ToolboxPanel/AppearanceSettingsForm/QuickCommandEditorForm/ConnectionDialog 关键段;MainForm.cs:442-560。
- 脚本化台账:54 文件实例化/引用矩阵(全限定名+对象初始化器双式);20 窗体基类矩阵(19×AntdUI.Window+1×Form);38 文件按钮-vs-Click 差值表;28 处 fieldH 定义口径表;80 处 Input 创建点尺寸策略;21 文件 AcceptButton/CancelButton 台账。
- agent 使用:主 agent 单体扫描,未派子 agent(54 文件在预算内,切片按菜单链完成)。
- 架构文档对照:docs/DESIGN-LANGUAGE.md(输入样式/高度行)、docs/UI-SCALING-CONVENTIONS.md:30/135(输入控件高度规范)——F4/F5/F9 的"违反规范"判定以此为据。
- 未验证:运行时行为(对话框实际弹出、ESC 实际效果)——本机无 dotnet,与既往审计同等约束。
- 去重:F2 与 F3 同根因(被替代后未清)保持分列(文件不同、处置建议不同)。

## 下一步(不在本审计内执行)

1. F1 死按钮 → cs-issue(功能性缺陷,一行接线)
2. F2/F3 死代码 → cs-refactor(删除前二次确认)
3. F4/F5/F9 输入框口径 → cs-refactor(FieldHeight SSOT 收敛)
4. F6 PasswordGenerator/SetupWizard 补 Cancel → cs-refactor
5. F7 快捷键 SSOT、F8 未解锁引导 → cs-feat
