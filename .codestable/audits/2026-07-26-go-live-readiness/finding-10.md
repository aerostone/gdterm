---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "sec-01"
nature: security
severity: P1
confidence: high
suggested_action: cs-issue
status: **resolved** (2026-07-26 go-live fix batch)
---

# Finding 10：跳板 hop 仍仅密码认证

## 速答

叶子目标已支持 `PrivateKeyConnectionInfo`；`TunnelSession.ConnectHop` 与 `CredentialResolver.PopulateHopPasswords` **只处理密码**。密钥-only 跳板无法建立，或错误回落到叶子密码。

## 关键证据

- `TunnelSession.cs:36-64` — 仅 `PasswordConnectionInfo`
- `CredentialResolver.PopulateHopPasswords` — 只填 `HopPasswordsByRefId` 密码字典
- `ConnectDirect`（叶子）已有私钥分支 — 能力不对称

## 影响

企业常见「跳板密钥 + 目标密码/密钥」拓扑失败；上线跳板场景残缺。

## 修复方向

镜像叶子：`HopKeysByRefId` + ConnectHop PrivateKey 路径。

## 建议动作

`cs-issue`。

## Resolution

Fixed in go-live fix batch (2026-07-26). See commit message for details.
