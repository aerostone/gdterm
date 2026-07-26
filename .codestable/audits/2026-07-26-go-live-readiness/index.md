---
doc_type: audit-index
audit: 2026-07-26-go-live-readiness
scope: 健壮性 / 高可用(桌面客户端韧性) / 可观察性 + 上线门槛评分；对照 HEAD 9559797
created: 2026-07-26
status: active
mode: standard-plus
total_findings: 12
go_live_score: 4.8
go_live_verdict: conditional-no
---

# go-live-readiness 审计报告

## 范围

**目标**：评估 gdterm 是否具备「可上线」的健壮性、客户端高可用（自动恢复）与可观察性，并给出综合分数与门槛建议。

**范围文件（约 22 个关键路径 + 交叉验证）**：

| 簇 | 文件 |
|----|------|
| 组合根 / 全局钩子 | `Program.cs`, `CrashLog`, `DiagLog`, `GlobalExceptionBridge` |
| 会话生命周期 | `MainForm`, `TabContainerControl`, `TerminalControl`, `TabReconnectService`, `TabCloseService`, `TabSessionLifecycle`, `LockStateCoordinator`, `AppShutdownCoordinator`, `SessionStateCoordinator` |
| 韧性引擎 | `AutoReconnectWatchdog`, `ConnectionHealthMonitor`, `TunnelManager`, `TunnelSession`, `TerminalSession` |
| 观测 / 安全残留 | `AuditLogger`, `CommandHistoryStore`, `SecurityManager`, `KeePassService`, `AiModelStore` |
| 架构对照 | `.codestable/architecture/ARCHITECTURE.md`, `attention.md` |

**不在范围**：全仓库功能回归、Windows 实机 MSBuild（本环境无 .NET SDK）、依赖 CVE 扫描。

**模式**：Standard 五维并行 Explore + 主 agent 交叉验证；维度映射：

| 用户诉求 | 审计维度 |
|----------|----------|
| 健壮性 | bug |
| 高可用（桌面韧性） | bug + performance |
| 可观察性 | maintainability + bug（日志缺口） |
| 上线安全底线 | security |
| 文档/债务诚实度 | arch-drift |

## 总评

共 **12** 条发现（去重后）：**P0×2 · P1×9 · P2×1**。

相对上一轮 `2026-07-26-post-split-quality`（P0 安全项已在 `5808529`/`9559797` 落地），**安全底线明显抬升**（PBKDF2、锁屏清凭据、fail-closed、gdk2、CredWrite、私钥登录）。但**自动恢复闭环仍未闭合**：

1. **重连等待与 `async void Connect` 在 UI 同步上下文上死锁**（finding-01）——自动/手动重连可冻 UI ~20s 并误判失败。
2. **健康监控 `ConnectionLost` 一生只响一次**（finding-02）——断线→重连→再断线不再通知 Watchdog。
3. **锁屏 Pause 丢事件、解锁不 re-arm**（finding-03）——闲锁期间掉线，解锁后僵尸会话。

可观察性有骨架（`crash.jsonl` + 全局钩子 + 部分 `DiagLog` + 连接 Open/Error 审计），但 **~148 处空 catch**、凭据/闲锁零审计、审计写盘无失败回退，**运维排障能力不足以上线 SLA**。

### 上线分数（加权）

| 维度 | 分 | 权重 | 依据摘要 |
|------|----|------|----------|
| **安全** | **6.0** | ×2.0 | 核心 vault/主密码/锁屏清凭据到位；跳板仍仅密码、RDP 机器级持久、审计 XOR 装饰 |
| **Bug / 健壮性** | **4.5** | ×1.5 | 2 个 P0 阻断无人值守重连；首次打开/关签路径已硬化 |
| **架构诚实度** | **6.0** | ×1.5 | 组合根/分层大体对齐；债务表与健康/Watchdog 能力表述过时/过誉 |
| **性能 / 客户端 HA** | **4.0** | ×1.0 | 恢复闭环断、UI 阻塞重连、无多标签资源闸、RDP 无应用级 HA |
| **可维护 / 可观察性** | **4.0** | ×1.0 | Crash 通道好；业务审计与空 catch 仍大面积黑洞 |

**综合分 = (6.0×2 + 4.5×1.5 + 6.0×1.5 + 4.0×1 + 4.0×1) / 7.5 = 4.8 / 10**

| 口径 | 含义 |
|------|------|
| 9–10 | 可上生产，隐患可排期 |
| 6–8 | 有条件上线，P0 清完后可内测 |
| **3–5** | **存在阻断项，不建议正式上线** ← 本次 |
| 1–2 | 需专项整改 |

### 上线裁决

| 场景 | 裁决 |
|------|------|
| **内部狗粮 / 开发自用（有人值守、手动重连）** | **可以**（已知断线需人工） |
| **运维台班常驻、跳板多标签、依赖自动重连** | **不可以** — 先修 finding 01–03 |
| **对外/客户侧绿色版正式发布** | **不可以** — 至少清全部 P0 + 核心 P1 可观察性，并在 Windows 完成 MSBuild+冒烟 |
| **高安全合规环境（审计追溯凭据使用）** | **不可以** — `LogCredentialUse` 死接口 + 闲锁无审计 |

**一句话**：**功能面接近完整，韧性与观测未达上线线；当前更像「功能完备的 Beta」，不是「可无人值守的生产客户端」。**

## 发现清单

| # | 性质 | 严重度 | 置信度 | 标题 | 文件 |
|---|------|--------|--------|------|------|
| 1 | bug | **P0** | high | 重连等待 UI 死锁：`GetResult` vs `async void Connect` | [finding-01.md](finding-01.md) |
| 2 | bug | **P0** | high | 健康监控 `ConnectionLost` 一生一次，重连后不再通知 | [finding-02.md](finding-02.md) |
| 3 | bug | P1 | high | 锁屏 Pause 吞断线，解锁 `ResumeAll` 不 re-arm | [finding-03.md](finding-03.md) |
| 4 | bug | P1 | high | 同 connectionId 并发 `EstablishAsync` 非原子，可孤儿隧道 | [finding-04.md](finding-04.md) |
| 5 | bug | P1 | medium | 会话断线状态机不完整：无 session 级 disconnect 信号 | [finding-05.md](finding-05.md) |
| 6 | performance | P1 | high | 重连占 UI 线程至 20s×MaxRetries；无多标签资源闸 | [finding-06.md](finding-06.md) |
| 7 | maintainability | P1 | high | `AuditLogger.WriteEntry` 无 IO 失败回退，盘满时观测坍塌 | [finding-07.md](finding-07.md) |
| 8 | maintainability | P1 | high | 凭据使用/闲锁/解锁零业务审计；`LogCredentialUse` 无调用方 | [finding-08.md](finding-08.md) |
| 9 | maintainability | P1 | high | 关键路径仍大量空 catch；DiagLog 未覆盖重连/隧道/锁 | [finding-09.md](finding-09.md) |
| 10 | security | P1 | high | 跳板 hop 仍仅 `PasswordConnectionInfo`，密钥跳板不可用 | [finding-10.md](finding-10.md) |
| 11 | security | P1 | high | RDP `CRED_PERSIST_LOCAL_MACHINE` + 硬杀可残留 TERMSRV | [finding-11.md](finding-11.md) |
| 12 | arch-drift | P1 | high | ARCHITECTURE 债务表/健康能力表述落后于 9559797 | [finding-12.md](finding-12.md) |

## 按维度分布

| 性质 | P0 | P1 | P2 | 合计 |
|------|----|----|----|------|
| bug | 2 | 3 | 0 | 5 |
| performance | 0 | 1 | 0 | 1 |
| maintainability | 0 | 3 | 0 | 3 |
| security | 0 | 2 | 0 | 2 |
| arch-drift | 0 | 1 | 0 | 1 |
| **合计** | **2** | **10** | **0** | **12** |

## 已具备的上线资产（不计入发现）

- 全局异常：`ThreadException` / `UnhandledException` / `UnobservedTaskException` + `CrashLog` + `GlobalExceptionBridge` 双写
- 主密码 PBKDF2-HMAC-SHA256 100k + 旧 SHA256 迁移；锁屏清 `CredentialPayload` + Watchdog `PauseAll`
- 危险命令 fail-closed；ApiKey 新写强制 gdk2；SecretScan 脱敏再验证
- 叶子 SSH/SFTP 私钥登录；RDP CredWrite（非 cmdkey 命令行）
- `connections.json` 无密码字段；会话恢复只存 ConnectionId
- 关签 last-user 关隧道；`DiagLog` 已覆盖关签/关机主路径
- finding-10 拆分后 MainForm ~387 / TabContainer ~331 组合壳

## 下一步建议

### 上线前必须（P0）— 建议立刻 `cs-issue`

1. **finding-01**：重连等待改为真正异步（或 Connect 完成信号不依赖被阻塞的 UI 同步上下文）
2. **finding-02**：`RecordReconnect` 复位 `_connectedAt`；重连成功后 re-arm；必要时健康探测改真实 keepalive

### 上线前强烈建议（P1 韧性）

3. **finding-03**：`ResumeAll` 对 watched 会话 re-probe + 重放 `NotifyConnectionLost`
4. **finding-04 / 06**：隧道建立加锁或 refcount；重连勿阻塞 UI；软上限标签数
5. **finding-05**：shell/SSH 断线事件驱动 Watchdog，不只靠 5s 轮询

### 上线前强烈建议（P1 可观察性）

6. **finding-07**：`WriteEntry` try/catch + 失败落 `CrashLog`
7. **finding-08**：`LogCredentialUse` + IdleLock/Unlock 审计
8. **finding-09**：重连/隧道/锁路径空 catch → `DiagLog.Swallowed`

### 上线前安全加固

9. **finding-10**：hop 私钥路径对齐叶子
10. **finding-11**：RDP 改为 session 级持久或保证硬杀清理策略文档化

### 文档

11. **finding-12**：刷新 ARCHITECTURE 债务表与健康/Watchdog 诚实表述

### Windows 门禁（环境外，但上线硬门槛）

- `tools/pack-release.ps1` 全量 MSBuild + `Gdterm.Tests.exe`
- 冒烟：断网重连、锁屏掉线解锁、双标签同跳板、RDP 注入清理、15 标签内存

## Verification Evidence

- 范围：约 22 个关键路径文件 + `ARCHITECTURE.md` / `attention.md`；聚焦健壮性/HA/可观察性
- 扫描维度：bug、security、performance(HA)、maintainability(observability)、arch-drift
- 子 agent：5 个 Explore 并行 dispatched，5 个 returned（robustness / observability / HA / security / architecture）
- 合并前原始 FINDING 行：~25；去重合并后：**12**
- 主 agent 交叉验证读取：`TabReconnectService.cs`, `TerminalControl.cs` (Connect), `ConnectionHealthMonitor.cs`, `AutoReconnectWatchdog.cs`, `TabSessionLifecycle.cs`, `TunnelSession.cs`, `AuditLogger.cs`, `Program.cs` hooks, `SessionStateCoordinator.cs`, 全仓 empty-catch / audit 调用点统计
- 架构文档对照：`.codestable/architecture/ARCHITECTURE.md` + `attention.md`
- 量化基线：空 catch ~**148**/55 文件；`DiagLog` 调用 ~29；`LogCredentialUse` 业务调用 **0**；`RecordReconnect` 调用 **0**；`async void` 9 处（TerminalControl.Connect 等）
- 综合评分：**4.8/10**（安全×2.0 bug×1.5 架构×1.5 性能×1.0 维护×1.0）
- 上线裁决：**conditional-no**（内部狗粮可，正式/无人值守不可）
