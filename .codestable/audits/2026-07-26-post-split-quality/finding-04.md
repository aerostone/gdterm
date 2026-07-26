---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-02"
nature: security
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 04：锁屏不清除会话内明文凭据

## 修复状态

- **status**: `resolved`
- **fix**: `LockStateCoordinator` 锁定时调用 `TabContainerControl.ClearCachedCredentials`（`CredentialPayload.ClearSecrets` + 终端 `Credentials=null`）并 `AutoReconnectWatchdog.PauseAll`；解锁 `ResumeAll`。
