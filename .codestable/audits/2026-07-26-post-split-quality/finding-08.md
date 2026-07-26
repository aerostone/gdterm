---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "perf-02"
nature: performance
severity: P1
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 08：暂停标签仍向 UI 线程泵输出

## 修复状态

- **status**: `resolved`
- **fix**: `OnTerminalOutput` 在 `_isPaused` 时直接返回（仅后台 auto-log），不再 `BeginInvoke`。
