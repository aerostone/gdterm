---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "bug-01"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 01：重连等待 UI 死锁（GetResult vs async void Connect）

## 速答

`TabReconnectService.WaitForTerminalConnected` 在 **UI 线程** 上 `Task.Run(...).GetAwaiter().GetResult()` 轮询 `IsConnected`；而 `TerminalControl.Connect` 是 `async void`，`await Task.Run(Connect)` 之后的 `_session = session` 需回到 UI 同步上下文——已被 `GetResult` 占住 → **最多卡死 ~20s 并返回 false**，Watchdog 继续 close+reopen 空转。

## 关键证据

- `src/Gdterm.UI/Services/TabReconnectService.cs:155-165` — 线程池轮询包在 `GetResult` 里，调用方是 UI 上的 `ReconnectByIdSync` / `CompleteAfterOpen`
- `src/Gdterm.UI/Controls/TerminalControl.cs:156-198` — `public async void Connect()`；`await Task.Run(() => session.Connect(...))` 之后才赋值 `_session` 并挂 `OutputReceived`
- `src/Gdterm.UI/Controls/TabContainerControl.cs` — `DefaultReconnectFunc` 经 UI `BeginInvoke` 调同步重连，最终仍进 `WaitForTerminalConnected`

finding-07 已去掉 `Sleep+DoEvents`，但 **同步阻塞 UI 等待依赖 UI 续体的 Connect** 仍构成经典死锁。

## 影响

- 自动重连：冻 UI、误判失败、反复开关标签
- 手动 Ctrl+R：同样卡顿
- 上线阻断：任何依赖「断线自愈」的场景不可用

## 修复方向

Connect 完成用 `TaskCompletionSource`/事件在 **任意线程** 置位；等待侧只用 `await` 或纯后台轮询 `_session`/`IsConnected` 且 **禁止 UI 线程 GetResult**；或把 Connect 改成可 await 的 `Task` API。

## 建议动作

`cs-issue` — 运行时正确性 P0，直接决定自动重连是否可用。
