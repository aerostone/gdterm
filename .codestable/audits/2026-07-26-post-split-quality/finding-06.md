---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-04"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 06：跳板 hop.CredentialRefId 被忽略

## 修复状态

- **status**: `resolved`
- **fix**: `CredentialPayload.HopPasswordsByRefId` + `ResolveHopPassword`；`CredentialResolver.PopulateHopPasswords`；`TunnelManager` 使用 `ResolveHopPassword`。
