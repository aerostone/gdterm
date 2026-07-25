---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "maintainability-01"
nature: maintainability
severity: P1
confidence: high
suggested_action: cs-refactor
status: partial
---

# Finding 10：MainForm / TabContainer 上帝对象

## 速答

`MainForm` ~958 行、`TabContainerControl` ~739 行，菜单/会话/凭据/重连/健康/分屏/侧栏工厂堆叠；任何协议改动都要改 UI 枢纽。

## 关键证据

- `MainForm` 20+ 依赖字段；侧栏 Create* 工厂 + 热键 + 会话恢复全在同一类。
- `TabContainerControl` 同时管 SSH/RDP/Serial/SFTP/凭据/Watchdog/分屏。
- `_aiService` 注入后未使用；`_auditLogger` 仅透传且调用错误。

## 影响

难测、难改、易回归；阻碍“架构易于改动”目标。

## 修复方向

**Separate orchestration from business logic**：抽出 `SessionOrchestrator` / `CredentialResolver`；MainForm 只做布局与菜单。

## 建议动作

`cs-refactor`。

## 修复状态

- **status**: `partial`（显著推进，非全量消灭）
- **done**:
  - `ActiveSessionBridge` / `CredentialResolver` / `RdpOptionsBuilder`
  - `SidePanelFactory`（全部 Create* 侧栏面板 + 多通道同步/广播闸门）
  - `SessionStateCoordinator`（窗口/标签会话保存恢复）
  - `TabSessionLifecycle`（登录脚本、健康监控/Watch、隧道最后用户关闭、Close 审计）
- **remaining**: MainForm 菜单/布局仍在同一类（~870 行）；TabContainer 仍持有 SSH/RDP/SFTP 建签 UI（~760 行）；完整 SessionOrchestrator 与协议策略再拆仍 deferred
- **metrics**: MainForm 1072→~868 行；TabContainer 836→~761 行
