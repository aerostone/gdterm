# 全项目按钮盘点（2026-11-27）

本次范围包括 AntdUI 按钮、工具模块的原生按钮、动态工厂、侧栏关闭/专注退出和标签页自绘关闭命中区。

## 共因与修复

- `SizeActionButton` 旧紧凑分支把 AntdUI Padding 当内容留白，导致底色被缩小到文字附近；统一改为文字尺寸加库内预算，清零 Padding/WaveSize。
- 流式按钮间距和换行高度同步修复；页脚换行也保留行间距。
- 通知/传输中心改用流式按钮条，避免 Dock 忽略 Margin；终端键条预留更多菜单空间。
- 文件图标和专注退出按钮补键盘可达；原生工具按钮也纳入 DPI/字体尺寸策略。
- 欢迎页数据行、AI 建议命令按实际可用宽省略，完整文本保存在提示；点击仍使用原始值。
- 新打开的编辑器暴露出旧绝对布局下的标签/复选框重叠，修复了对应内容行和取消路径。

## 验证口径

`UiTreeDumper` 导出每个按钮的绘制框、GDI+ 文字宽高。`ui-tree-check` R18/R19 检查全部可见按钮的内容留白、实际相邻间隔；包括禁用态，页脚/入口另外受 C# 更严格断言约束。原有越界、重叠、键盘与自绘命中区检查保持。

`UiPageCatalog` 扩展常规及六组矩阵页面；`ButtonEditorsSmoke` 从真实入口打开嵌套编辑器，以 9/14pt 截图并取消，避免仅检查主页面。工具布局采集不运行网络命令；文件按钮使用本地 provider 检查共用界面。DPI 轴是注入系数，尚未代表实体显示器跨屏验证。

## 创建点清单

下列数量是源码创建点（工厂只计一次），不是运行时实例数量或截图数量；注释中的示例不计。

| 文件 | 创建点 | 处理/覆盖路径 |
| --- | ---: | --- |
| [NetworkDiagnosticsTool.cs](../src/Gdterm.Tools/Modules/NetworkDiagnosticsTool.cs) | 1 | 原生按钮内容留白 + 离线工具页面 |
| [ToolPanelHelper.cs](../src/Gdterm.Tools/ToolPanelHelper.cs) | 1 | 原生按钮内容留白 + 离线工具页面 |
| [AiChatPanel.cs](../src/Gdterm.UI/Controls/AiChatPanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [BatchCommandPanel.cs](../src/Gdterm.UI/Controls/BatchCommandPanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [BottomBarPanel.cs](../src/Gdterm.UI/Controls/BottomBarPanel.cs) | 8 | 终端专用排布 + 实际绘制/溢出检查 |
| [CommandHistoryPanel.cs](../src/Gdterm.UI/Controls/CommandHistoryPanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [CommandTemplatePanel.cs](../src/Gdterm.UI/Controls/CommandTemplatePanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [DangerousCommandDialog.cs](../src/Gdterm.UI/Controls/DangerousCommandDialog.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [EmptyStateView.cs](../src/Gdterm.UI/Controls/EmptyStateView.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [FilePaneControl.cs](../src/Gdterm.UI/Controls/FilePaneControl.cs) | 4 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [HealthMonitorPanel.cs](../src/Gdterm.UI/Controls/HealthMonitorPanel.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [HighlightRulePanel.cs](../src/Gdterm.UI/Controls/HighlightRulePanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [HostKeyDialog.cs](../src/Gdterm.UI/Controls/HostKeyDialog.cs) | 4 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [KeyBindingPanel.cs](../src/Gdterm.UI/Controls/KeyBindingPanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [ListShell.cs](../src/Gdterm.UI/Controls/ListShell.cs) | 2 | 公共列表工厂，排序图标与导出按钮 |
| [LockOverlayControl.cs](../src/Gdterm.UI/Controls/LockOverlayControl.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [LogonScriptPanel.cs](../src/Gdterm.UI/Controls/LogonScriptPanel.cs) | 9 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [MacroPanel.cs](../src/Gdterm.UI/Controls/MacroPanel.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [MultiChannelPanel.cs](../src/Gdterm.UI/Controls/MultiChannelPanel.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [NotificationCenterPanel.cs](../src/Gdterm.UI/Controls/NotificationCenterPanel.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [PortForwardPanel.cs](../src/Gdterm.UI/Controls/PortForwardPanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [RemoteMonitorPanel.cs](../src/Gdterm.UI/Controls/RemoteMonitorPanel.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SecretScanPanel.cs](../src/Gdterm.UI/Controls/SecretScanPanel.cs) | 5 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SessionBookmarksPanel.cs](../src/Gdterm.UI/Controls/SessionBookmarksPanel.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SnippetSearchPanel.cs](../src/Gdterm.UI/Controls/SnippetSearchPanel.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [TableShell.cs](../src/Gdterm.UI/Controls/TableShell.cs) | 2 | 公共列表工厂，排序图标与导出按钮 |
| [TerminalSearchBar.cs](../src/Gdterm.UI/Controls/TerminalSearchBar.cs) | 1 | 终端专用排布 + 实际绘制/溢出检查 |
| [TextPromptDialog.cs](../src/Gdterm.UI/Controls/TextPromptDialog.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [TransferCenterPanel.cs](../src/Gdterm.UI/Controls/TransferCenterPanel.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [TransferProgressDialog.cs](../src/Gdterm.UI/Controls/TransferProgressDialog.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [WelcomePanel.cs](../src/Gdterm.UI/Controls/WelcomePanel.cs) | 2 | 快捷入口 + 有数据行 + 窄宽省略 |
| [AddOtpForm.cs](../src/Gdterm.UI/Forms/AddOtpForm.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [AiSettingsForm.cs](../src/Gdterm.UI/Forms/AiSettingsForm.cs) | 4 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [AppearanceSettingsForm.cs](../src/Gdterm.UI/Forms/AppearanceSettingsForm.cs) | 5 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [ChangeMasterPasswordForm.cs](../src/Gdterm.UI/Forms/ChangeMasterPasswordForm.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [ConnectionDialog.cs](../src/Gdterm.UI/Forms/ConnectionDialog.cs) | 4 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [DangerousCommandConfigForm.cs](../src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs) | 7 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [ImportPreviewForm.cs](../src/Gdterm.UI/Forms/ImportPreviewForm.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [KeePassEntryPicker.cs](../src/Gdterm.UI/Forms/KeePassEntryPicker.cs) | 4 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [KeePassManagerForm.cs](../src/Gdterm.UI/Forms/KeePassManagerForm.cs) | 5 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [KeePassUnlockForm.cs](../src/Gdterm.UI/Forms/KeePassUnlockForm.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [LogViewerForm.cs](../src/Gdterm.UI/Forms/LogViewerForm.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [OtpManagerForm.cs](../src/Gdterm.UI/Forms/OtpManagerForm.cs) | 4 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [PasswordGeneratorForm.cs](../src/Gdterm.UI/Forms/PasswordGeneratorForm.cs) | 5 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [PasswordHealthForm.cs](../src/Gdterm.UI/Forms/PasswordHealthForm.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [QuickCommandEditorForm.cs](../src/Gdterm.UI/Forms/QuickCommandEditorForm.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [ScannerCenterForm.cs](../src/Gdterm.UI/Forms/ScannerCenterForm.cs) | 4 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SessionRecordingsForm.cs](../src/Gdterm.UI/Forms/SessionRecordingsForm.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SetupWizardForm.cs](../src/Gdterm.UI/Forms/SetupWizardForm.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SftpPermissionForm.cs](../src/Gdterm.UI/Forms/SftpPermissionForm.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SshKeyManagerForm.cs](../src/Gdterm.UI/Forms/SshKeyManagerForm.cs) | 3 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [TerminalPasteDialog.cs](../src/Gdterm.UI/Forms/TerminalPasteDialog.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [TerminalPasteProgressDialog.cs](../src/Gdterm.UI/Forms/TerminalPasteProgressDialog.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [TrashForm.cs](../src/Gdterm.UI/Forms/TrashForm.cs) | 1 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [MasterPasswordPrompt.cs](../src/Gdterm.UI/Services/MasterPasswordPrompt.cs) | 2 | 公共尺寸/页脚策略 + 所属页面或嵌套编辑器 |
| [SidePanelHost.cs](../src/Gdterm.UI/Services/SidePanelHost.cs) | 1 | 侧栏关闭按钮 |
| [ToolsDialogsLauncher.cs](../src/Gdterm.UI/Services/ToolsDialogsLauncher.cs) | 2 | 更新结果弹窗，离线输入 |
| [UiA11y.cs](../src/Gdterm.UI/Services/UiA11y.cs) | 1 | 图标公共工厂（目前无调用者），接入公共尺寸策略 |
| [ViewModeController.cs](../src/Gdterm.UI/Services/ViewModeController.cs) | 1 | 专注退出按钮 + 键盘可达 |

共 59 个源文件、153 个创建点。自绘标签关闭按钮另通过 `TabChromePainter` / `IHitRegionProvider` 的命中矩形检查，不能计作 AntdUI 实例。

## Windows 证据

[Windows 验证 46009705271](https://github.com/aerostone/gdterm/actions/runs/46009705271)（80f578d）全绿：构建、单元测试、全部控件树、像素基线、56 页 × 6 组布局矩阵、性能和静态检查。嵌套编辑器另以 9/14pt 真实打开并取消。截图证据随该次 CI 的 ci-evidence 制品保存。

常规冒烟共 101 张截图，98 个含按钮场景记录了 357 次可见按钮测量；矩阵另有 336 张截图，单元测试 1,017 项全部通过。同一按钮在不同字号、状态或场景下会重复测量。

框架内部的分页箭头、窗口标题栏控件由 AntdUI/WinForms 自身绘制，不使用项目 `SizeActionButton`，随所属页面截图检查；本次未重写第三方绘制。
