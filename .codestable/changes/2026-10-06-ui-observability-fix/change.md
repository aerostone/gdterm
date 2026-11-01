---
doc_type: change
kind: issue
slug: 2026-10-06-ui-observability-fix
status: approved
phase: analyzed
mode: standard
risk:
  level: medium
  reasons: [cross_module, no_tests]
contract:
  include:
    - .codestable/changes/2026-10-06-ui-observability-fix/**
    - src/Gdterm.UI/**
    - src/Gdterm.Terminal/**
    - src/Gdterm.Tests/**
  exclude:
    - src/Gdterm.Core/**
    - src/Gdterm.Connections/**
    - src/Gdterm.Sftp/**
    - src/Gdterm.Tunnel/**
    - src/Gdterm.Rdp/**
    - src/Gdterm.Tools/**
    - src/Gdterm.Security/**
    - src/Gdterm.Logging/**
    - src/Gdterm.AI/**
    - src/Gdterm.KeePass/**
    - docs/**
    - appveyor.yml
  preexisting_changes: [.codestable/tools/check-compliance.py, .codestable/tools/route-observer.py, .codestable/.runtime/runtime-manifest.json, .codestable/tools/verify-compound-archived.py, .codestable/tools/verify-compound-format.py, .codestable/tools/verify-compound-tree.py, .codestable/vendor/baseline-commands.md, .codestable/vendor/baseline-standards.md, .codestable/vendor/baseline-templates.md, .codestable/vendor/baseline.md, .codestable/vendor/change-package.md, .codestable/vendor/code-dimensions.md, .codestable/vendor/constitution.yaml, .codestable/vendor/maintainer-notes.md, .codestable/vendor/model-profile.yaml, .codestable/vendor/requirement-example.md, .codestable/vendor/shared-conventions.md, .codestable/vendor/system-overview.md, .codestable/vendor/tools.md, .codestable/vendor/workflow.yaml, .codestable/changes/2026-10-06-ui-observability-audit/change.md]
  baseline:
    git_head: 16d194a3f0972eaa3c663111863fb7ddd7f74ddf
    dirty_hashes:
      .codestable/.runtime/runtime-manifest.json: 6f0c30aa39cab58f9330e48c7b514b4d9a54be8dba027a84655e53371d136cb3
      .codestable/changes/2026-10-06-ui-observability-audit/change.md: 631967c43769f7922798e49f2893a7a9027a164c7e6350e6a7afb56ccbc31604
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
  architecture_reason: 修复限于日志/反馈/异步化接线，不改变模块边界与调用方向
  requirement_impact: not-applicable
  requirement_reason: 不新增用户能力，只恢复已承诺的可观测性语义
  context_refs:
    design:
      - .codestable/changes/2026-10-06-ui-observability-audit/change.md
  artifacts: []
  evidence_ledger: false
---

# UI 可观测性修复包（A1–A9）（2026-10-06）

上游证据：`.codestable/changes/2026-10-06-ui-observability-audit/change.md`（审计已 closed，本包不重复 file:line 全量证据，只给修复所需的定位与方案）。

## 目标与边界

- **目标**：修复 UI 可观测性审计 A1–A9 共 9 项发现，使断线/重连、隧道清理、热键降级、保存失败等关键路径"用户看得到、事后查得到"，并消除 UI 线程同步网络 IO。
- **边界（做）**：`src/Gdterm.UI/**`（Diagnostics、Services、Controls、Forms、Hotkeys）；`src/Gdterm.Terminal/**`（A1 需在 AutoReconnectWatchdog/ConnectionHealthMonitor 补轮次日志）；`src/Gdterm.Tests/**`（可测项补单测）。
- **边界（不做）**：不改 Tunnel/Sftp/Rdp/Connections 等子系统（A3 的隧道健壮性留在 UI 侧处理）；不动安全语义（审计安全维度 0 发现）；不做行为增强（A4 仅接通既有面板，不加新通知类型）；不回改 architecture/requirement 文档。
- **硬约束（attention.md）**：重连必须 async、禁止 UI 线程 GetResult（go-live 2026-07-26）；新逻辑进 `Gdterm.UI/Services/`，禁止堆回 MainForm/TabContainer；Gdterm.Terminal 不得引用 UI，轮次日志走 `TerminalLog` 汇（Program.cs 已接 CrashLog）；C# 7.3；UI 提示统一 ToastNotifier/AntdUI.Message。

## 逐条 Report（draft:reported）

### A1（P1）断线/自动重连全程不可见

- **现象/触发**：SSH 被踢线或网络抖动 → watchdog 静默重试 5 轮（5/10/20/40/80s）→ 放弃。用户面对死终端零提示；diag.log 无断线与轮次记录，仅 audit JSONL 有终态 `RECONNECT_FAILED`。
- **根因**：`TerminalControl.SessionDisconnected`（TerminalControl.cs:66）、watchdog `Reconnecting/Reconnected`（AutoReconnectWatchdog.cs:42/47）零订阅；`ConnectionLost` 接线（TabSessionLifecycle.cs:105-124、ConnectionHealthMonitor.cs:159-171）不落日志；状态栏/tab chrome 无连接状态。
- **修复方向**：
  1. 新建 `Services/ConnectionStateNotifier`（或并入 TabSessionLifecycle，二选一在 design 定稿）：订阅 `SessionDisconnected` + `Reconnecting/Reconnected/ReconnectFailed`，统一扇出到 ① `BottomBarPanel` 新增连接状态段（断开=红/重连中=N/已恢复=绿）② tab 标题状态点（TabChromePainter/Tab.Text，遵循 UI 缩放规范）③ 断开与放弃各 Toast 一次（恢复用 Info 级，避免每轮弹）。
  2. `Gdterm.Terminal` 侧：ConnectionHealthMonitor.ConnectionLost 触发点、watchdog 每轮重试与终态各写一条 `TerminalLog`（含 RetryCount/耗时），接入既有 CrashLog 通道。
  3. 接线点放 ProtocolTabOpener/TabSessionLifecycle（开签时订阅、关签时退订，防泄漏）。
- **验证**：修后 grep `Reconnecting +=|SessionDisconnected +=` 出现订阅；CI 编译；Windows 手工：kill sshd/断网 → 观察状态栏与 Toast、diag.log（或 logs 轮次记录）出现断线与轮次、恢复后状态回绿。

### A2（P1）图片预览 UI 线程同步下载

- **现象/触发**：双击/F3/右键预览远端图片 → 主线程 `DownloadAsync(...).GetResult()`（FilePaneControl.cs:376）阻塞至下载完成；慢链路冻结，SSH 挂起无限期；无超时。
- **根因**：同方法文本预览已正确 `Task.Run`（:393），图片路径漏改。
- **修复方向**：对齐 :393 模式——`Task.Run` 下载 + 状态栏"预览加载中…" + 回 UI 线程展示（`ContinueWith` 当前同步上下文）+ `CancellationTokenSource` 30s 超时；失败保留 MessageBox。禁止任何新增 `GetResult()` 于 UI 事件路径。
- **验证**：grep `GetAwaiter` 于 FilePaneControl 仅剩 RunFsOp/Task.Run 包裹内；CI 编译；手工预览大图期间 UI 可操作、断链时报错不冻结。

### A3（P2）隧道清理吞异常 + UI 线程同步执行

- **现象/触发**：关标签/自动重连时隧道清理失败 → 后台连接与本地端口残留（下次"端口占用"），diag.log 无记录；网络慢时关签卡顿。
- **根因**：TabSessionLifecycle.cs:164-168 `GetResult()` + `catch { /* best-effort */ }`（全文件唯一无痕 catch）；调用链 TabCloseService.cs:112 在 UI 点击线程。
- **修复方向（两案，design 定夺）**：案 A（最小）：catch 补 `DiagLog.Swallowed("TabSessionLifecycle.CloseTunnel", ex)`，同步执行保留（接受卡顿）；案 B（完整）：`Task.Run` 异步清理 + 失败日志，依赖 TunnelManager 同 connectionId 单飞防竞态（attention 既有机制）。倾向案 B，但需评估 reopen 竞态；若评估不充分回退案 A。
- **验证**：grep 无裸 `catch { /* best-effort */ }`；手工：断链状态下关签 → diag.log 出现清理失败记录（或成功无痕）、重开无端口占用。

### A4（P2）通知中心恒空

- **现象/触发**：错过 3–5s Toast 后无处回查；通知中心仅 XFER/KEY 两类事件。
- **根因**：ToastNotifier.Show 不 Push；生产者仅 TransferCenterPanel.cs:78、SshKeyManagerForm.cs:160。
- **修复方向**：`ToastNotifier.Show` 落点统一 `NotificationCenterPanel.Push(level, message)`（try-catch Swallowed，按调用侧 level 透传）；现有 2 处直连 Push 保持（避免重复）。注意 ToastNotifier 与 NotificationCenterPanel 同程序集，无新引用。
- **验证**：单元测试：Show 后 NotificationCenterPanel 静态缓冲含对应条目（若面板无缓冲则断言 Push 被调用的可测接缝）；手工触发一次"移动失败"→ 打开通知中心可见历史。

### A5（P2）ui.log 镜像不含异常

- **现象/触发**：logs/ui.log 只有 Info 流水，MainForm/ConnDialog 的 Swallowed 全在 diag.log，UI 专项排查文件缺最关键异常。
- **根因**：DiagLog.Swallowed（DiagLog.cs:14-24）传 `uiFile=false`；仅 Info（:57-67）按 UiSourcePrefixes 镜像。
- **修复方向**：Swallowed 按同一 `UiSourcePrefixes` 判定决定 uiFile（推荐，异常才是 UI 排查重点）；同步修正 :27 注释语义。CrashLog.Write 签名已支持，无需改。
- **验证**：单测：Swallowed("MainForm", ex) 后 ui.log 含该条；Swallowed("TunnelManager", ex) 不进 ui.log（前缀白名单语义不变）。

### A6（P2）热键注册失败静默降级

- **现象/触发**：Ctrl+` 被占用 → 功能失效零提示。
- **根因**：GlobalHotkeyManager.cs:60 throw → GlobalHotkeyController.cs:28-31 仅 DiagLog。
- **修复方向**：Initialize catch 补一次 `ToastNotifier.Show("全局热键 Ctrl+` 注册失败（可能被占用）", Warn)`（注意反引号转义），DiagLog 保留；MainForm 生命周期内只提示一次。
- **验证**：grep Controller catch 含 ToastNotifier；手工占用热键启动（或临时改 RegisterHotKey 目标键）见 Toast。

### A7（P2）外观保存假成功

- **现象/触发**：Save 失败（只读介质/占用）仍 `DialogResult=OK` → 无条件弹"外观已保存"，重启设置丢失。
- **根因**：AppearanceSettingsForm.cs:316-323 catch 后继续 OK；ToolsDialogsLauncher.cs:159 只看 DialogResult。恢复默认（:346-351）同模式。
- **修复方向**：Save/Reset 失败 → 窗体内 AntdUI.Message.error 展示，`DialogResult=Cancel`（不 Close），DiagLog 保留；成功路径不变（Success Toast 仅 OK 时弹，现状已满足）。
- **验证**：手工把 appearance.ini 置只读 → 保存报错、不弹 Success、文件未变；恢复正常路径无回归。

### A8（P2）CrashLog 等级恒真条件

- **现象/触发**：空消息异常记为 `[INFO]`，按 ERROR 过滤漏检。
- **根因**：CrashLog.cs:63 `ex is Exception` 恒真。
- **修复方向**：判定改写为 `isTerminating → FATAL；ex != null → ERROR；否则 INFO`，删除恒真子句；:68-71 source 启发式保留（防止 Info 路径误升需另测，若单测证明误升则一并简化）。
- **验证**：单测三例：`ex` 非空空消息 → ERROR；`ex` null + info source → INFO；terminating → FATAL。

### A9（P2）日志同步写盘

- **现象/触发**：慢介质（U 盘）上开标签/点击连发日志时 UI 卡顿（无 profiling，影响中）。
- **根因**：CrashLog.cs:102-133 每条 `lock` + FileInfo 轮转检查 + `File.AppendAllText`（开/关文件）。
- **修复方向**：写入改单生产者队列 + 后台批 flush；`FATAL`/`AppDomain.ProcessExit`/`AppShutdownCoordinator` 路径同步 flush 保证崩溃安全；轮转检查移入 flush 循环。风险：队列未刷丢日志——用 ProcessExit 兜底 + 队列上限。
- **验证**：单测：并发写 N 条 → 内容全量落盘、行序正确；进程退出路径立即可见；CI 编译。

## 行为增量

| # | 变更前 | 变更后 |
|---|--------|--------|
| A1 | 断线/重连无 UI 与日志痕迹 | 状态栏连接状态段（活动会话）+ Toast（断开/恢复/放弃各一次）+ TerminalLog 记轮次与原因、monitor 断线点落日志 |
| A2 | 预览图片冻结 UI | 后台下载 + 状态栏"预览加载中" + 30s 超时 + 失败回 UI 提示 |
| A3 | 清理失败无痕（断链时卡顿） | 失败必落 DiagLog + >2s 慢清理记录（案 A，保持同步） |
| A4 | 通知中心近乎恒空 | ToastNotifier.Show 统一留历史；删 SshKeyManagerForm 重复直连 |
| A5 | ui.log 无异常 | UI 前缀源的 Swallowed 镜像进 ui.log（Info 语义不变） |
| A6 | 热键失败静默 | 一次性 Warning Toast（日志保留） |
| A7 | 保存失败仍报成功 | 失败 AntdUI.Message.error 且不关窗、不回显 |
| A8 | 空消息异常=INFO；terminating+null ex 到不了 FATAL | isTerminating→FATAL、ex!=null→ERROR（前缀保护与启发式保留） |
| A9 | 每条日志同步开文件 | 有界队列批写，FATAL/ProcessExit 同步 flush |

## 设计与契约

### 定位（analyze 阶段逐条读码复核，非报告转抄）

| # | 关键定位（复核确认） | 本阶段新增确认的事实 |
|---|---|---|
| A1 | TerminalControl.cs:66/:863；AutoReconnectWatchdog.cs:42/47/52、:233/:247/:284/:296；MainForm.cs:144-157；TabSessionLifecycle.cs:105-124；ConnectionHealthMonitor.cs:159-171 | **watchdog 全文件零日志调用**（grep 无命中）；`ReconnectEventArgs{SessionId,RetryCount,MaxRetries,NextRetryDelayMs,ErrorMessage}` 已够日志/UI 用；**SessionId = config.Id**（ProtocolTabOpener:156 等，跨重连稳定）；`session.Disconnected` 已被 TabSessionLifecycle:110 订阅（只驱动重连动作，无观测扇出）；ToastNotifier.Show 已自带 UI 线程 marshal（:59-63）、BottomBarPanel.Update* 自带 InvokeRequired（:164-171）→ 后台线程可直接调 |
| A2 | FilePaneControl.cs:376（PreviewSelected 图片分支），同方法 :393-407 文本分支已是 `Task.Run + ContinueWith(FSCContext)` | 失败反馈 MessageBox 在 :380-383（可见性达标，问题是阻塞） |
| A3 | TabSessionLifecycle.cs:164-168；调用方 TabCloseService.cs:112、TabReconnectService.cs:117（closeTab→openConnection 同链路） | 案 B 的 fire-and-forget 会引入 close→open 同 connectionId 顺序竞态（见方案） |
| A4 | ToastNotifier.cs:53-71（Show，无 Push）；NotificationCenterPanel.cs:65-73（静态 Push + lock Buffer）；生产者仅 TransferCenterPanel.cs:78、SshKeyManagerForm.cs:160 | SshKeyManagerForm:160 与 :159 Toast 成对 → A4 后**必重复，需删直连**；TransferCenterPanel.Record 无 Toast → 保留 |
| A5 | DiagLog.cs:14-24（Swallowed→Write(uiFile 缺省 false)）、:44-51 `IsUiSource` 已存在、:57-66 Info 用法 | 修复 = Swallowed 补 `uiFile: IsUiSource(source)` 一个参数，前缀语义不变 |
| A6 | GlobalHotkeyController.cs:27-31（catch 仅 DiagLog）；GlobalHotkeyManager.cs:57-61 throw | Initialize 每实例一次，无重复提示问题 |
| A7 | AppearanceSettingsForm.cs:297-323 `SaveResult`（catch 后仍 :322-323 OK+Close）；:346-351 `ResetToDefaults`（catch 后仍回显）；类基 `AntdUI.Window`（:17） | 失败提示模式现成：`AntdUI.Message.error(this, …)`（SshKeyManagerForm:166 同款） |
| A8 | CrashLog.cs:63-66（恒真条件）、:68-71（启发式）、:47-56（info:/swallowed: 前缀分支先行） | `isTerminating && ex==null` 现状也到不了 FATAL（内层 `ex != null` 守卫）→ 一并修正 |
| A9 | CrashLog.cs:101-133（lock 内 AppendWithRotate=FileInfo 检查+File.AppendAllText 每次开关文件） | DiagLog.Info/CrashLog 全部经此单点，改造只落一个文件 |

### 路径（A1 主链，正常/失败/分叉点）

- **正常**：连接成功 → TabContainerControl.HandleTerminalConnected(:280) → `ts.IsConnected=true` → WireHealthAndReconnect(ts.SessionId, session) → monitor.Start + watchdog.Watch。全链无观测输出（现状亦无）。
- **失败**：断链 → 分叉点①`session.Disconnected`（TabSessionLifecycle:110）与分叉点②monitor 5s 轮询闩（ConnectionHealthMonitor:159）→ 二者都走 `onLost/NotifyConnectionLost`（现状只做动作）→ watchdog.ReconnectLoop（Task.Run 线程池, :212）→ `Reconnecting`(每轮 :247/:296，带 RetryCount/NextRetryDelayMs/LastError) → 终态 `Reconnected`(:284) 或超限 `ReconnectFailed`(:233)。
- **现状观测断点**：四事件 0 订阅（除 MainForm:144 订 ReconnectFailed 只写 audit JSONL）、watchdog 0 日志、monitor raise 点 0 日志、状态栏/标签 0 反馈。
- **A2 路径**：双击图片(F3/右键) → UI 线程 → `DownloadAsync().GetResult()`(:376) 阻塞 → 完成才 Process.Start / 或异常 MessageBox。分叉点即 :376；对照 :393 文本分支已异步。

### 根因归类

A1=缺少接线（事件有定义无订阅）+分层日志缺位；A2=异步化改造遗漏点（同方法旁支已改）；A3=防御性吞异常+UI 线程同步网络 IO；A4=生产者未接通（入口存在无调用方）；A5=参数缺位（uiFile 恒 false）；A6=错误处理只日志不反馈；A7=失败分支未截断成功路径；A8=恒真条件逻辑缺陷；A9=同步 I/O 写入设计。全部为主根因、无伴随因素；无并发类主根因（A3 案 B 才引入并发，故推荐案 A）。

### 影响面（具体调用方）

- A1：新增/触及 TabSessionLifecycle（已有 Disconnected 订阅处补日志）、AutoReconnectWatchdog（3 事件处补 TerminalLog）、ConnectionHealthMonitor（raise 点补 TerminalLog）、新增 Services/ConnectionStateNotifier、BottomBarPanel（新增状态段）、MainForm（组合根 1 行接线，符合 finding-10 边界——呈现策略在 Service、MainForm 只接线）；**不动** MainForm:144 既有 audit 块。
- A2：FilePaneControl 单方法单分支。
- A3：仅 TabSessionLifecycle 一个 catch；调用方签名不变（TabCloseService/TabReconnectService 不动）。
- A4：ToastNotifier.Show 单点 + 删除 SshKeyManagerForm:160 直连；TransferCenterPanel.Record 保留。
- A5：DiagLog.Swallowed 单点（影响所有 Swallowed 调用方的 ui.log 镜像，属预期扩面）。
- A6/A7/A8/A9：各自单文件单方法（A7 两方法、A9 还需 Program.cs ProcessExit 注册一行或 CrashLog.Init 内自注册）。

### 方案对比与推荐

**A1（UI 扇出位置）**

| 方案 | 内容 | 优点 | 风险 |
|---|---|---|---|
| U1 | MainForm 直接扩展现有 watchdog 订阅块 | 改动最小 | 策略继续堆 MainForm，违 finding-10/attention 红线 |
| **U2（推荐）** | 新 `Services/ConnectionStateNotifier`：ctor 注入 watchdog + `Func<string,string> resolveName` + `Func<string,bool> isActive` + `BottomBarPanel`；内部订阅三事件，产出=DiagLog+状态段+Toast；MainForm 组合根一行 new+Dispose | 符合"新逻辑进 Services/"；纯委托可单测；MainForm 零业务 | 多一个类（约 80 行） |
| U3 | 并入 TabSessionLifecycle | 复用已有 watchdog 引用 | 它持"接线"职责无 UI 引用，注入面反而变大，职责混淆 |

推荐 **U2**。配套契约：① Gdterm.Terminal 侧轮次/断线日志一律 `TerminalLog.Info`（sink 已由 Program.cs:138 接 CrashLog→diag.log）；② UI 侧 `DiagLog`+`ToastNotifier`；③ Toast 策略：首轮 Reconnecting→Warning"连接断开，正在重连: {host}"，Reconnected→Success"连接已恢复: {host}"，ReconnectFailed→Error"重连失败: {host}（可手动重连）"，每会话每状态一次、每轮次不弹；④ 状态段仅当 `isActive(sessionId)` 时更新（非活动标签由 Toast 覆盖），Reconnected 清除；⑤ **tab 标题状态点不采纳**（需 TabChromePainter 状态缓存+重绘驱动，状态栏+Toast+日志已闭环，收益边际）。

**A3（隧道清理）**

| 方案 | 内容 | 优点 | 风险 |
|---|---|---|---|
| **案 A（推荐）** | 保持同步调用；`catch { }` → `catch (Exception ex) { DiagLog.Swallowed("TabSessionLifecycle.CloseTunnel", ex); }` + Stopwatch 计时，>2s 记 DiagLog.Info 慢清理 | 零竞态；补齐观测；慢清理日志为案 B 提供实证 | 断链场景 UI 卡顿仍在（P2 保留） |
| 案 B | Task.Run 异步清理 | 消除卡顿 | 重连链 closeTab→openConnection 同 connectionId，fire-and-forget 后旧 close 可能晚于新 start 完成→**误关新隧道**；需先加顺序保证，改动面外溢 TabCloseService/TabReconnectService |

推荐**案 A**（简单模型未被实证推翻前不加机制；案 A 的慢清理日志即"是否需要案 B"的判据，实证卡顿后再立项）。

**A9（写盘）**

| 方案 | 内容 | 优点 | 风险 |
|---|---|---|---|
| **Q1（推荐）** | 有界队列(1000)+单消费线程批写；`isTerminating`/ProcessExit 同步 FlushSync；轮转检查移入消费循环 | 消除 UI 路径开文件；崩溃路径同步保证不丢 | 实现约 60 行+并发单测；进程被强杀时队列尾部丢失（可接受，FATAL 路径已同步） |
| Q2 | 保留同步写，仅加"批量合并/降低频次"优化 | 改动小 | UI 线程开文件本质未消除，弱证据下收益有限 |

推荐 **Q1**；风险兜底= FATAL 同步 drain + ProcessExit drain + 队列上限丢弃计数。

**其余（A2/A4/A5/A6/A7/A8）**：均单点定案，无方案分叉——A2 复制 :393 模式+30s CTS 超时；A4 在 Show 空值守卫后 `Push(级别大写, message)`（Push 纯静态无 UI 依赖，任何线程安全）+删 SshKeyManagerForm:160；A5 Swallowed 补一个参数；A6 catch 内补一条 Warning Toast；A7 catch→`AntdUI.Message.error`+`return`（不 OK 不 Close，Reset 失败不回显）；A8 else-if 改 `isTerminating→FATAL；ex!=null→ERROR（保留 info 前缀保护与 :68 启发式）`。替代方案均为"不改/只改注释"，不满足根因，弃。

### 复核信号与复现阻碍

- 本机（Linux）无 .NET SDK：**不能本地编译/运行**——替代证据链=静态 grep 断言（逐条：A1 订阅出现、A2 UI 线程 GetResult 消失、A3 裸 catch 消失、A4 Push 接通、A7 无条件 OK 消失）→ CI 编译环（AppVeyor，推送后取结果）→ Windows 手工冒烟（kill sshd/断网、断链关签、占用热键、只读 ini、大图预览）。
- 可单测信号（Gdterm.Tests 控制台 runner）：A8 三级判定三例、A5 镜像两例、A4 Push 断言、A9 并发写+FlushSync 顺序。

### 模型路由（下放候选）

- 可下放低阶 step：A2、A4、A5、A6、A7、A8（单点、有既定模式、静态断言可验）。
- 高阶全程：A1（跨 6 文件接线）、A3（竞态评估）、A9（并发写盘）。
- 下放需用户确认后才切换；未确认则当前模型全程。

## 执行计划（分批，批界审批）

| 批 | 内容 | 依据 |
|----|------|------|
| 批 1 | A1 + A2 | P1，当前迭代必须处理 |
| 批 2 | A3 + A7 + A6 | 反馈正确性（吞异常/假成功/静默降级） |
| 批 3 | A4 + A5 | 通道接通（通知中心/ui.log） |
| 批 4 | A8 + A9 | 日志基建微调，最后做避免干扰前批验证 |

- 审批模式：整包一次批复或按批放行均可；批内逐条 commit（`fix(ui-obs-A1): …` 风格），批末跑验证。
- 依赖：A1 先于 A3（同在 TabSessionLifecycle/Close 链路，避免冲突）；A8 先于 A9（同一文件 CrashLog.cs，顺序改避免 rebase）。

## 验证策略

- **静态（本机可做）**：每条修后 grep 断言（订阅出现、裸 GetResult 消失、裸 catch 消失、Push 接通）；`check-compliance.py --change <本包> --agent`。
- **编译**：本机无 .NET SDK → 以 **CI（AppVeyor）为编译环**，推送后取构建结果迭代（用户既定工作方式）。
- **单测**：A4/A5/A8/A9 可在 `src/Gdterm.Tests` 补控制台可跑用例（LogSanitizer 模式先例）；A1/A2/A3/A6/A7 为 UI/运行时行为，以 Windows 手工冒烟清单验收。
- **手工冒烟（Windows，批末）**：断网重连可见性；大图预览不冻结；断链关签有日志；占用热键启动有 Toast；只读外观保存报错。

## 风险

- A1 接线点多（MainForm/TabSessionLifecycle/ProtocolTabOpener），回归面中——独立提交+CI 把关。
- 案 B（A3 异步）存在 reopen 竞态评估不充分的风险，design 阶段可回退案 A。
- A9 改写入路径有崩溃丢日志风险——ProcessExit 同步 flush 兜底 + 单测并发写。

## 执行证据（fix 阶段）

**实施范围**：20 文件，仅方案声明内——A1：AutoReconnectWatchdog（5 处 TerminalLog）、ConnectionHealthMonitor（lost 落日志）、TabSessionLifecycle（disconnected 落日志 + 会话 Disconnected 接线）、ConnectionStateNotifier.cs（新，csproj 已注册）、BottomBarPanel.UpdateConnectionStatus、TabContainerControl.TryGetDisplayName/IsActiveSessionId、MainForm 接线+FormClosed 释放+切签重放；A2：FilePaneControl.PreviewSelected 图片分支；A3：TabSessionLifecycle.CloseTunnelIfLastUser；A4：ToastNotifier.Show→Push、SshKeyManagerForm 直连 Push 删除、NotificationCenterPanel.Snapshot；A5：DiagLog.Swallowed 补 uiFile；A6：GlobalHotkeyController；A7：AppearanceSettingsForm 两方法；A8/A9：CrashLog；测试：InternalsVisibleTo + ObservabilityTests + csproj/Program 注册。

**验证记录（命令 → 退出码）**

1. 静态失败信号断言（bash check，14 组覆盖 9 发现 + 测试注册）→ **exit 0，14/14 PASS**。过程记录：首轮 24/25，A2 断言过宽（整个文件 grep GetResult）误报——收窄至 PreviewSelected 区间 0 命中；FilePaneControl:463 的 GetResult 经 Read 确认位于 `RunFsOp` 的 `Task.Run` 后台线程（:479），非 UI 阻塞，不属 A2 缺陷。
2. 括号平衡计数（python，10 个重写/新增文件）→ 10 OK / 0 MISMATCH。
3. `check-compliance.py --test-impact --symbols ...`（改前）→ pass，既有测试 0 命中（risk none）→ 按推荐新建 ObservabilityTests（A8×3 级别判定、A5×2 镜像、A4×2 留痕、A9×2 并发/排空断言）。
4. **本地构建：未运行**——`which msbuild mono dotnet mcs xbuild` 全部无结果（本机无 .NET 工具链，attention 已声明）；不作"构建通过"声明。编译验证环=CI（AppVeyor），需推送后取结果。
5. required_commands：attention frontmatter `required_commands: {}` → 无适用项。
6. 单测 ObservabilityTests：**未执行**（需 Windows），不作通过声明。
7. impl 门禁：`--phase impl --check` → pass（写码前）；test-impact 与断言见上。

**CI 循环（AppVeyor aerostone/gdterm）**

- 第 1 轮：commit `c69eb04` → build **0.1.341 failed**（job ct6dwjd561r57564，77s 快败）→ 日志 1 处错误：`CrashLog.cs(182) CS0160`（`ObjectDisposedException` 是 `InvalidOperationException` 派生类，双 catch 不可达）。全 log `error CS` 计数=1，无其他错误/同模式残留。
- 第 2 轮修复：合并为单 catch（含派生类）→ commit `66d9749` → push `c69eb04..66d9749`（第 3 次重试成功，直连间歇慢/超时）。
- 第 2 轮结果：build **0.1.342 success**（job sco8n65r8noe62ra，15:51:59→15:54:10，0 失败消息）。test_script 强制退出码全过：`Gdterm.Tests.exe`（**含 ObservabilityTests → A4/A5/A8/A9 断言在 Windows CI 实际执行通过**）+ `--ui-smoke` UI 冒烟（截图/JSON 产物）+ ui-tree-check 控件树门 + check-encoding 编码守卫 + after_test 绿色版 ILRepack 打包。

## 验收结果

- 行为增量 A1–A9 **全部代码落地**，静态失败信号 14/14 绿（exit 0）；方案声明范围外零改动（contract exclude 实证未触碰 .codestable 参考态与 docs）。
- **CI 两轮闭环**：0.1.341 failed（CS0160，1 处，修复）→ 0.1.342 **success**（编译 + 单测含 ObservabilityTests + UI 冒烟 + 控件树门 + 编码守卫 + 绿色版打包全过）。
- **未完成项**：仅剩交互性手工冒烟（断网重连观感/大图预览不冻结/断链关签/热键占用/只读保存等，见遗留清单②）——需人工操作，CI UI 冒烟不覆盖交互流。

## 遗留事项

1. **范围外既存脏态（本会话未触碰，仅记录）**：`.codestable/reference/` 下 10 个文件删除 + `runtime-manifest.json` 删除 + `.codestable/tools/{check-compliance,route-observer}.py` 修改——审计 baseline 时已存在（早于本 fix 会话），疑似历史 `codestable update` 结果，请用户确认预期性；不在本方案 contract 内，未处理。
2. **Windows 手工冒烟清单**：①断网/杀 sshd → 状态段"重连中 n/m"（活动标签）+ 每断连一次 Warning Toast + diag.log 出现 `AutoReconnect attempt session=` 轮次、恢复 Success Toast/`recovered`、放弃 Error Toast/`give up` ②双击远端大图 → 状态栏"预览加载图片 …"、UI 不冻结、>30s 报超时 ③断链场景关签 → diag.log 有 `TabSessionLifecycle.CloseTunnel` swallowed 或无（不再裸吞）④占用 Ctrl+` 后启动 → Warning Toast ⑤只读目录保存外观 → 错误提示且窗体不关 ⑥恢复默认失败 → 提示且不回显 ⑦Tests.exe → ObservabilityTests 全过（退出码 0）。
3. 设计已裁决的未采纳项：tab 标题状态点（状态栏+Toast+日志闭环）；隧道清理案B（以 >2s 慢清理日志为实证门槛再立项）；A9 队列强杀丢尾（FATAL/ProcessExit 已同步排空，可接受）。
4. 模型路由：全程当前模型，未下放（无 `user_confirmation: approved`）。

## 下一步

- ✅ commit `c69eb04`（20 文件）+ `66d9749`（CS0160 修复），均 `--only` 限定 src，已推送 master（死代理 192.168.3.98:7890 绕过走直连，重试 3 次成功）。
- ✅ CI：0.1.341 failed → 修复 → **0.1.342 success**（单测/冒烟/门禁/打包全过，ObservabilityTests 已在 CI 执行）。
- ⏳ 待 Windows 手工冒烟 7 项（遗留清单②）→ 用户确认后置 accepted + `--phase accept --converge` 归档。
