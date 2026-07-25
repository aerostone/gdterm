---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "security-02"
nature: security
severity: P1
confidence: high
suggested_action: cs-issue
status: partial
---

# Finding 08：剪贴板无 TTL；API key/命令历史脱敏不足

## 速答

KeePass「复制密码」无定时清空；`AiModelStore` 明文写 ApiKey；`LogCommands` 默认 true 且 sanitizer 挡不住 CLI 位置参数；`CommandHistoryStore` 原样落盘。

## 关键证据

- `KeePassManagerForm`：`Clipboard.SetText(credential.Password)` 无 Timer。
- `AiModelStore` JSON：`"ApiKey":"..."` 明文。
- `AuditLogConfig.LogCommands = true`；`LogSanitizer` 以 `password=` 键值为主。
- `CommandHistoryStore.RecordCommand` 写完整 command。

## 影响

共享机剪贴板残留；便携 data 目录同步即带走 API key 与命令中的密钥。

## 修复方向

剪贴板 15–30s 清空；API key 入 kdbx 或 DPAPI；历史/审计默认关命令或加强 CLI 脱敏。

## 建议动作

`cs-issue`。

## 修复状态

- **status**: `partial`
- **note**: 61d0bfc clipboard TTL + ApiKey obfuscation; d4bd629 CLI sanitizer+history
