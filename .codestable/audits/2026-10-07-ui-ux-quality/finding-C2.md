---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "ux-C2"
nature: ux
severity: P1
confidence: high
suggested_action: cs-feat
status: reported
---

# Finding C2：无"进行中"反馈与防重入统一语义

## 速答

全仓 **0 处 `Cursors.WaitCursor`**，各类异步/长任务的忙碌表达靠各写各的：有的用私有 bool
（`FilePaneControl._busy`）、有的用 `Enabled=false`、有的什么都没有（同步阻塞 UI）。
用户点击后没有立即反馈 → 二次点击 → 要么重复提交，要么被静默丢弃（`if (_busy) return;`）。

## 关键证据

| 事实 | 数量 | 位置 |
|---|---|---|
| `Cursors.WaitCursor` | **0** | 全仓 `src/Gdterm.UI` |
| 忙碌文案（"加载 …"/"扫描中"） | 1 | `Controls/FilePaneControl.cs:264`（唯一正确样板） |
| `xxx.Enabled = false` 做互斥 | ~16 | 分散在 SecretScan/BatchCommand/MultiChannel/TransferProgress |
| `ProgressBar` | 14 | 传输/扫描类 |
| `CancellationToken`（UI 层） | 26 | 集中在传输/终端，面板级扫描无 |
| 阻断式 `MessageBox` | 58 OK-only + 57 默认 | 见 C4/§9.3 |

**样板（正面）**：`FilePaneControl.Navigate` —— `_busy` 互斥 + 状态栏 "加载 <path> …" +
`Task.Run` + `TaskScheduler.FromCurrentSynchronizationContext()` 回 UI，语义完整，可作模板。

**反例（同步阻塞，无任何反馈）**：`Forms/PasswordHealthForm.cs:129 LoadReport()` 在 UI 线程直接
调 `_keepassService.AnalyzeHealth()` —— KeePass 条目多时窗口出现即卡住，没有等待光标、没有进度、
没有超时。同类：连接建立路径在 `cs-audit 2026-07-26 finding-07` 修掉了 `GetResult` 死锁，但
**"是否给用户等待反馈"** 这一层从未统一设计。

**反例（静默丢弃）**：`if (_busy) return;` / `if (_running) return;` 这类守卫共 3 种写法，
用户二次点击时**没有任何视觉变化**（按钮没变灰、光标没变），表现为"点了没反应"。

## 影响

- 慢操作（KeePass 健康分析、批量扫描、连接、目录列举）体验为"卡死"。
- 无取消：14 处 ProgressBar 中只有传输/扫描有停止按钮，其余长任务无法中断。
- 防重入语义不一致 → 同类 bug 反复出现（每次新增面板都要重新发明一次）。

## 修复方向

统一封装 `BusyScope`（`IDisposable`）：

```
using (var busy = Busy.Begin(this, "正在分析密码健康…", cancellable: true))
{
    var report = await Task.Run(() => _keepassService.AnalyzeHealth(), busy.Token);
}
```

契约：
1. 进入即 `Cursor = WaitCursor` + 主操作按钮 `Enabled=false` + 状态行文案；
2. 退出（含异常）必复位（`try/finally`，参考 `ScannerCenterForm.OnRunClicked` 的 finding-03 修法）；
3. 提供 `Token` 供取消；面板关闭时取消而非 `IsDisposed` 检查；
4. 重入时给出可见反馈（"上一次操作还在进行"）而不是静默 return。

## 建议动作

`cs-feat`（新增 UI 基础设施组件）。先接 `PasswordHealthForm`（当前最差的同步阻塞）+ 3 个扫描面板，
再推广到其余 `none` 侧栏。
