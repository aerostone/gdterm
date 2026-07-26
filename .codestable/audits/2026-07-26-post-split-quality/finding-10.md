---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-04"
nature: security
severity: P1
confidence: medium
suggested_action: cs-issue
status: resolved
---

# Finding 10：SecretScan 详情明文匹配内容

## 修复状态

- **status**: `resolved`
- **fix**: 默认 `GetRedactedContent`；「显示明文」需 `MasterPasswordPrompt.Confirm`；`SidePanelFactory` 注入 `ISecurityManager`。
