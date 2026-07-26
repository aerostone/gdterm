---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "arch-01"
nature: arch-drift
severity: P1
confidence: high
suggested_action: cs-refactor
status: **resolved** (2026-07-26 go-live fix batch)
---

# Finding 12：ARCHITECTURE 债务表与健康/Watchdog 表述落后 HEAD

## 速答

§债务仍写 MainForm~700/TabContainer~510「部分已修」；实测 **387 / 331** 且 finding-10 已 resolved。健康/自动重连被写成一等能力，未标明 **poll-only、ConnectionLost 一次、无字节/RTT**。ApiKey「仍有债」与 gdk2 强制新写矛盾。

## 关键证据

- `ARCHITECTURE.md` 债务表 vs `MainForm.cs` 387 行 / `TabContainerControl.cs` 331 行
- `attention.md` 已记 finding-10 resolved 与 P0/P1 post-split
- `ConnectionHealthMonitor` 能力 vs 文档「健康监控」字样
- 回填日期停在 2026-07-25，HEAD 已是 9559797（07-26）

## 影响

上线评审若只读架构文档会误判就绪度；或反向低估已完成的拆分。

## 修复方向

`cs-arch` update：债务表、健康/Watchdog 诚实边界、DiagLog、PBKDF2、gdk2、行数。

## 建议动作

`cs-refactor` / `cs-arch`（文档），非代码阻断但影响上线决策质量。

## Resolution

Fixed in go-live fix batch (2026-07-26). See commit message for details.
