---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-03"
nature: security
severity: P1
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 09：ApiKey gdk1/明文回退

## 修复状态

- **status**: `resolved`
- **fix**: `ProtectSecret` 强制 gdk2；无主密码时不落盘（返回空）；读路径仍兼容 gdk1/明文以便迁移；`UpgradeSecretsToMasterKey` 升级存量。
