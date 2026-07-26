---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "obs-02"
nature: maintainability
severity: P1
confidence: high
suggested_action: cs-issue
status: **resolved** (2026-07-26 go-live fix batch)
---

# Finding 08：凭据使用与闲锁生命周期零业务审计

## 速答

`IAuditLogger.LogCredentialUse` **全仓无业务调用方**；`AuditLogConfig.LogCredentialUsage` 默认 false。`SecurityEvent.IdleLock`/`Unlock` 等枚举存在但 LockStateCoordinator 从不 `LogSecurityEvent`。无法回答「谁用了哪条凭据 / 何时锁定」。

## 关键证据

- `rg LogCredentialUse` 仅 IAuditLogger + AuditLogger 定义
- `CredentialResolver` 解析成功/失败无审计
- `LockStateCoordinator` 多处空 catch，无审计

## 影响

安全合规与事故追溯缺口；上线到受控环境不合格。

## 修复方向

Resolve/Inject 路径记 LogCredentialUse（默认可仍 off，但调用链要在）；Lock/Unlock 记 SecurityEvent。

## 建议动作

`cs-issue`。

## Resolution

Fixed in go-live fix batch (2026-07-26). See commit message for details.
