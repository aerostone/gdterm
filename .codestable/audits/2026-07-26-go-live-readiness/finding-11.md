---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "sec-02"
nature: security
severity: P1
confidence: high
suggested_action: cs-issue
status: **resolved** (2026-07-26 go-live fix batch)
---

# Finding 11：RDP CredWrite 使用 LOCAL_MACHINE 持久

## 速答

`CRED_PERSIST_LOCAL_MACHINE` 使 TERMSRV 凭据跨会话/重启残留，直到显式 CredDelete。ProcessExit 有 CleanupAll，但 **硬杀/断电不保证**。

## 关键证据

- `KeePassService` CredWrite Persist = LOCAL_MACHINE
- 清理依赖 tab close / ProcessExit / Dispose
- 对比：会话级持久更符合便携客户端

## 影响

共享机/被硬杀后 RDP 密码留在 Windows 凭据管理器。

## 修复方向

`CRED_PERSIST_SESSION`（或文档强制登出清理 + 启动时扫 TERMSRV/ 前缀自有项）。

## 建议动作

`cs-issue`。

## Resolution

Fixed in go-live fix batch (2026-07-26). See commit message for details.
