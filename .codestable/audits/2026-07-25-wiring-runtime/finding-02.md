---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "bug-02"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 02：SSH/SFTP 忽略私钥，仅 PasswordConnectionInfo

## 速答

`CredentialPayload` / KeePass 已装入 `SshPrivateKey`，但 `TerminalSession` / `SftpService` 只建 `PasswordConnectionInfo`，密钥登录主机必失败或空密码硬撞。

## 关键证据

- `TerminalSession.Connect`：`new PasswordConnectionInfo(host, port, user, credential.Password ?? "")`，未读私钥字段。
- `TabContainerControl.ResolveCredential` 会设置 `SshPrivateKeyData` / passphrase，到会话层被丢弃。
- Sftp 路径同构（仅密码连接信息）。

## 影响

宣称的 SSH key 自动填充链路功能不完整；仅密码主机可用。

## 修复方向

有私钥时用 `PrivateKeyConnectionInfo`（或 SSH.NET 多 auth 方法）；无密钥再回落密码。

## 建议动作

`cs-issue`，功能闭环缺陷。

## 修复状态

- **status**: `resolved`
- **note**: 6dbc670 SshConnectionInfoFactory PrivateKeyConnectionInfo
