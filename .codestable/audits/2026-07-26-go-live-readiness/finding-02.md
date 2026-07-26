---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "bug-02"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: **resolved** (2026-07-26 go-live fix batch)
---

# Finding 02：健康监控 ConnectionLost 一生只触发一次

## 速答

`ConnectionHealthMonitor` 仅在构造时设 `_connectedAt`；首次判定断开后触发 `ConnectionLost` 并清零 `_connectedAt`；`RecordReconnect()` **全仓零调用方**且即使调用也不复位 `_connectedAt` → 同一 monitor 实例下 **第二次及以后断线静默**，Watchdog 收不到通知。

## 关键证据

- `src/Gdterm.Terminal/ConnectionHealthMonitor.cs:51` — ctor 设 `_connectedAt = DateTime.Now`
- `src/Gdterm.Terminal/ConnectionHealthMonitor.cs:125-128` — `ConnectionLost` 后 `_connectedAt = default`
- `src/Gdterm.Terminal/ConnectionHealthMonitor.cs:88-91` — `RecordReconnect` 只 `++_reconnectCount`
- 全仓 `rg RecordReconnect` 仅定义处
- `OnTick` 整段 `catch { }`（:131）吞掉探测异常
- 延迟实现是对 `IsConnected` 属性读的 Stopwatch，**不是**真实 RTT；`BytesReceived/Sent` 从未赋值

## 影响

- 网络抖动第一次或可恢复，之后自动 HA 失效
- 与 finding-01/03 叠加 → 「自动重连」名存实亡
- 健康面板展示的「延迟/流量」误导运维

## 修复方向

重连成功路径调用 `RecordReconnect` 并复位 `_connectedAt`（或 `MarkConnected`）；可选 SSH keepalive / 读写探针；tick 异常走 DiagLog。

## 建议动作

`cs-issue` — 客户端 HA 核心闭环断裂。

## Resolution

Fixed in go-live fix batch (2026-07-26). See commit message for details.
