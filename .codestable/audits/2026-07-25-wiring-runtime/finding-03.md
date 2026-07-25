---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "security-01"
nature: security
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 03：RDP cmdkey 明文密码进进程参数

## 速答

`InjectRdpCredential` 用 `cmdkey /pass:{password}` 明文参数；进程列表/审计/崩溃转储可见；清理 best-effort 且 `catch {}`，异常退出残留 TERMSRV 凭据。

## 关键证据

- `KeePassService.cs`：`Arguments = $"/generic:TERMSRV/{host} /user:{username} /pass:{password}"`。
- `TabContainerControl` 注入与 `CleanupRdpCredential` 均 `catch {}` 吞失败。
- host/user/pass 未转义，特殊字符可破坏参数。

## 影响

共享机/便携 U 盘场景凭据泄露；进程监控可截获 RDP 密码。

## 修复方向

优先走 RDP ActiveX 安全属性通道；若必须 cmdkey，避免命令行明文（管道/临时受保护输入）并保证退出钩清理。

## 建议动作

`cs-issue`，安全 P0。

## 修复状态

- **status**: `resolved`
- **note**: 06879a1 CredWrite instead of cmdkey CLI
