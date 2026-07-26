---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-01"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 01：TerminalControl.Connect async void 关签竞态

## 修复状态

- **status**: `resolved`
- **fix**: `Connect` 在 `await` 后检查 `_disposed`；若已 dispose 则 `session.Dispose()` 并 return，不再赋值/订阅/触发 `SessionConnected`。catch 路径同样在 dispose 后静默返回。
