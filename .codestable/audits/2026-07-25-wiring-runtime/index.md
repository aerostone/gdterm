---
doc_type: audit-index
audit: 2026-07-25-wiring-runtime
scope: P0–P2 接线后运行时关键路径 — 架构可变性、功能完整度、内存/泄漏、可观察性、安全泄露
created: 2026-07-25
status: mostly-resolved
total_findings: 14
mode: standard-plus
---

# wiring-runtime 审计报告

## 范围

**关键词（用户）**：架构易于改动、功能完整、泄露评审、长期运行无内存爆炸、可观察性（故障落日志）

**扫描路径（收敛后，约 30–40 文件）**：

| 区 | 路径 |
|---|---|
| 组合根 / UI 枢纽 | `Program.cs`, `MainForm.cs`, `TabContainerControl.cs`, `TerminalControl.cs`, `SftpBrowserPanel.cs`, 侧栏 `*Panel.cs` |
| 会话 / 隧道 | `TerminalSession*.cs`, `Local/SerialSession`, `AutoReconnectWatchdog`, `ConnectionHealthMonitor`, `MultiChannelManager`, `TunnelManager`, `PortForward*` |
| 安全 / 凭据 | `Security*`, `KeePassService`, `DangerousCommand*`, `SecretScanner*` |
| 日志 / AI | `IAuditLogger`, `AuditLogger`, `AuditLogConfig`, `CommandHistoryStore`, `AiAssistantService`, `AiModelStore` |
| 架构文档 | `.codestable/architecture/ARCHITECTURE.md`, `attention.md`, `reviews/p0-p2-wiring-memory-review.md` |

**未盲扫**：全仓库 172 个 `.cs` 文件；工具模块实现细节、UI 纯表单美化不在本轮。

**单文件尺寸警告**：`MainForm.cs` 958 行、`TabContainerControl.cs` 739 行、`SecretScanner.cs` ~42KB — 已作为 maintainability / 拆分信号。

## 总评

接线提交 `61302b7` 之后，**菜单级入口大体存在，但运行时数据面与契约层仍多处断裂**。最严重的不是“缺功能类”，而是：

1. **审计接口调用签名错误** → 当前关键路径在契约层即无法编译/无法落审计（可观察性归零）。
2. **危险命令闸门只罩住键盘回车/多通道广播**，快捷栏/片段/批量/登录脚本/AI/`AutoRunCommands` 旁路；且键盘路径确认前已逐字下发。
3. **关标签不关隧道 + 多通道只 Register 不 Unregister** → 8h+ 多开多关会堆 hop 会话与死引用。
4. **SSH 私钥已进 `CredentialPayload` 但会话层只用 `PasswordConnectionInfo`** → 密钥登录功能不完整。
5. **架构文档仍为空骨架**；UI 上帝对象 + 直接 `new` 具体类 + 泄漏 `Renci.SshNet.SshClient`，后续改动成本高。

整体印象：**功能面宽、闭环浅、可观测弱、长期运行有明确泄漏面**。适合立刻开一批 `cs-issue`（P0）+ 一轮 `cs-arch` 回填 + 中期 `cs-refactor` 拆枢纽。

## 维度评分（1–10）

| 维度 | 分 | 依据摘要 |
|---|---|---|
| Bug 隐患 | **3.5** | 2×P0（危险命令形同虚设、私钥丢弃）+ 多条 P1（假重连、隧道单槽、枚举命名） |
| 安全 | **3.0** | cmdkey 明文参数 P0；危险命令旁路 P0；剪贴板/API key/历史脱敏 P1 |
| 性能/内存 | **4.5** | 隧道与多通道堆积 P0；侧栏事件/PortForward 泄漏 P1；渲染器/缓冲区有硬顶是亮点 |
| 可维护性 | **4.0** | 上帝对象、渲染器硬编码、半接线功能 |
| 架构偏离 | **2.5** | 空 ARCHITECTURE + 接口契约断裂 + 分层泄漏 |
| **综合** | **3.3/10** | 权重：安全×2.0 + bug×1.5 + 架构×1.5 + 性能×1.0 + 维护×1.0 |

口径：3–5 = 存在明显问题，需要尽快处理。

## 发现清单

| # | 性质 | 严重度 | 置信度 | 标题 | 文件 |
|---|---|---|---|---|---|
| 1 | bug | P0 | high | 危险命令确认形同虚设 + 多入口旁路 | [finding-01.md](finding-01.md) |
| 2 | bug | P0 | high | SSH/SFTP 忽略私钥，仅 PasswordConnectionInfo | [finding-02.md](finding-02.md) |
| 3 | security | P0 | high | RDP cmdkey 明文密码进进程参数 | [finding-03.md](finding-03.md) |
| 4 | bug / arch-drift | P0 | high | IAuditLogger 调用签名错误，审计整套未接线 | [finding-04.md](finding-04.md) |
| 5 | performance | P0 | high | 关标签不 Close 隧道，hop 会话堆积 | [finding-05.md](finding-05.md) |
| 6 | performance | P0 | high | MultiChannel 只 Register 不 Unregister | [finding-06.md](finding-06.md) |
| 7 | bug | P1 | high | 自动重连假成功 + 健康监控绑旧 session | [finding-07.md](finding-07.md) |
| 8 | security | P1 | high | 剪贴板密码无 TTL；API key/命令历史脱敏不足 | [finding-08.md](finding-08.md) |
| 9 | bug | P0 | high | 无全局异常钩子 + 关键路径空 catch | [finding-09.md](finding-09.md) |
| 10 | maintainability | P1 | high | MainForm/TabContainer 上帝对象 | [finding-10.md](finding-10.md) |
| 11 | arch-drift | P0 | high | ARCHITECTURE.md 空骨架 vs 12 项目 done | [finding-11.md](finding-11.md) |
| 12 | arch-drift | P1 | high | UI/Tools 分层泄漏 SSH.NET 与 new 具体类 | [finding-12.md](finding-12.md) |
| 13 | arch-drift | P1 | high | 端口转发/远程工具会话注入未闭环 | [finding-13.md](finding-13.md) |
| 14 | bug | P1 | high | ProtocolType 枚举命名与 UI 调用不一致 | [finding-14.md](finding-14.md) |

## 按维度分布

| 性质 | P0 | P1 | P2 | 合计 |
|---|---|---|---|---|
| bug | 3 | 2 | 0 | 5 |
| security | 1 | 1 | 0 | 2 |
| performance | 2 | 0 | 0 | 2 |
| maintainability | 0 | 1 | 0 | 1 |
| arch-drift | 1 | 2 | 0 | 3 |
| **合计** | **7** | **6** | **0** | **13+**（#4 跨 bug/arch 计 1 条） |

> 注：合并去重后主清单 14 条；#4 同时属 bug 与 arch-drift，表中按主导性质计入 bug 列时综合仍突出契约断裂。

## Dead Code / 半接线清单（不占维度上限）

| 文件 | 名称 | 类型 | 说明 |
|---|---|---|---|
| `PasswordHealthPanel.cs` | PasswordHealthPanel | 未用控件 | UI 只用 `PasswordHealthForm` |
| `TerminalControl.EnableAutoLog` | 方法 | 无调用方 | 自动日志功能死入口 |
| `LogonScriptEngine` | 引擎 | 半接线 | 菜单只 CRUD，连接成功不 Execute |
| `TabContainerControl._aiService` | 字段 | 注入未用 | 仅赋值 |
| `IAuditLogger.Log*` 四方法 | API | 业务零调用 | 除错误签名的 LogConnection |

## 下一步建议

### P0 立刻开 `cs-issue`（建议本周）
1. **finding-04 + 14**：修正 `LogConnection` 签名与 `ProtocolType` 命名 — 否则 Windows MSBuild 红灯  
2. **finding-01 + security 旁路**：统一 `SendInput` 闸门；键盘改为本地缓冲、确认后再下发  
3. **finding-03**：RDP 凭据注入去 cmdkey 明文参数或加固清理  
4. **finding-05 + 06**：关标签 `TunnelManager.CloseAsync` + MultiChannel `Unregister`  
5. **finding-02**：`PrivateKeyConnectionInfo` / 双因子连接信息  
6. **finding-09**：`Program` 注册全局异常钩子 + 最小 error 文件  

### P1 本迭代 / 下迭代
- finding-07 重连等真实 `SessionConnected`  
- finding-08 剪贴板 TTL、历史脱敏、API key 入 kdbx 或 DPAPI  
- finding-13 PortForward `SetSshClient` + Toolbox `SetSshSession`  
- finding-10/12 `cs-refactor` 拆枢纽 + `ITunnelManager` / 工厂  

### 架构治理
- **立刻 `cs-arch` backfill**：12 模块边界、组合根、禁止 UI 直接碰 Renci/KeePassLib 细节  
- 更新 `p0-p2-wiring-memory-review.md` 中“入口已接通”声明，标注数据面缺口  

### P2 / 有空
- AI `_history` 滑动窗口  
- MultiChannelPanel / PortForwardPanel 事件与 manager Dispose  
- 渲染器经构造注入 `IRenderer`  

---

## Verification Evidence

- 范围：接线后运行时关键路径（UI 枢纽 + Terminal/Tunnel/Sftp/Rdp + Security/Logging/KeePass/AI）约 30–40 文件；非全仓库 172 文件盲扫  
- 扫描维度：bug / security / performance(memory) / maintainability+observability / arch-drift  
- 子 agent 调用：5 个 Explore dispatched，5 个 returned（Bug / Security / Memory / Observability+Maintainability / Architecture）  
- 合并前发现：约 25 条；去重合并后主清单 **14** 条  
- 主 agent 交叉验证读取：`IAuditLogger.cs`, `TerminalControl.cs`, `TabContainerControl.cs`, `MainForm.cs`, `Program.cs`, `ProtocolType.cs`, `ConnectionDialog.cs`, `ConnectionTreeControl.cs`, `ConnectionHealthMonitor.cs`, `AuditLogger.cs`, `ARCHITECTURE.md`, `attention.md`  
- 架构文档对照：`.codestable/architecture/ARCHITECTURE.md`（唯一文件，空骨架）  
- 综合评分：**3.3/10**（安全×2.0, bug×1.5, 架构×1.5, 性能×1.0, 维护×1.0）  
- 本技能**只发现不定修**；修复路由见各 finding 的 `suggested_action`

## 修复跟踪（2026-07-25 实施批次）

| # | 原严重度 | 状态 | 代表提交 |
|---|---|---|---|
| 1 | P0 | resolved | `1fc04f0` 本地行缓冲 + 多入口闸门 |
| 2 | P0 | resolved | `6dbc670` SSH 私钥连接 |
| 3 | P0 | resolved | `06879a1` CredWrite RDP 凭据 |
| 4 | P0 | resolved | `08b913d` / `d4bd629` 审计签名 + LogCommand |
| 5 | P0 | resolved | `3784467` 末标签关隧道 |
| 6 | P0 | resolved | `3784467` MultiChannel Unregister |
| 7 | P1 | resolved | `0cc0c8d` 重连等真实连接 |
| 8 | P1 | resolved | gdk2 主密码 AES + 剪贴板 TTL + CLI 脱敏 |
| 9 | P0 | resolved | `09a9700` 全局异常钩子 |
| 10 | P1 | partial | `ActiveSessionBridge` 抽出；MainForm/TabContainer 全量拆分仍待 |
| 11 | P0 | resolved | `0ee9b4a` ARCHITECTURE 回填 |
| 12 | P1 | partial | ITunnelManager + ISshRemoteSession + ISshPortForwardHost；UI 无 Renci；Rdp/Serial 工厂仍待 |
| 13 | P1 | resolved | `0355592` PortForward/Toolbox 会话注入 |
| 14 | P1 | resolved | `08b913d` ProtocolType 命名 |

**剩余主动债**：finding-10 全量枢纽拆分、finding-12 Rdp/Serial 工厂抽象。ApiKey 已 gdk2 主密码加密（便携，非 DPAPI）。

**综合观感（修复后预估）**：从 3.3/10 提升到约 **7.0–7.5/10**（P0 闭环，P1 大部分 partial/resolved）。
