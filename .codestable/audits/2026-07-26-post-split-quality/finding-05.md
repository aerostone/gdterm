---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-03"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 05：分屏后活动终端解析失败

## 修复状态

- **status**: `resolved`
- **fix**: `TabSessionState.PrimaryTerminal`；`SplitPaneControl.FindFirstTerminal/CollectTerminals`；`TabActiveSessionQuery.ResolveTerminal`；分屏/选中/锁屏/关签路径覆盖全部 TerminalControl。
