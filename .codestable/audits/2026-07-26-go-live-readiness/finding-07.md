---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "obs-01"
nature: maintainability
severity: P1
confidence: high
suggested_action: cs-issue
status: **resolved** (2026-07-26 go-live fix batch)
---

# Finding 07：AuditLogger 写盘无失败回退

## 速答

`WriteEntry` 在 lock 内无 try/catch；`RotateFile`/`InitializeCurrentFile` 失败后 `_writer` 可为 null，下一行 NRE。调用方常空 catch → **盘满时审计静默全丢**，且不落 CrashLog。

## 关键证据

- `AuditLogger.cs:188-206` — WriteLine/Flush 无保护
- 对比 `CrashLog`：独立文件、never-throw、有旋转

## 影响

恰在故障高峰（磁盘/权限）时失去业务审计，违背可观察性上线门槛。

## 修复方向

WriteEntry 全包 try；失败 `CrashLog`/`DiagLog`；writer null 时降级丢弃并计数。

## 建议动作

`cs-issue`。

## Resolution

Fixed in go-live fix batch (2026-07-26). See commit message for details.
