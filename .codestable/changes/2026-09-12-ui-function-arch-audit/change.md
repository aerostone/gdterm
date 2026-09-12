---
doc_type: change
kind: audit
slug: 2026-09-12-ui-function-arch-audit
mode: standard
scope: src/Gdterm.UI（Forms 16 + Controls 34 + Services 31）功能完整性 + 全5维；RDP凭据路径延伸 src/Gdterm.Rdp/FreeRdpClient.cs
created: 2026-09-12
status: closed
total_findings: 4
---

# UI 功能完整性 + 架构审计报告

## 范围与总评

范围：`src/Gdterm.UI` 全量（Forms 16、Controls 34、Services 31，约 105 文件）+ 凭据链延伸 `src/Gdterm.Rdp/FreeRdpClient.cs`。
模式：Standard，全 5 维（bug / security / performance / maintainability / arch-drift）+ 用户追加问"功能是否完整 / 是否屎山"。

总评：**功能基本完整，不是屎山**。菜单 46 回调 MainForm 全实现；Controls 事件/handler 全非空（TODO/FIXME 零命中）；
分层约束全守（Forms 零直接 new 业务类，全经 Factory/Store 接口；MainForm 852 行仅 15 私有方法，组合根达标）；
跨线程 UI 访问全部走 BeginInvoke/InvokeRequired；日志脱敏成体系（`/p:***`、DiagLog.Swallowed 71 处）。
唯一功能缺口（传输中心无清空）审计中已修（`7f720a6`），不列为 finding。
最高风险 1 条 P1（RDP 命令行密码），其余 P2。

## 发现清单

| # | 性质 | 严重度 | 置信度 | 标题 | 位置 |
|---|---|---|---|---|---|
| 1 | security | P1 | high | RDP 密码经命令行传给 wfreerdp 子进程，同桌面会话可见 | `src/Gdterm.Rdp/FreeRdpClient.cs`（`/p:` + Q(password) 组装处） |
| 2 | maintainability | P2 | high | orphan 三件套编译残留（QuickBar/StatusBar/TmuxBar），ViewModeController 仍引用 TmuxBarPanel 类型 | `src/Gdterm.UI/Services/ViewModeController.cs:20,41` |
| 3 | performance | P2 | medium | LineBox/RowStep 每次 CreateGraphics 量字，底栏/树/标签高频路径重复创建 | `src/Gdterm.UI/Services/FormFontPolicy.cs:114,146` |
| 4 | bug | P2 | medium | 空 catch 100+ 处，71 处走 DiagLog.Swallowed 但剩余 UI 容错 catch 无日志，静默失败难排障 | `src/Gdterm.UI/Controls/TabContainerControl.cs`（31 处）、`TerminalControl.cs`（54 处） |

## 发现详情

### Finding 01：RDP 密码经命令行传子进程

- 证据：`src/Gdterm.Rdp/FreeRdpClient.cs` — `args.Add("/p:" + Q(credential.Password))`（`logArgs` 侧正确脱敏为 `/p:***`，说明作者知晓敏感性，但实际 `args` 仍是明文进子进程命令行；同桌面会话任务管理器/wmic 可见）。
- 影响：本机其他进程/用户可窥见 RDP 明文密码；与 KeePass CredWrite 通道并存但命令行仍是首连默认路径。
- 建议：`cs-issue` —— 方向：优先走 KeePass CredWrite/凭证文件通道传密，命令行仅传用户名；或用环境变量+子进程继承（FreeRDP 需确认支持）。

### Finding 02：orphan 三件套编译残留

- 证据：`src/Gdterm.UI/Services/ViewModeController.cs:20,41` 仍持有 `TmuxBarPanel` 类型；`new QuickBarPanel/new StatusBarControl/new TmuxBarPanel` 全仓零实例化（v2 单栏合并后），但三文件仍在编译（TmuxBarPanel 还被 BottomBarPanel 借用 `ToolTipText2` 扩展 + QuickBarPanel 的 `DarkMenuRenderer`）。
- 影响：死代码 ~1500 行；新人易误改"看似活着"的旧底栏。
- 建议：`cs-refactor` —— 方向：先把 `ToolTipText2`/`DarkMenuRenderer` 搬到独立小文件，再删三 orphan + ViewModeController 改 Control 类型（需 CI 验，无本地编译，单走一批）。

### Finding 03：CreateGraphics 高频重复创建

- 证据：`src/Gdterm.UI/Services/FormFontPolicy.cs:114,146` — `LineBox`/`RowStep` 每次调用 `CreateGraphics` + `MeasureText`；调用方含 BottomBarPanel 按钮逐个量宽、树节点、标签页等高频路径。
- 影响：窗口 resize/字体切换/底栏刷新时 GDI 对象反复创建销毁；静态分析不能证明热点，置信度 medium。
- 建议：`cs-refactor` —— 方向：按 (font, dpi) 缓存行高，或调用方批量传参；先 profiling（diag.log 计时）再动手。

### Finding 04：剩余空 catch 无日志

- 证据：`TabContainerControl.cs` 31 处、`TerminalControl.cs` 54 处 `catch { }`；`DiagLog.Swallowed` 全仓 71 处已覆盖关键路径，但 UI 容错类 catch（如 `BeginInvoke(c.Focus())`）无日志。
- 影响：静默失败难排障；但多为焦点/绘制容错，实际危害低。
- 建议：不修也行；若修走 `cs-refactor` —— 方向：给 UI 容错 catch 加 `DiagLog.Swallowed("位置", ex)`，变量名需避 C# 7.3 CS0136（OnPaint(e) 内禁再声明 e）。

## 零发现的维度

- **arch-drift**：零偏离。Forms 零直接 `new` 业务类；RDP 经 `IRdpClientFactory`；attention 禁止事项（业务回 MainForm/TabContainer、Ctrl 抢键、新原生控件）全守。
- **功能完整性**：菜单 46/46 实现；Controls handler 全非空；右键/托盘全接线；扫描插件签名链完整。

## 下一步

1. P1：Finding 01 → `cs-issue`（RDP 凭据通道）。
2. P2：Finding 02 → `cs-refactor`（删 orphan，需 CI 验）。
3. P2：Finding 03/04 → 排期，profiling 先行 / 顺手带。

## Verification Evidence

- 范围：src/Gdterm.UI 约 105 文件（Forms 16 + Controls 34 + Services 31 + Program/Diagnostics/Models 等）+ FreeRdpClient.cs；实际回读：MenuBuilder、MainForm 回调段、TransferCenterPanel、ToolboxPanel、SftpDualPanePanel、ScanChannel、KeePassManagerForm 剪贴板段、HealthMonitorPanel、SecretScanPanel、FreeRdpClient 凭据段
- 维度：bug / security / performance / maintainability / arch-drift + 功能完整性
- agent：0（主 agent 直扫；Standard 预算允许 2，但文件集中、无需并行）
- 发现：去重前 9 候选 / 去重后 4（合并：命令行密码 security+bug 同根因取 security；空 catch 合并；M2 大文件不报；扫描 base64/剪贴板用户名明文不报）
- 主 agent 回读：文件列表见上
- 架构对照：`.codestable/attention.md` 分层约束节（UI 不直接 new 会话类、Coordinator 约束、快捷键约定）——已对照，无偏离
