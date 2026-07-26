---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-02"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 02：危险命令检测 fail-open

## 修复状态

- **status**: `resolved`
- **fix**: `ConfirmIfDangerous` 与 `AiCommandGateBinder` 检测异常改为 fail-closed（拒绝发送 + MessageBox + `LogSecurityEvent DangerousCommandBlocked`）。
