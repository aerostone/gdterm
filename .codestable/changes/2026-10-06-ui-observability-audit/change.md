---
doc_type: change
kind: audit
slug: 2026-10-06-ui-observability-audit
status: closed
mode: standard
risk:
  level: medium
  reasons: []
contract:
  include:
    - .codestable/changes/2026-10-06-ui-observability-audit/**
    - .codestable/changes/2026-10-06-ui-observability-fix/**
  exclude:
    - src/**
  preexisting_changes: [.codestable/tools/check-compliance.py, .codestable/tools/route-observer.py, .codestable/.runtime/runtime-manifest.json, .codestable/tools/verify-compound-archived.py, .codestable/tools/verify-compound-format.py, .codestable/tools/verify-compound-tree.py, .codestable/vendor/baseline-commands.md, .codestable/vendor/baseline-standards.md, .codestable/vendor/baseline-templates.md, .codestable/vendor/baseline.md, .codestable/vendor/change-package.md, .codestable/vendor/code-dimensions.md, .codestable/vendor/constitution.yaml, .codestable/vendor/maintainer-notes.md, .codestable/vendor/model-profile.yaml, .codestable/vendor/requirement-example.md, .codestable/vendor/shared-conventions.md, .codestable/vendor/system-overview.md, .codestable/vendor/tools.md, .codestable/vendor/workflow.yaml]
  baseline:
    git_head: 16d194a3f0972eaa3c663111863fb7ddd7f74ddf
    dirty_hashes:
      .codestable/.runtime/runtime-manifest.json: 6f0c30aa39cab58f9330e48c7b514b4d9a54be8dba027a84655e53371d136cb3
      .codestable/tools/check-compliance.py: e007f1e37d9716dd8e1a89907cc2740481a8f0996187fb03a231f5ce3956f3ec
      .codestable/tools/route-observer.py: 7ddb8883d8f416f3c421d8cf939e7167851396ad0bf5e569063b7e4eea187b48
      .codestable/tools/verify-compound-archived.py: 709e032bf947d3c2982b1b347abcf0b49fced6ad761ea88b8a362c872d31509d
      .codestable/tools/verify-compound-format.py: f48f53fc91e29b81c2d789926a95cfd452a28cdc44ec389e5bd731783d274d87
      .codestable/tools/verify-compound-tree.py: faa2530a6ab501ac670c7f1282d67e337cdf6507f02e37795ed16f3c397d4c10
      .codestable/vendor/baseline-commands.md: ba2641b3dd3af4b86d16dd3fe2dd5b3f14f32aae6baa8c1e043280e611078b22
      .codestable/vendor/baseline-standards.md: d73984dedf791299966bda5adaebd0e5b1ea5e737af27a122aacc2595391f6ae
      .codestable/vendor/baseline-templates.md: 6ba331678b1df5732ea10b04f6042c255345ba04cad450bc179a7bf5fab17d83
      .codestable/vendor/baseline.md: 103c3f70b06e7e7df4d6bdadc1d991672859814e856d76d57977d6f6d8ec5052
      .codestable/vendor/change-package.md: 47cc1052a47ca3d5cfac08a8bc933bcbae34847fb6af44b8158c37e43c17c143
      .codestable/vendor/code-dimensions.md: 9c4b8dc5177298565b22e4ae0fb54222b67301ff41d0ee48e43c15f1546ecc3e
      .codestable/vendor/constitution.yaml: 077c3c8ab7380176f5ae2206a2a0615be03d662a0a69c4408b41c56d77f77da5
      .codestable/vendor/maintainer-notes.md: c86ee99350df3819f6b990c642efb7280f568d36195e7a2b8894c89b2a90a853
      .codestable/vendor/model-profile.yaml: e606d495610d57ea760f1808e6afa73ac90a06798e5003db4a33ef92dd46def4
      .codestable/vendor/requirement-example.md: 73bcab831918d0cfcacf4365a353735c3f9b7ed532fb138717135ef7fe91de2c
      .codestable/vendor/shared-conventions.md: 268bc1ce8244dd6811856cb2d0f67799016e2d5fd320bc5812cbe5b59c8cf0f4
      .codestable/vendor/system-overview.md: 6a87ac371faeb5f24741588550b1dd9fafefab19e872dca60f7b0d3c4aed0fc9
      .codestable/vendor/tools.md: 19be50c8592868e301fa70cf5739afc1f7d0136700d23d04d9c5a982d76d901c
      .codestable/vendor/workflow.yaml: f47c04965af35144593912bd0282bbca3a293dea754d4ccc7ac1c7f0b0be3d5d
  architecture_impact: not-applicable
  architecture_reason: 审计只读，不修改架构
  requirement_impact: not-applicable
  requirement_reason: 审计只读，不修改需求
  context_refs: []
  artifacts: []
  evidence_ledger: false
---

# UI 可观测性专项审计（2026-10-06）

## 1. 范围

- **对象**：`src/Gdterm.UI`（95 文件 / 25,961 行），联查其依赖的调用链上游 `src/Gdterm.Terminal`（AutoReconnectWatchdog、ConnectionHealthMonitor）与 `src/Gdterm.Tunnel`（TunnelManager、Models/TunnelSession）
- **模式**：Standard 全 5 维（正确性 / 安全 / 架构偏离 / 性能 / 维护性），主轴为**可观测性**（用户可见反馈 + 日志可回溯）
- **目标**：验证"UI 关键路径发生异常或状态变化时，用户能否看到、事后能否查到"
- **基线**：HEAD `16d194a`（工作区含 codestable reference→vendor 迁移脏文件，与本审计无关）
- **方法**：基础设施契约先行（DiagLog/CrashLog/ToastNotifier/全局异常桥/AuditLogger/NotificationCenterPanel）→ 契约一致性 grep 收敛候选（catch 模式、事件订阅、反馈通道）→ 逐候选拉代码核实 file:line → 排除或立项

### 覆盖情况

- **深查（读码核实）**：Diagnostics/{DiagLog,CrashLog,ToastNotifier}.cs、Program.cs（异常桥/锁屏路径）、Forms/{MainForm(错误路径/导出/状态栏),ConnectionDialog(可见性诊断路径),AppearanceSettingsForm,KeePassManagerForm,SetupWizardForm,ChangeMasterPasswordForm,ScannerCenterForm,TransferCenterPanel(经grep),SshKeyManagerForm(经grep)}、Services/{TabSessionLifecycle,TabReconnectService,TabCloseService,GlobalHotkeyController,ToolsDialogsLauncher,ProtocolTabOpener(断连回调),NotificationCenterPanel,ConnectionImportExportUi,TabChromePainter}、Controls/{TerminalControl(连接/断连/审计路径),TabContainerControl,BottomBarPanel,FilePaneControl,FilePaneModel,SftpDualPanePanel,BatchCommandPanel,AiChatPanel,HealthMonitorPanel}、Hotkeys/GlobalHotkeyManager、ImportExport/ConnectionImporterExporter
- **表层（grep 证据）**：其余 Controls/Forms（catch 体、反馈通道、async void、凭据拼接）
- **跳过**：MainForm.Designer.cs、Properties/AssemblyInfo（无行为）；纯样式辅助（DpiScale/NativeTheme 等，按需引用）

## 2. 发现总表

候选 19 → 核实排除 10 类 → 立项 **9**（P1×2，P2×7；安全 0，架构 0）

| ID | 标题 | 维度 | 严重度 | 置信度 |
|----|------|------|--------|--------|
| A1 | SSH 断线/自动重连全程对用户与 diag.log 不可见（事件有定义无订阅） | bug | **P1** | 高 |
| A2 | SFTP 图片预览在 UI 线程同步下载，无超时 | bug/性能 | **P1** | 高 |
| A3 | 隧道清理 best-effort 吞异常且在 UI 线程同步执行 | bug | P2 | 高（静默）/中（卡顿） |
| A4 | 通知中心事件源未接通，面板基本恒空 | 维护性 | P2 | 高 |
| A5 | ui.log 镜像永远不含异常（Swallowed/ERROR 不进） | 维护性 | P2 | 高 |
| A6 | 全局热键注册失败静默降级，无用户提示 | bug | P2 | 高 |
| A7 | 外观保存失败仍报"已保存"（假成功） | bug | P2 | 高 |
| A8 | CrashLog 等级判定恒真条件，空消息异常记为 INFO | bug | P2 | 中（影响） |
| A9 | CrashLog/DiagLog 在 UI 线程同步开文件写盘 | 性能 | P2 | 中（无 profiling） |

## 3. 发现详情

### A1 · SSH 断线/自动重连全程对用户与 diag.log 不可见 【bug / P1 / 高】

**证据**

- `src/Gdterm.UI/Controls/TerminalControl.cs:66` 定义 `SessionDisconnected`，`:863` raise——全仓 grep `SessionDisconnected +=` **0 命中**（事件零订阅）
- `src/Gdterm.Terminal/AutoReconnectWatchdog.cs:42/47/52` 定义 `Reconnecting/Reconnected/ReconnectFailed`——`.Reconnecting +=`、`.Reconnected +=` 全仓 **0 命中**
- `src/Gdterm.UI/Forms/MainForm.cs:144-155`：唯一订阅的 `ReconnectFailed` 只写 `LogSecurityEvent("RECONNECT_FAILED", …)`（audit JSONL），无状态栏/Toast/标签页/tab chrome 变化
- `src/Gdterm.Terminal/ConnectionHealthMonitor.cs:159-171`：`ConnectionLost?.Invoke` 前后无任何日志，外层 `catch { /* 不吞掉后续 tick */ }` 也不留痕
- `src/Gdterm.UI/Services/TabSessionLifecycle.cs:105-124` 接线 `ConnectionLost → NotifyConnectionLost`，同样零日志
- 无任何替代通道：`重连中` 全仓 grep 0 命中；TabChromePainter 只画 selection/hover；状态栏只有 security/keepass 指示（`Forms/MainForm.cs:407-408`）、无连接状态段（`BottomBarPanel.cs:801` 仅发送命令时检查）；tab 标题无状态点

**根因**：断连是一条"状态类事件"，接线只做了动作链（关闭→重开重试），从未接入任何用户可见通道与 diag.log；事件定义齐备但无消费者。

**影响与触发**：网络抖动/堡垒机踢线/SSH 超时 → watchdog 静默重试 5 轮（5+10+20+40+80s）→ 放弃。全程用户面对死终端零提示（易误判为客户端卡死）；diag.log 无断线/轮次记录，排障断链——必须知道去翻 audit JSONL 才能确认"重连失败过"。

**部分缓解（非等价）**：HealthMonitorPanel 打开时显示 ○断开 + 重连计数（需用户主动打开）；重连触发的 reopen 会留 `OpenConnection creating` Info（间接痕迹，不含断线事件与轮次）。

**建议**：cs-issue（缺反馈是缺陷）——订阅 `SessionDisconnected` + `Reconnecting/Reconnected/ReconnectFailed`：断开/重试时状态栏与 tab 标题状态点变化 + 重试轮次 `DiagLog.Info` 落 diag.log，终态（恢复/放弃）Toast 一次。

### A2 · SFTP 图片预览在 UI 线程同步下载，无超时 【bug/性能 / P1 / 高】

**证据**

- `src/Gdterm.UI/Controls/FilePaneControl.cs:376`：`sftp.DownloadAsync(…).GetAwaiter().GetResult()` 直接位于 `PreviewSelected`（`:360`）
- 调用方全部是 UI 事件：`:176` 右键菜单、`:211` F3、`:248` 双击
- 同方法的文本预览已正确 `Task.Run`（`:393`）——同文件两种写法不一致，证明缺陷是遗漏而非设计
- 无超时参数（`CancellationToken.None`），失败仅事后 MessageBox（`:380-383`）

**影响与触发**：双击/预览远端图片即阻塞主线程直到整个下载完成；经跳板链/慢网络下载大图冻结数十秒，SSH 挂起时可无限期冻结（网络 IO 无超时）。对照项目已宣示的规则"重连必须 async，禁止 UI 线程 GetResult"，同类问题再次出现在预览路径。

**建议**：cs-issue——套用同文件 `:393` 既有模式（`Task.Run` + `ContinueWith(TaskScheduler.FromCurrentSynchronizationContext())` + 状态栏"预览加载中…"）。

### A3 · 隧道清理 best-effort 吞异常且在 UI 线程同步执行 【bug / P2 / 高(静默) 中(卡顿)】

**证据**

- `src/Gdterm.UI/Services/TabSessionLifecycle.cs:164-168`：`CloseTunnelIfLastUser` → `_manager.CloseAsync(…).GetAwaiter().GetResult();` 后 `catch { /* best-effort */ }`——**无 DiagLog**，与同文件相邻 catch（124/147/161… 均 `DiagLog.Swallowed`）形成对照，是全文件唯一无痕吞异常点
- 执行位置：`Services/TabCloseService.cs:112`（关闭标签，UI 点击线程）与 `TabReconnectService.cs:117` 的 closeTab（重连 reopen，UI 线程）
- 同步网络 IO 实体：`Gdterm.Tunnel/Models/TunnelSession.cs:203-233`（Dispose 内 `forwardedPort.Stop` + 逐 hop `Disconnect/Dispose`）

**影响与触发**：关标签/自动重连时若断链或端口未释放抛异常 → 后台 SSH 隧道连接/本地监听端口残留（下次连接报端口占用、连接数累积），且 **diag.log 无任何记录**——"端口被占用"类工单无法回溯到清理失败；网络异常时 UI 线程同步等待造成卡顿。

**建议**：cs-issue——`catch` 内补 `DiagLog.Swallowed("TabSessionLifecycle.CloseTunnel", ex)`；关闭流程改 `Task.Run`（与 TunnelManager 自身的异步模型一致）。

### A4 · 通知中心事件源未接通，面板基本恒空 【维护性 / P2 / 高】

**证据**

- `Controls/NotificationCenterPanel.cs:9-10` 自述"汇总 Toast 级别事件"，`:65 Push` 静态入口
- `Diagnostics/ToastNotifier.cs`（全文核实）**不调用 Push**——临时 Toast 展示 3-5s 即销毁
- 全仓 Push 生产者仅 2 个：`Controls/TransferCenterPanel.cs:78`（XFER）、`Forms/SshKeyManagerForm.cs:160`（KEY）

**影响**：错过的一次性提示（含 ToastNotifier 里"移动失败/连接失败"类瞬时消息）无处回查；通知中心长期近乎空白，与面板文档承诺不符。

**建议**：cs-feat——在 `ToastNotifier.Show` 落点统一 `NotificationCenterPanel.Push`（或抽轻量事件总线），使"看过即过"的消息留痕。

### A5 · ui.log 镜像永远不含异常 【维护性 / P2 / 高】

**证据**

- `Diagnostics/DiagLog.cs:14-24` `Swallowed` → `CrashLog.Write(source, ex, false)` **不传 uiFile**
- `Diagnostics/CrashLog.cs:106-107` `Write` 默认 `uiFile=false`，所有异常路径（含 isTerminating）默认只进 diag.log
- 仅 `DiagLog.cs:57-67` `Info` 按 `UiSourcePrefixes`（:27 注释称"UI 相关 source 命中时额外镜像"）传 `uiFile:true`

**影响**：logs/ui.log 作为"UI 专项排查"文件，只含 Info 流水、**零异常记录**；MainForm/ConnDialog 等前缀的 Swallowed 全在 diag.log。修复方向二选一：Swallowed 按同一前缀判定镜像，或把 ui.log 定位改为纯 Info 流水并更新注释。

### A6 · 全局热键注册失败静默降级 【bug / P2 / 高】

**证据**

- `Hotkeys/GlobalHotkeyManager.cs:57-61`：`RegisterHotKey` 返回 false → `throw InvalidOperationException("注册热键失败: …可能已被其他程序占用")`
- `Services/GlobalHotkeyController.cs:28-31`：`Initialize` catch → 仅 `DiagLog.Swallowed`，**无 Toast/对话框**

**影响**：Ctrl+` 呼出/隐藏被其他程序抢占时功能整体失效，用户零感知（要翻 diag.log 才知道），表现为"快捷键没反应"的无效反馈环。

**建议**：cs-issue——`Initialize` 失败后一次性 `ToastNotifier.Show("全局热键 Ctrl+` 注册失败（可能被占用）", Warn)`。

### A7 · 外观保存失败仍报"已保存"（假成功） 【bug / P2 / 高】

**证据**

- `Forms/AppearanceSettingsForm.cs:316` `Result.Save(_iniPath)` 失败 → `:319-322` 仅 `DiagLog.Swallowed` → **`:323` 无条件 `DialogResult = DialogResult.OK; Close()`**
- `Services/ToolsDialogsLauncher.cs:159/170`：只要拿到 OK 就 `ToastNotifier.Success("外观已保存（DPI 需重启）")`
- 同模式：`AppearanceSettingsForm.cs:346-351` 恢复默认——落盘失败仍把表单回显为默认值

**影响与触发**：只读介质/文件占用/权限异常时，设置**未持久化但用户被告知成功**，重启后丢失，唯一线索是 diag.log——观测性反向失真（假成功比无反馈更误导）。

**建议**：cs-issue——Save 失败时置 `DialogResult.Cancel` 并在窗体内展示错误，禁止走 Success Toast。

### A8 · CrashLog 等级判定恒真条件，空消息异常记为 INFO 【bug / P2 / 中】

**证据**

- `Diagnostics/CrashLog.cs:63`：`else if (isTerminating || (ex != null && !(ex is Exception && string.IsNullOrEmpty(ex.Message))))`——`ex is Exception` 对非空 ex **恒真**，条件实际等价于"消息非空才算 ERROR"
- `:68-71` 另有按 source 名含 "Exception"/"swallowed" 的启发式提级

**影响**：空消息异常（部分 Win32/COM 包装异常）被记为 `[INFO]`，按 `[ERROR]`/`[WARN]` 过滤 diag.log 排障时漏检；恒真条件同时说明原始意图丢失（疑似想排除某类异常但写错）。

**建议**：cs-issue——改为明确规则（`isTerminating → FATAL`，其余 → ERROR；提级启发式删除或加注释）。

### A9 · CrashLog/DiagLog 在 UI 线程同步开文件写盘 【性能 / P2 / 中】

**证据**

- `Diagnostics/CrashLog.cs:102-133`：每条日志 `lock` + `FileInfo.Exists/LastWriteTime` 轮转检查 + `File.AppendAllText`（每次打开/关闭文件）
- 调用方多在 UI 事件链：如 `Services/TabContainerControl.cs:189-195` 每次开标签连发 4 条 Info，`MainForm`/`ConnDialog` 点击路径同理
- 绿色版目标介质含 U 盘（项目既定部署形态）

**影响**：慢介质上开标签/高频操作时的可感卡顿；**无 profiling 实测**，故置信度中、定级 P2 不升不降。

**建议**：cs-issue——队列化异步写（批 flush）；保持 `lock` 语义与崩溃安全（FATAL 路径同步落盘）。

## 4. 待确认线索（不计入发现）

- **T1** 终端侧 `TerminalControl` 每组件 68 处空 catch 体（grep 计数）——已核实均为 `DiagLog.Swallowed` 包裹的模式化写法，但**未逐条读语义**，若其中混有"真静默"需另立
- **T2** 长尾 Forms（ScannerCenter/KeyBinding/Highlight/LogonScript 等编辑器）保存路径仅做表层 grep，未逐个核对"保存失败是否可见"；已抽样无异常
- **T3** `ConnectionImporterExporter` 导入失败只 MessageBox 不落日志——可见性达标、日志缺失影响低，若要求"失败必留痕"可并入 A7 同类修复

## 5. 排除清单（有证据的排除）

1. **分层约束违规（UI 直构 SshClient/RdpClient）**：`new SshClient|new RdpClient` 在 Gdterm.UI 下 **0 命中**，均经 factory/桥接接口——排除
2. **凭据进日志**：`(DiagLog|CrashLog|ToastNotifier|MessageBox) ∩ (Password|Secret|ApiKey|Token)` **0 命中**；`ex.Message` 进提示 3 处（ProtocolTabOpener:346 / TerminalControl:843 / SftpDualPanePanel:163）均为错误文案，不含凭据——排除
3. **async void 无 catch**：KeePassUnlockButton、ScannerCenterPanel、SftpDualPanePanel.BeginConnect(:117-163 状态标签+日志)、BatchCommandPanel.ExecuteCommand(:451 状态标签+日志)、TerminalControl.Connect(:835-874 终端内回显+日志) 均有 catch + 可见反馈——排除
4. **文件面板操作无反馈**：`FilePaneControl.Navigate(:269-279)` 与 `RunFsOp(:464-484)` 失败 → 状态栏"读取/操作失败" + MessageBox——达标，排除
5. **FilePaneModel 同步 GetResult（:170/:196/:201/:207）**：调用方均包 `Task.Run`（FilePaneControl:269/468）非 UI 线程——排除（仅 A2 的 :376 例外）
6. **配置导入导出失败无日志**：`ConnectionImportExportUi` 成功/失败双侧 MessageBox 均可见——降级为线索 T3，不立项
7. **JSON→CSV 静默回退（ConnectionImporterExporter:86）**：仅未知扩展名 default 分支，.csv/.json/.txt 显式分流——触发面窄，排除
8. **KeePass 保存/解锁、SetupWizard(:522)、ChangeMasterPassword(:281/:286) 失败反馈**：均有 Toast/_errorLabel/MessageBox——排除
9. **WriteJsonFile 原子写与 FileChangeWatcher 轮询**：一致性/回环防线完整（atomic tmp+replace、净变更才落盘、损坏标记+FILE_REPAIRED 审计）——达标，排除
10. **架构维度**：Services 解构与 ProtocolTabOpener 集中协议分支复核一致，本次无架构 finding（与 2026-08-11 架构审计结论不冲突）

## 6. 总评

观测性**基建是强的**：五通道齐备（DiagLog 主日志分级 INFO/WARN/ERROR/FATAL + CrashLog 兜底 + Toast 反馈 + Audit JSONL + 通知中心）、Program 级 ThreadException/UnobservedTaskException/CriticalFailureEvent 三桥、密码健康/布局/字体等高密度诊断 Info（ConnectionDialog 连 okBtnBounds 都落日志）。**断点在"事件→通道"的接线层**：

1. **状态类事件没人订阅**（A1）——最严重：核心流程"断线→重连→放弃"对用户与 diag.log 双向不可见；
2. **通道有入口无生产者**（A4 通知中心、A5 ui.log 只镜像 Info）；
3. **反馈时序失真**（A7 假成功）与**同步阻塞点**（A2/A3/A9）散落各处；
4. 个别 catch 是**唯一无痕点**（A3），与整体风格不一致，说明规范靠人工复制难以全覆盖。

**修复优先级**：A1 → A2 → A3/A7 → A4/A5/A6 → A8/A9。A1+A2 建议当前迭代处理。

**复查需求**：logging-engine / ui-shell 设计文档均未涉及 ui.log 镜像语义与通知中心事件源（grep 无命中），实现与设计无冲突，**无需回改上游知识库**；若采纳 A4/A5 的修复，建议同步补 logging-engine 设计文档中的通道矩阵。

## 7. Verification Evidence（验证证据）

```text
# A1 事件零订阅
grep -rn '\.Reconnecting +=|\.Reconnected +=|SessionDisconnected +=' src/        → 仅定义与 raise，0 订阅
grep -rn '重连中' src/                                                            → 0 命中
grep -n 'SessionDisconnected' src/Gdterm.UI/Controls/TerminalControl.cs          → :66 定义
# A2 同步阻塞
grep -n 'GetAwaiter().GetResult();' src/Gdterm.UI/Controls/FilePaneControl.cs    → :376(预览-UI线程) :452(RunFsOp内-已包Task.Run)
grep -n 'PreviewSelected' src/Gdterm.UI/Controls/FilePaneControl.cs              → :176 :211 :248（均为UI事件）
# A3 唯一无痕吞异常
grep -n 'best-effort' src/Gdterm.UI/Services/TabSessionLifecycle.cs              → :168 catch { /* best-effort */ }
# A4 通知中心生产者
grep -rn 'NotificationCenterPanel.Push' src/                                     → TransferCenterPanel:78, SshKeyManagerForm:160（+定义处）
# A5 ui.log
grep -rn 'ui.log' src/                                                           → 仅 CrashLog/DiagLog 内部 3 处
# A6 热键
grep -n '注册热键失败' src/Gdterm.UI/Hotkeys/GlobalHotkeyManager.cs              → :60 throw；Controller:28-31 catch 仅日志
# A7 假成功
sed -n '314,323p' AppearanceSettingsForm.cs → Save 失败后仍 :323 DialogResult=OK
# 安全维度
grep -rn '(DiagLog|CrashLog|ToastNotifier|MessageBox)[^\n]*\b(Password|Secret|ApiKey|Token)\b' src/Gdterm.UI → 0 命中
# 分层维度
grep -rn 'new SshClient|new RdpClient' src/Gdterm.UI → 0 命中
```

## 8. 处置

- 本包为只读审计，`status: closed`，不改 `src/**`
- 建议后续：A1/A2/A3/A6/A7/A8 → `cs-issue`（各自独立 report，A1 亦可按新行为走 `cs-feat`）；A4/A5/A9 → `cs-feat`/`cs-refactor`
- 未指定负责人与排期；执行时按第 6 节优先级排序
