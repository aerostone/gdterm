---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "arch-01"
nature: arch-drift
severity: P1
confidence: high
suggested_action: cs-refactor
status: resolved
---

# Finding 11：UI 下转 TerminalSession.UnderlyingClient

## 修复状态

- **status**: `resolved`
- **fix**: `ITerminalSession.TryGetSshClient()`；`TabActiveSessionQuery` 用接口 + `SshPortForwardHost.Wrap(object)` / `SshNetRemoteSession.Wrap(object)`；UI 不再 `as TerminalSession`。
