---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-04"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 06：跳板 hop.CredentialRefId 被忽略

## 速答

`TunnelManager` 解析跳板密码时三元运算符两臂相同，始终 `credential?.Password`，从不按 `hop.CredentialRefId` 取 KeePass。

## 关键证据

- `TunnelManager.cs`：`hop.CredentialRefId != null ? credential?.Password : credential?.Password`  

## 影响

多跳且跳板账号≠目标账号时认证失败或误用终端凭据。

## 修复方向

按 CredentialRefId 调 KeePass/CredentialResolver；无 ref 再用叶子密码。

## 建议动作

`cs-issue`。
