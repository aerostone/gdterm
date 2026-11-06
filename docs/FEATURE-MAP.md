# 功能大纲 × 页面契约矩阵

> 生成物：`python3 tools/feature-map.py --write`（勿手改）。契约数据在 `docs/feature-contracts.json`。
> 机制说明见 §0；判定口径见 §2。

## 0. 这套机制在判什么

布局门（ui-ux-lint / ui-tree-check）判「画得对不对」；**这张表判「做没做完」**。
三条来自业界实践的判据：

1. **先列全页面**（IA audit）：入口树从代码自动抽取（菜单 → 回调 → 工厂方法 → 页面类 → 源文件），
   不靠人回忆，所以「有页面没入口」和「有入口没页面」都会被点名。
2. **状态清单**：一个页面最少有 空 / 零匹配 / 加载中 / 失败 / 无权限 / 不可逆确认 六态。
   只画 happy path 的页面，设计只完成了三分之一。
3. **列表页 DoD**：能看（列表）、能找（搜索/过滤）、能挑（多选批量）、能走（分页/虚拟滚动）、
   能带出（导出）、能回来（撤销/回收站）。缺哪个就写哪个。

能力格的三档判定——**判不了的必须显式标人工，不许假装有门**：

| 档 | 含义 | 谁来判 |
|---|---|---|
| `ok` | 源码里有该类能力的证据（组件/API 调用） | 正则证据（本工具） |
| `manual` | 有实现但证据不在源码特征里 | 人工在 `manual_ok` 签字 |
| `debt` | 已知缺失，登记在 `known_gaps`（棘轮：只减不增） | 人工 |
| `gap` | **未发现证据且未登记** —— 这就是待办 | 本工具 |

## 1. 入口大纲（L1 → L2）

共 6 个一级菜单 / 53 个二级入口。

### 文件(&F)

| L2 入口 | 页面/类 | 源文件 | 契约 |
|---|---|---|---|
| 新建连接(&N)... | ConnectionDialog | `src/Gdterm.UI/Forms/ConnectionDialog.cs` | ✅ `page.ConnectionDialog` |
| 快速跳转连接 Ctrl+Shift+K | ConnectionQuickJumpForm | `src/Gdterm.UI/Controls/ConnectionQuickJumpForm.cs` | ✅ `page.ConnectionQuickJumpForm` |
| 本地终端(&L) | — | — | （动作，无需页面） |
| SFTP 浏览器(&S) | — | — | （动作，无需页面） |
| 导入连接(&I)... | — | — | （动作，无需页面） |
| 导出连接(&E)... | — | — | （动作，无需页面） |
| 退出(&X) | — | — | （动作，无需页面） |

### 连接(&C)

| L2 入口 | 页面/类 | 源文件 | 契约 |
|---|---|---|---|
| 重连当前标签 Ctrl+Shift+R | — | — | （动作，无需页面） |
| 关闭当前标签 Ctrl+Shift+W | — | — | （动作，无需页面） |
| 书签 / 最近连接(&B) | SessionBookmarksPanel | `src/Gdterm.UI/Controls/SessionBookmarksPanel.cs` | ✅ `page.SessionBookmarksPanel` |

### 视图(&V)

| L2 入口 | 页面/类 | 源文件 | 契约 |
|---|---|---|---|
| 水平分割 | — | — | （动作，无需页面） |
| 垂直分割 | — | — | （动作，无需页面） |
| 快捷命令栏 | — | — | （动作，无需页面） |
| 标准视图(&S) | — | — | （动作，无需页面） |
| 专注模式(&F) | — | — | （动作，无需页面） |
| 紧凑模式(&C) | — | — | （动作，无需页面） |
| 切换连接面板(&T) | — | — | （动作，无需页面） |
| 自动隐藏连接面板(&P) | — | — | （动作，无需页面） |
| tmux 快捷面板 | — | — | （动作，无需页面） |

### 终端(&E)

| L2 入口 | 页面/类 | 源文件 | 契约 |
|---|---|---|---|
| 查找 Ctrl+Shift+F | — | — | （动作，无需页面） |
| 片段搜索 Ctrl+Shift+P | — | — | （动作，无需页面） |
| 高亮规则 | HighlightRulePanel | `src/Gdterm.UI/Controls/HighlightRulePanel.cs` | ✅ `page.HighlightRulePanel` |
| 登录脚本 | LogonScriptPanel | `src/Gdterm.UI/Controls/LogonScriptPanel.cs` | ✅ `page.LogonScriptPanel` |
| 多通道广播 | MultiChannelPanel | `src/Gdterm.UI/Controls/MultiChannelPanel.cs` | ✅ `page.MultiChannelPanel` |
| 批量命令 | BatchCommandPanel | `src/Gdterm.UI/Controls/BatchCommandPanel.cs` | ✅ `page.BatchCommandPanel` |
| 宏录制 Ctrl+Shift+H | MacroPanel | `src/Gdterm.UI/Controls/MacroPanel.cs` | ✅ `page.MacroPanel` |
| 命令历史 | CommandHistoryPanel | `src/Gdterm.UI/Controls/CommandHistoryPanel.cs` | ✅ `page.CommandHistoryPanel` |
| AI 助手 Ctrl+Shift+G | AiChatPanel | `src/Gdterm.UI/Controls/AiChatPanel.cs` | ✅ `page.AiChatPanel` |
| 命令模板库 | CommandTemplatePanel | `src/Gdterm.UI/Controls/CommandTemplatePanel.cs` | ✅ `page.CommandTemplatePanel` |
| 健康监控 | HealthMonitorPanel | `src/Gdterm.UI/Controls/HealthMonitorPanel.cs` | ✅ `page.HealthMonitorPanel` |
| 主机资源（远端 CPU/内存） | RemoteMonitorPanel | `src/Gdterm.UI/Controls/RemoteMonitorPanel.cs` | ✅ `page.RemoteMonitorPanel` |
| 端口转发 | PortForwardPanel | `src/Gdterm.UI/Controls/PortForwardPanel.cs` | ✅ `page.PortForwardPanel` |

### 工具(&T)

| L2 入口 | 页面/类 | 源文件 | 契约 |
|---|---|---|---|
| 运维工具箱 | ToolboxPanel | `src/Gdterm.UI/Controls/ToolboxPanel.cs` | ✅ `page.ToolboxPanel` |
| 敏感信息扫描 | SecretScanPanel | `src/Gdterm.UI/Controls/SecretScanPanel.cs` | ✅ `page.SecretScanPanel` |
| 扫描中心（插件） | ScannerCenterForm | `src/Gdterm.UI/Forms/ScannerCenterForm.cs` | ✅ `page.ScannerCenterForm` |
| 传输中心 | TransferCenterPanel | `src/Gdterm.UI/Controls/TransferCenterPanel.cs` | ✅ `page.TransferCenterPanel` |
| 通知中心 | NotificationCenterPanel | `src/Gdterm.UI/Controls/NotificationCenterPanel.cs` | ✅ `page.NotificationCenterPanel` |
| 密码库管理(&K) | KeePassManagerForm | `src/Gdterm.UI/Forms/KeePassManagerForm.cs` | ✅ `page.KeePassManagerForm` |
| 密码健康报告(&H) | PasswordHealthForm | `src/Gdterm.UI/Forms/PasswordHealthForm.cs` | ✅ `page.PasswordHealthForm` |
| 密码生成器(&G) | PasswordGeneratorForm | `src/Gdterm.UI/Forms/PasswordGeneratorForm.cs` | ✅ `page.PasswordGeneratorForm` |
| SSH 密钥管理 | SshKeyManagerForm | `src/Gdterm.UI/Forms/SshKeyManagerForm.cs` | ✅ `page.SshKeyManagerForm` |
| 修改主密码(&M)... | ChangeMasterPasswordForm | `src/Gdterm.UI/Forms/ChangeMasterPasswordForm.cs` | ✅ `page.ChangeMasterPasswordForm` |
| OTP 动态码管理(&T)… | OtpManagerForm | `src/Gdterm.UI/Forms/OtpManagerForm.cs` | ✅ `page.OtpManagerForm` |
| 外观设置(&R)... | AppearanceSettingsForm | `src/Gdterm.UI/Forms/AppearanceSettingsForm.cs` | ✅ `page.AppearanceSettingsForm` |
| AI 助手设置(&I) | AiSettingsForm | `src/Gdterm.UI/Forms/AiSettingsForm.cs` | ✅ `page.AiSettingsForm` |
| 危险命令规则(&D) | DangerousCommandConfigForm | `src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs` | ✅ `page.DangerousCommandConfigForm` |
| 快捷键绑定 | KeyBindingPanel | `src/Gdterm.UI/Controls/KeyBindingPanel.cs` | ✅ `page.KeyBindingPanel` |
| 调试模式(&D) | — | — | （动作，无需页面） |

### 帮助(&H)

| L2 入口 | 页面/类 | 源文件 | 契约 |
|---|---|---|---|
| 快捷键列表 | — | — | （动作，无需页面） |
| 检查更新… | PasswordGeneratorForm | `src/Gdterm.UI/Forms/PasswordGeneratorForm.cs` | ✅ `page.PasswordGeneratorForm` |
| 打开日志文件夹 | — | — | （动作，无需页面） |
| 导出诊断包… | — | — | （动作，无需页面） |
| 关于 gdterm | — | — | （动作，无需页面） |

## 2. 完备性矩阵（按缺口排序，最烂的在最上面）

43 个页面，其中 29 个存在未登记缺口。

| 页面 | 一级菜单 | 类型 | 完备度 | 缺口 | dump |
|---|---|---|---|---|---|
| AI 助手 `page.AiChatPanel` | 终端 > AI 助手 | panel | 0% (0/1) | 空态引导 | — |
| 健康监控 `page.HealthMonitorPanel` | 终端 > 健康监控 | monitor | 0% (0/2) | 加载中/忙碌、失败提示 | — |
| 密码健康报告 `page.PasswordHealthForm` | 工具 > 密码健康报告 | list | 0% (0/4) | 列表/数据展示、空态引导、失败提示、回车/双击激活 | password-health | |
| 主机资源监控 `page.RemoteMonitorPanel` | 终端 > 主机资源（远端 CPU/内存） | monitor | 0% (0/2) | 加载中/忙碌、失败提示 | — |
| 传输进度 `page.TransferProgressDialog` | SFTP > 上传/下载 | progress | 0% (0/2) | 加载中/忙碌、失败提示 | transfer-progress | |
| SFTP 双栏 `page.SftpDualPanePanel` | 文件 > SFTP 浏览器 | list | 25% (1/4) | 列表/数据展示、空态引导、回车/双击激活 | — |
| SSH 密钥管理 `page.SshKeyManagerForm` | 工具 > SSH 密钥管理 | list | 25% (1/4) | 列表/数据展示、空态引导、回车/双击激活 | sshkey-manager | |
| 传输中心 `page.TransferCenterPanel` | 工具 > 传输中心 | list | 25% (1/4) | 空态引导、失败提示、回车/双击激活 | — |
| 批量命令 `page.BatchCommandPanel` | 终端 > 批量命令 | editor | 33% (1/3) | 输入校验、配置持久化 | — |
| 命令面板 `page.CommandPaletteForm` | Ctrl+Shift+P 风格入口 | list | 50% (2/4) | 空态引导、失败提示 | — |
| 命令模板库 `page.CommandTemplatePanel` | 终端 > 命令模板库 | list | 50% (2/4) | 空态引导、失败提示 | — |
| 连接导入/导出 `page.ConnectionImportExport` | 文件 > 导入连接 / 导出连接 | input | 50% (1/2) | 键盘/快捷键 | — |
| 快速跳转 `page.ConnectionQuickJumpForm` | 文件 > 快速跳转连接 | list | 50% (2/4) | 空态引导、失败提示 | — |
| 密码库条目选择 `page.KeePassEntryPicker` | 密码库 > 选择条目 | list | 50% (2/4) | 列表/数据展示、空态引导 | keepass-picker | |
| 密码库管理 `page.KeePassManagerForm` | 工具 > 密码库管理 | list | 50% (2/4) | 列表/数据展示、空态引导 | keepass-manager,keepass-manager-empty | 有数据 / 空态两态 |
| 密码库解锁 `page.KeePassUnlockForm` | 密码库 > 解锁 | input | 50% (1/2) | 输入校验 | keepass-unlock | |
| 多通道广播 `page.MultiChannelPanel` | 终端 > 多通道广播 | list | 50% (2/4) | 空态引导、失败提示 | — |
| 通知中心 `page.NotificationCenterPanel` | 工具 > 通知中心 | list | 50% (2/4) | 失败提示、回车/双击激活 | — |
| SFTP 权限修改 `page.SftpPermissionForm` | SFTP > 右键 > 权限 | input | 50% (1/2) | 输入校验 | — |
| 外观设置 `page.AppearanceSettingsForm` | 工具 > 外观设置 | settings | 66% (2/3) | 输入校验 | appearance-settings | |
| 危险命令规则 `page.DangerousCommandConfigForm` | 工具 > 危险命令规则 | list | 75% (3/4) | 空态引导 | dangerous-cmd | |
| 文件面板（单栏） `page.FilePaneControl` | SFTP > 单栏 | list | 75% (3/4) | 空态引导 | — |
| 高亮规则 `page.HighlightRulePanel` | 终端 > 高亮规则 | list | 75% (3/4) | 空态引导 | — |
| 宏录制 `page.MacroPanel` | 终端 > 宏录制 | list | 75% (3/4) | 空态引导 | — |
| OTP 动态码管理 `page.OtpManagerForm` | 工具 > OTP 动态码管理 | list | 75% (3/4) | 空态引导 | otp-manager | B1 OTP manager; smoke 建 tmp 空库 dump，条目态需人工过 |
| 端口转发 `page.PortForwardPanel` | 终端 > 端口转发 | list | 75% (3/4) | 回车/双击激活 | port-forward | 侧栏常驻（审计 C7-2 入 dump） |
| 扫描中心 `page.ScannerCenterForm` | 工具 > 扫描中心（插件） | list | 75% (3/4) | 空态引导 | scanner-center | |
| 敏感信息扫描 `page.SecretScanPanel` | 工具 > 敏感信息扫描 | list | 75% (3/4) | 空态引导 | secret-scan | 侧栏常驻（审计 C7-2 入 dump） |
| 书签 / 最近连接 `page.SessionBookmarksPanel` | 连接 > 书签 / 最近连接 | list | 75% (3/4) | 空态引导 | — |
| 添加 OTP `page.AddOtpForm` | OTP > 新增 | input | 100% (2/2) | — | add-otp | B1 添加 OTP 对话框 |
| AI 助手设置 `page.AiSettingsForm` | 工具 > AI 助手设置 | settings | 100% (3/3) | — | ai-settings | |
| 修改主密码 `page.ChangeMasterPasswordForm` | 工具 > 修改主密码 | input | 100% (2/2) | — | change-masterpwd | |
| 命令历史 `page.CommandHistoryPanel` | 终端 > 命令历史 | list | 100% (4/4) | — | command-history | 侧栏常驻（审计 C7-2 入 dump） |
| 连接属性 `page.ConnectionDialog` | 文件 > 新建连接 | settings | 100% (3/3) | — | connection-ssh,connection-rdp,connection-serial | 三协议 + 高级区展开 |
| 连接树 `page.ConnectionTreeControl` | 主窗左侧 | panel | 100% (0/0) | — | — |
| 危险命令确认 `page.DangerousCommandDialog` | 终端 > 命中危险命令 | dialog | 100% (1/1) | — | — |
| 快捷键绑定 `page.KeyBindingPanel` | 工具 > 快捷键绑定 | list | 100% (3/3) | — | key-binding | 侧栏常驻（审计 C7-2 入 dump） |
| 登录脚本 `page.LogonScriptPanel` | 终端 > 登录脚本 | editor | 100% (3/3) | — | logon-script | 侧栏常驻（审计 C7-2 入 dump） |
| 密码生成器 `page.PasswordGeneratorForm` | 工具 > 密码生成器 | input | 100% (2/2) | — | pwd-generator | |
| 快捷命令编辑 `page.QuickCommandEditorForm` | 快捷命令栏 > 编辑 | input | 100% (2/2) | — | quickcmd-editor | |
| 首次启动向导 `page.SetupWizardForm` | 首启 | wizard | 100% (3/3) | — | setup-wizard | |
| 运维工具箱 `page.ToolboxPanel` | 工具 > 运维工具箱 | panel | 100% (0/0) | — | toolbox | 侧栏常驻（审计 C7-2 入 dump） |
| 欢迎页 `page.WelcomePanel` | mainform > 空标签页 | panel | 100% (0/0) | — | — |

## 3. 待补清单（按页面展开）

### AI 助手（page.AiChatPanel，0%）

- 源文件：`src/Gdterm.UI/Controls/AiChatPanel.cs`
- [ ] **空态引导**（empty）

### 健康监控（page.HealthMonitorPanel，0%）

- 源文件：`src/Gdterm.UI/Controls/HealthMonitorPanel.cs`
- [ ] **加载中/忙碌**（loading）
- [ ] **失败提示**（error）

### 密码健康报告（page.PasswordHealthForm，0%）

- 源文件：`src/Gdterm.UI/Forms/PasswordHealthForm.cs`
- 结构 dump：`password-health |`
- [ ] **列表/数据展示**（list）
- [ ] **空态引导**（empty）
- [ ] **失败提示**（error）
- [ ] **回车/双击激活**（activation）

### 主机资源监控（page.RemoteMonitorPanel，0%）

- 源文件：`src/Gdterm.UI/Controls/RemoteMonitorPanel.cs`
- [ ] **加载中/忙碌**（loading）
- [ ] **失败提示**（error）

### 传输进度（page.TransferProgressDialog，0%）

- 源文件：`src/Gdterm.UI/Controls/TransferProgressDialog.cs`
- 结构 dump：`transfer-progress |`
- [ ] **加载中/忙碌**（loading）
- [ ] **失败提示**（error）
- 备注：进度对话框：只该有「进行中/失败」两态

### SFTP 双栏（page.SftpDualPanePanel，25%）

- 源文件：`src/Gdterm.UI/Controls/SftpDualPanePanel.cs`
- [ ] **列表/数据展示**（list）
- [ ] **空态引导**（empty）
- [ ] **回车/双击激活**（activation）
- 备注：双栏宿主：单栏能力见 FilePaneControl

### SSH 密钥管理（page.SshKeyManagerForm，25%）

- 源文件：`src/Gdterm.UI/Forms/SshKeyManagerForm.cs`
- 结构 dump：`sshkey-manager |`
- [ ] **列表/数据展示**（list）
- [ ] **空态引导**（empty）
- [ ] **回车/双击激活**（activation）

### 传输中心（page.TransferCenterPanel，25%）

- 源文件：`src/Gdterm.UI/Controls/TransferCenterPanel.cs`
- [ ] **空态引导**（empty）
- [ ] **失败提示**（error）
- [ ] **回车/双击激活**（activation）

### 批量命令（page.BatchCommandPanel，33%）

- 源文件：`src/Gdterm.UI/Controls/BatchCommandPanel.cs`
- [ ] **输入校验**（validation）
- [ ] **配置持久化**（persist）

### 命令面板（page.CommandPaletteForm，50%）

- 源文件：`src/Gdterm.UI/Controls/CommandPaletteForm.cs`
- [ ] **空态引导**（empty）
- [ ] **失败提示**（error）

### 命令模板库（page.CommandTemplatePanel，50%）

- 源文件：`src/Gdterm.UI/Controls/CommandTemplatePanel.cs`
- [ ] **空态引导**（empty）
- [ ] **失败提示**（error）

### 连接导入/导出（page.ConnectionImportExport，50%）

- 源文件：`src/Gdterm.UI/Services/ConnectionImportExportUi.cs`
- [ ] **键盘/快捷键**（keyboard）
- 备注：只有系统文件框 + MessageBox 汇总；无预览、无冲突逐条裁定、无导入结果列表 —— 典型「简陋」

### 快速跳转（page.ConnectionQuickJumpForm，50%）

- 源文件：`src/Gdterm.UI/Controls/ConnectionQuickJumpForm.cs`
- [ ] **空态引导**（empty）
- [ ] **失败提示**（error）

### 密码库条目选择（page.KeePassEntryPicker，50%）

- 源文件：`src/Gdterm.UI/Forms/KeePassEntryPicker.cs`
- 结构 dump：`keepass-picker |`
- [ ] **列表/数据展示**（list）
- [ ] **空态引导**（empty）

### 密码库管理（page.KeePassManagerForm，50%）

- 源文件：`src/Gdterm.UI/Forms/KeePassManagerForm.cs`
- 结构 dump：`keepass-manager,keepass-manager-empty | 有数据 / 空态两态`
- [ ] **列表/数据展示**（list）
- [ ] **空态引导**（empty）

### 密码库解锁（page.KeePassUnlockForm，50%）

- 源文件：`src/Gdterm.UI/Forms/KeePassUnlockForm.cs`
- 结构 dump：`keepass-unlock |`
- [ ] **输入校验**（validation）

### 多通道广播（page.MultiChannelPanel，50%）

- 源文件：`src/Gdterm.UI/Controls/MultiChannelPanel.cs`
- [ ] **空态引导**（empty）
- [ ] **失败提示**（error）

### 通知中心（page.NotificationCenterPanel，50%）

- 源文件：`src/Gdterm.UI/Controls/NotificationCenterPanel.cs`
- [ ] **失败提示**（error）
- [ ] **回车/双击激活**（activation）

### SFTP 权限修改（page.SftpPermissionForm，50%）

- 源文件：`src/Gdterm.UI/Forms/SftpPermissionForm.cs`
- [ ] **输入校验**（validation）

### 外观设置（page.AppearanceSettingsForm，66%）

- 源文件：`src/Gdterm.UI/Forms/AppearanceSettingsForm.cs`
- 结构 dump：`appearance-settings |`
- [ ] **输入校验**（validation）

### 危险命令规则（page.DangerousCommandConfigForm，75%）

- 源文件：`src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs`
- 结构 dump：`dangerous-cmd |`
- [ ] **空态引导**（empty）

### 文件面板（单栏）（page.FilePaneControl，75%）

- 源文件：`src/Gdterm.UI/Controls/FilePaneControl.cs`
- [ ] **空态引导**（empty）

### 高亮规则（page.HighlightRulePanel，75%）

- 源文件：`src/Gdterm.UI/Controls/HighlightRulePanel.cs`
- [ ] **空态引导**（empty）

### 宏录制（page.MacroPanel，75%）

- 源文件：`src/Gdterm.UI/Controls/MacroPanel.cs`
- [ ] **空态引导**（empty）

### OTP 动态码管理（page.OtpManagerForm，75%）

- 源文件：`src/Gdterm.UI/Forms/OtpManagerForm.cs`
- 结构 dump：`otp-manager | B1 OTP manager; smoke 建 tmp 空库 dump，条目态需人工过`
- [ ] **空态引导**（empty）

### 端口转发（page.PortForwardPanel，75%）

- 源文件：`src/Gdterm.UI/Controls/PortForwardPanel.cs`
- 结构 dump：`port-forward | 侧栏常驻（审计 C7-2 入 dump）`
- [ ] **回车/双击激活**（activation）

### 扫描中心（page.ScannerCenterForm，75%）

- 源文件：`src/Gdterm.UI/Forms/ScannerCenterForm.cs`
- 结构 dump：`scanner-center |`
- [ ] **空态引导**（empty）

### 敏感信息扫描（page.SecretScanPanel，75%）

- 源文件：`src/Gdterm.UI/Controls/SecretScanPanel.cs`
- 结构 dump：`secret-scan | 侧栏常驻（审计 C7-2 入 dump）`
- [ ] **空态引导**（empty）

### 书签 / 最近连接（page.SessionBookmarksPanel，75%）

- 源文件：`src/Gdterm.UI/Controls/SessionBookmarksPanel.cs`
- [ ] **空态引导**（empty）

## 4. 加分项缺口（非必需，但决定「简陋感」）

必需项是「不做就是功能残缺」；这一节是不做也能用、但同行都有的东西——
搜索/排序/分页/多选批量/导出/撤销/加载态。按缺得最多排序。

| 页面 | 已有/总数 | 缺口 |
|---|---|---|
| SFTP 双栏 `page.SftpDualPanePanel` | 0/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、键盘/快捷键、无障碍名称 |
| 传输中心 `page.TransferCenterPanel` | 0/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、键盘/快捷键、无障碍名称 |
| 宏录制 `page.MacroPanel` | 0/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、键盘/快捷键、无障碍名称 |
| 快捷键绑定 `page.KeyBindingPanel` | 0/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、键盘/快捷键、无障碍名称 |
| 敏感信息扫描 `page.SecretScanPanel` | 0/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、键盘/快捷键、无障碍名称 |
| OTP 动态码管理 `page.OtpManagerForm` | 1/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| SSH 密钥管理 `page.SshKeyManagerForm` | 1/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 书签 / 最近连接 `page.SessionBookmarksPanel` | 1/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 危险命令规则 `page.DangerousCommandConfigForm` | 1/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 端口转发 `page.PortForwardPanel` | 1/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 通知中心 `page.NotificationCenterPanel` | 1/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、撤销/回收站、加载中/忙碌、键盘/快捷键、无障碍名称 |
| AI 助手设置 `page.AiSettingsForm` | 0/7 | 失败提示、危险操作确认、无障碍名称、撤销/回收站、搜索/过滤、导入、导出 |
| 命令历史 `page.CommandHistoryPanel` | 2/9 | 排序、分页/虚拟滚动、多选/批量、导出、加载中/忙碌、键盘/快捷键、无障碍名称 |
| 命令模板库 `page.CommandTemplatePanel` | 2/9 | 排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 命令面板 `page.CommandPaletteForm` | 2/9 | 排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 密码健康报告 `page.PasswordHealthForm` | 2/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、无障碍名称 |
| 密码库条目选择 `page.KeePassEntryPicker` | 2/9 | 排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 快速跳转 `page.ConnectionQuickJumpForm` | 2/9 | 排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 扫描中心 `page.ScannerCenterForm` | 2/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、无障碍名称 |
| 文件面板（单栏） `page.FilePaneControl` | 2/9 | 搜索/过滤、排序、分页/虚拟滚动、多选/批量、导出、撤销/回收站、无障碍名称 |
| 欢迎页 `page.WelcomePanel` | 1/8 | 列表/数据展示、回车/双击激活、搜索/过滤、失败提示、加载中/忙碌、键盘/快捷键、无障碍名称 |
| 连接属性 `page.ConnectionDialog` | 0/7 | 失败提示、危险操作确认、无障碍名称、撤销/回收站、搜索/过滤、导入、导出 |
| 高亮规则 `page.HighlightRulePanel` | 2/9 | 搜索/过滤、分页/虚拟滚动、多选/批量、导出、撤销/回收站、加载中/忙碌、无障碍名称 |
| 外观设置 `page.AppearanceSettingsForm` | 1/7 | 危险操作确认、无障碍名称、撤销/回收站、搜索/过滤、导入、导出 |
| 多通道广播 `page.MultiChannelPanel` | 3/9 | 搜索/过滤、排序、分页/虚拟滚动、撤销/回收站、加载中/忙碌、无障碍名称 |
| 密码库管理 `page.KeePassManagerForm` | 3/9 | 排序、分页/虚拟滚动、多选/批量、导出、加载中/忙碌、无障碍名称 |
| 运维工具箱 `page.ToolboxPanel` | 2/8 | 搜索/过滤、失败提示、加载中/忙碌、配置持久化、键盘/快捷键、无障碍名称 |
| AI 助手 `page.AiChatPanel` | 3/8 | 列表/数据展示、失败提示、加载中/忙碌、配置持久化、无障碍名称 |
| SFTP 权限修改 `page.SftpPermissionForm` | 0/5 | 配置持久化、失败提示、无障碍名称、危险操作确认、撤销/回收站 |
| 主机资源监控 `page.RemoteMonitorPanel` | 0/5 | 列表/数据展示、分页/虚拟滚动、键盘/快捷键、导出、空态引导 |
| 健康监控 `page.HealthMonitorPanel` | 0/5 | 列表/数据展示、分页/虚拟滚动、键盘/快捷键、导出、空态引导 |
| 密码生成器 `page.PasswordGeneratorForm` | 0/5 | 配置持久化、失败提示、无障碍名称、危险操作确认、撤销/回收站 |
| 快捷命令编辑 `page.QuickCommandEditorForm` | 0/5 | 配置持久化、失败提示、无障碍名称、危险操作确认、撤销/回收站 |
| 修改主密码 `page.ChangeMasterPasswordForm` | 1/5 | 配置持久化、失败提示、无障碍名称、撤销/回收站 |
| 密码库解锁 `page.KeePassUnlockForm` | 1/5 | 配置持久化、无障碍名称、危险操作确认、撤销/回收站 |
| 添加 OTP `page.AddOtpForm` | 1/5 | 配置持久化、无障碍名称、危险操作确认、撤销/回收站 |
| 危险命令确认 `page.DangerousCommandDialog` | 1/4 | 失败提示、输入校验、无障碍名称 |
| 批量命令 `page.BatchCommandPanel` | 1/4 | 失败提示、撤销/回收站、无障碍名称 |
| 连接导入/导出 `page.ConnectionImportExport` | 2/5 | 无障碍名称、危险操作确认、撤销/回收站 |
| 连接树 `page.ConnectionTreeControl` | 5/8 | 加载中/忙碌、键盘/快捷键、无障碍名称 |
| 传输进度 `page.TransferProgressDialog` | 0/2 | 键盘/快捷键、撤销/回收站 |
| 登录脚本 `page.LogonScriptPanel` | 2/4 | 撤销/回收站、无障碍名称 |
| 首次启动向导 `page.SetupWizardForm` | 2/3 | 无障碍名称 |

