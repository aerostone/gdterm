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

- **status**: `partial`
- **note**: ActiveSessionBridge + CredentialResolver + RdpOptionsBuilder 已抽出；MainForm 侧栏绑定经 bridge；全量 SessionOrchestrator/菜单拆分仍 deferred
