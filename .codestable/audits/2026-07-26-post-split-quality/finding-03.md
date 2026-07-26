---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-01"
nature: security
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 03：主密码仅单次 SHA256

## 修复状态

- **status**: `resolved`
- **fix**: `SecurityManager` 使用 PBKDF2-HMAC-SHA256（100000 次）；`MasterPasswordConfig` 增加 `Algorithm`/`Iterations`；旧版 SHA256 解锁后透明升级并经 `PasswordConfigUpgraded` 落盘 `master-password.json`。
