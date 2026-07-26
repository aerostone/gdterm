---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "maintainability-01"
nature: maintainability
severity: P1
confidence: high
suggested_action: cs-refactor
status: resolved
---

# Finding 10：MainForm / TabContainer 上帝对象

## 速答

原 `MainForm` ~958 行、`TabContainerControl` ~739 行，菜单/会话/凭据/重连/健康/分屏/侧栏工厂堆叠。现已拆为 UI Services 层；MainForm 仅组合根+布局，TabContainer 仅会话字典+Tab chrome 壳。

## 原始证据

- `MainForm` 20+ 依赖字段；侧栏 Create* 工厂 + 热键 + 会话恢复全在同一类。
- `TabContainerControl` 同时管 SSH/RDP/Serial/SFTP/凭据/Watchdog/分屏。
- `_aiService` 注入后未使用；`_auditLogger` 仅透传且调用错误。

## 影响

难测、难改、易回归；阻碍“架构易于改动”目标。

## 修复方向

**Separate orchestration from business logic**：抽出协议/生命周期/侧栏/对话框/视图服务；MainForm 只做组合与布局；TabContainer 只持字典与 chrome。

## 建议动作

`cs-refactor`（已完成）。

## 修复状态

- **status**: `resolved`
- **done** (Services 层):
  - 凭据/协议：`CredentialResolver` / `ProtocolTabOpener` / `TabSessionState` / `RdpOptionsBuilder`
  - 生命周期：`TabSessionLifecycle` / `TabCloseService` / `TabReconnectService` / `TabSplitService` / `TabSelectionCoordinator` / `TabChromePainter` / `TabActiveSessionQuery`
  - 侧栏/菜单：`SidePanelFactory` / `SidePanelHost` / `MainFormMenuBuilder` / `ActiveSessionBridge`
  - 窗体：`SessionStateCoordinator` / `ViewModeController` / `ToolsDialogsLauncher` / `GlobalHotkeyController` / `MainFormCommandRouter` / `AiCommandGateBinder` / `LockStateCoordinator` / `AppShutdownCoordinator` / `ConnectionOpenCoordinator` / `MasterPasswordPrompt` / `ConnectionImportExportUi`
- **metrics**: MainForm 1072→~385；TabContainer 836→~309
- **remaining (intentional)**: MainForm 保留 DI 字段 + 布局装配（组合根职责）；TabContainer 保留 `_sessions` 字典与 `TabControl` 事件订阅（UI 壳职责）。不再追求把这两类压成“无状态转发器”。
- **cleanup**: 删除未接线死代码 `PasswordHealthPanel`（菜单走 `PasswordHealthForm`）
- **attention**: 禁止再把业务堆回 MainForm/TabContainer；新逻辑进 `Gdterm.UI/Services/`
