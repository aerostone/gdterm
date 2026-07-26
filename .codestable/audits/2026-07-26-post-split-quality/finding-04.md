---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-02"
nature: security
severity: P0
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 04：锁屏不清除会话内明文凭据

## 速答

`LockStateCoordinator` 在锁定时只 `KeePass.Lock` + 遮罩；`TabSessionState.Credential` / 终端缓存密码仍在内存，重连可无主密码再用。

## 关键证据

- `LockStateCoordinator.Handle`：IsLocked 分支无清空 Credential  
- `TabReconnectService`：使用 `session.Credential` 重连  
- `TabSessionState.Credential` 持有 `CredentialPayload`（含 Password / 私钥）  

## 影响

“锁定”对运维同事路过 / 远程协助仅 UI 遮挡，会话密钥仍可被重连路径使用。

## 修复方向

Lock 时遍历 tabs：`Credential = null`、停 Watchdog、可选断开会话；Unlock 后按需再 Resolve。

## 建议动作

`cs-issue`。
