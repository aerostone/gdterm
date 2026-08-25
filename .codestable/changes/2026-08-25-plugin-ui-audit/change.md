---
doc_type: change
kind: audit
slug: 2026-08-25-plugin-ui-audit
mode: standard
scope: 插件机制（src/Gdterm.Tools/Scanning/ 全部 6 文件 + 工具模块面）与界面 UI 关联功能（ScannerCenterForm / ToolsDialogsLauncher / MainFormMenuBuilder / ToolboxPanel / ToolPanelHelper / ToolRegistry 等）
created: 2026-08-25
status: closed
total_findings: 16
---

# plugin-ui-audit 审计报告

## 范围与总评

- **范围**：扫描插件机制（`ScanPluginStore` 签名链/热更新/台账、`BuiltinPlugins`、`ScanChannel` 本地+SSH 通道、`WmiScanChannel`、`ScanRunner`）及其 UI 关联面（扫描中心窗体、工具菜单/工具箱面板、工具模块注册表与配置解析助手），共 14 个核心文件 ≈ 3.2K 行，另抽查 5 个工具模块的阻塞调用。
- **模式**：Standard，全 5 维（安全/正确性/性能/维护性/架构漂移）。2 个并行 reviewer 子代理 + 主 agent 全量回读。
- **结论**：签名验证链本身设计正确（双哈希信封、官方公钥钉死、Invalid 硬拒绝、台账按 id+内容哈希记账）；UI 线程封送、快捷键约定、图标工厂、字体策略均合规。主要问题集中在三类：**① UI 线程同步阻塞**（端口扫描器、Keepass 改密走 `.GetAwaiter().GetResult()`）、**② 资源泄漏/生命周期缺口**（ToolboxPanel 面板堆叠不释放、共享 ScanPluginStore 永不 Dispose、SSH 通道无超时）、**③ 若干确定性 bug**（`byte[] ==` 引用相等导致内置签名永不补写、ExtractBool 解析缺陷）。无 P0。

## 发现清单

| # | 性质 | 严重度 | 置信度 | 标题 | 位置 |
|---|---|---|---|---|---|
| 01 | bug | P1 | high | `byte[] ==` 引用相等恒 false，内置插件 .sig 永不补写 | src/Gdterm.Tools/Scanning/ScanPluginStore.cs:393 |
| 02 | performance | P1 | high | SSH 扫描通道完全忽略 timeoutSeconds，远端挂死则扫描永久卡住 | src/Gdterm.Tools/Scanning/ScanChannel.cs:232 |
| 03 | bug/performance | P1 | high | 扫描中心 async void 无 try/finally：异常后 _running 恒 true，关窗后回调打在已释放句柄上 | src/Gdterm.UI/Forms/ScannerCenterForm.cs:405,451 |
| 04 | performance | P1 | high | ToolboxPanel 切换工具时旧 ActionPanel 从不 Dispose，逐次堆叠泄漏 | src/Gdterm.UI/Controls/ToolboxPanel.cs:177 |
| 05 | performance | P1 | high | 端口扫描器 onRun 在 UI 线程 `.GetAwaiter().GetResult()` 同步跑完整个扫描 | src/Gdterm.Tools/Modules/PortScannerTool.cs:172 |
| 06 | performance | P1 | high | Keepass 改主密码在 UI 线程同步等待异步迁移 | src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:171 |
| 07 | security | P2 | medium | 验签与执行之间无锁定，TOCTOU 窗口内可换脚本 | src/Gdterm.Tools/Scanning/ScanPluginStore.cs (Reload→RunOne) |
| 08 | security | P2 | high | 插件目录前缀校验无尾部分隔符，同级目录名前缀可绕过 | src/Gdterm.Tools/Scanning/ScanPluginStore.cs:138 |
| 09 | arch-drift | P2 | high | 共享 ScanPluginStore 为 UI Services 静态单例且从不 Dispose | src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:40 |
| 10 | bug | P2 | high | ExtractBool 不跳冒号后空格；IndexOf 从头搜致多布尔键误读 | src/Gdterm.Tools/ToolConfigBase.cs:79 |
| 11 | arch-drift | P2 | medium | Gdterm.Tools 模块面直接返回 WinForms Control，库耦合 UI 框架 | src/Gdterm.Tools/IToolModule.cs:26 |
| 12 | maintainability | P2 | medium | 多处裸 `catch {}` 吞异常不走 DiagLog，含加密密钥迁移静默失败 | ToolsDialogsLauncher.cs:108,116,140 等 8 处 |
| 13 | maintainability | P2 | low | BuiltinPlugins 版本刷新只备份脚本不备份 manifest，用户改动静默丢失 | src/Gdterm.Tools/Scanning/ScanPluginStore.cs (RefreshOutdatedBuiltin) |
| 14 | performance | P2 | medium | WMI 通道 stderr 读失败会覆盖 stdout 错误信息；远端超时不杀进程 | src/Gdterm.Tools/Scanning/WmiScanChannel.cs |
| 15 | maintainability | P2 | low | 台账实际落盘路径与文档不一致（data\plugins\config\ vs data/config/） | ScanPluginStore.cs:250 vs attention.md |
| 16 | maintainability | P2 | low | 死代码/小疵：AppendLine 与 AppendRawLine 重复、菜单图标复用、每次选中 new 会话包装探测 | ScannerCenterForm.cs:529 等 |

## 发现详情

### Finding 01：`byte[] ==` 引用相等恒 false，内置插件 .sig 永不补写

- 证据：`src/Gdterm.Tools/Scanning/ScanPluginStore.cs:393` — `return File.ReadAllBytes(path) == new UTF8Encoding(false).GetBytes(expected);`（`FileMatchesContent`）
- 影响：C# 数组 `==` 比较引用而非内容，该断言恒为 false → `BackfillPristineSignature`（L395-408）对缺失 .sig 的官方内置插件永远不补写签名文件。用户删除 .sig 后插件从 Trusted 降级为 Unsigned，"首次使用确认"被反复触发；若后续逻辑依赖补写结果做幂等判断也会随之失真。确定性 bug，非竞态。
- 建议：cs-issue → 改用 `SequenceEqual` 或逐字节比较。

### Finding 02：SSH 扫描通道忽略超时，远端挂死即永久卡住

- 证据：`src/Gdterm.Tools/Scanning/ScanChannel.cs:232-270` — `SshScanChannel.Execute(plugin, content, timeoutSeconds)` 方法体内未引用 `timeoutSeconds`；执行走 `_session.RunCommand(...)`（SSH.NET 无内建超时）。对照 `LocalScanChannel.Execute`（L49）正确把 timeout 传给 `RunProcess` 并在超时后 Kill。
- 影响：远端 Linux/Windows 主机上脚本挂起（NFS 卡死、交互提示漏网等）时命令永不返回；上层 `ScannerCenterForm.OnRunClicked` 的 `_running = true` 无法复位，扫描中心永久不可用，只能重启应用。manifest 里配置的 TimeoutSeconds 对 SSH 目标静默失效。补充：接口层 `src/Gdterm.Tools/ISshRemoteSession.cs:15` 的 `RunCommand(string command)` 本身无超时/取消参数，修复需扩展接口签名或通道层计时中断。
- 建议：cs-issue → RunCommand 包 CancellationToken + 异步任务超时，或通道层起计时器超时后中断命令并报 RuntimeError。

### Finding 03：OnRunClicked 无 try/finally，异常后按钮永久禁用

- 证据：`src/Gdterm.UI/Forms/ScannerCenterForm.cs:405`（`private async void OnRunClicked` 设 `_running = true; _runButton.Enabled = false;`）、L451-462（结尾恢复状态，但中间无 try/finally）
- 影响：任一插件运行或渲染抛出未捕获异常时 `_running` 恒为 true、"运行"按钮永久禁用；async void 异常还会直接打进消息循环。批量结束才统一渲染结果，中途关窗体则续跑的回调访问已释放控件（有 IsHandleCreated 检查的仅限 reload 封送路径）。
- 建议：cs-issue → try/finally 包裹全程并在 finally 复位状态；窗体关闭时对进行中的 Task 做取消或放弃标记。

### Finding 04：ToolboxPanel 切换工具堆叠泄漏 ActionPanel

- 证据：`src/Gdterm.UI/Controls/ToolboxPanel.cs:177-178` + `src/Gdterm.UI/Controls/ToolPanelHelper.cs:18-125` — 移除旧控件时只 Dispose `UserControl` 分支；但各工具模块经 `CreateActionPanel` 返回的是普通 `Panel`，落入"其他类型仅 Remove 不 Dispose"路径。
- 影响：每切换一次工具就在右侧容器残留一个 Dock.Fill 的 Panel 及其子控件树（按钮/文本框/事件订阅），长时间操作内存与句柄持续增长；旧面板虽不可见但仍挂在 Controls 集合中。
- 建议：cs-refactor → 移除后统一 `Dispose()` 旧面板（或改为先从父容器摘除再显式释放）。

### Finding 05：端口扫描器在 UI 线程同步跑完整个扫描

- 证据：`src/Gdterm.Tools/Modules/PortScannerTool.cs:172,176` — onRun 回调内 `ScanAsync(host,start,end).GetAwaiter().GetResult()` / `ScanCommonPortsAsync(host).GetAwaiter().GetResult()`；`src/Gdterm.UI/Controls/ToolPanelHelper.cs:102-108` 直接在 Click 处理器里同步 `onRun?.Invoke(...)`
- 影响：自定义范围最多 1024+ 端口逐一探测期间整个主窗体冻结（白屏、无法切换会话），网络差时可达分钟级。同模式还见于 CertificateInstallerTool（certutil 外部进程）、TimeSyncTool（远程时间同步），单次较短故降级提及。
- 建议：cs-issue → onRun 契约改为 async，或 ToolPanelHelper 统一包 `Task.Run` 后封送回 UI 渲染。

### Finding 06：Keepass 改主密码 UI 线程同步等待

- 证据：`src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:171-173` — `ChangeMasterPasswordAsync(...).GetAwaiter().GetResult()`
- 影响：改密涉及完整数据库重加密 + 写盘，期间 UI 冻结；大库或慢磁盘下体验明显劣化。项目约定网络/长操作必须异步。
- 建议：cs-issue → await 化（对话框按钮处理器本就可 async void + 忙碌指示）。

### Finding 07：验签与执行之间的 TOCTOU 窗口

- 证据：信任判定在 `Reload()` 时基于当时读到的 manifest/script 计算；执行时 `ScanRunner.RunOne` 重新 `File.ReadAllText(plugin.ScriptPath)`。两次读取之间文件可被替换（FileSystemWatcher 800ms 去抖进一步拉宽窗口）。
- 影响：本地攻击者若已能写入插件目录（默认需用户权限），可在验签后、执行前换入恶意脚本绕过 Invalid 硬拒绝。攻击前提是同用户级写权限，实际提权有限，故 P2 而非 P1。
- 建议：cs-refactor → 执行前对脚本字节重算哈希并比对 Trust 记录，或验证后以只读句柄/快照内容传给通道执行。

### Finding 08：插件目录前缀校验缺尾分隔符

- 证据：`src/Gdterm.Tools/Scanning/ScanPluginStore.cs:138` — `scriptPath.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase)` 未确保 `dirFull` 以 `\` 结尾
- 影响：经典前缀绕过——`data\plugins\scanner\evil` 与 `data\plugins\scanner-extra` 同级时可互相通过校验。因校验对象是插件自身目录（写入者本已控制该子树），提权增益有限，属加固项。
- 建议：cs-issue → 校验 `scriptPath.StartsWith(dirFull + Path.DirectorySeparatorChar)` 或用 `Path.GetFullPath` 后做段级比较。

### Finding 09：共享 ScanPluginStore 为静态单例且从不 Dispose

- 证据：`src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:40-41` — `public static ScanPluginStore SharedScanPluginStore { get; } = new ScanPluginStore();`；`AppShutdownCoordinator`（MainForm 关闭链）释放 hotkeys/tabs/tunnels/keepass/security 但不含它。
- 影响：两个 FileSystemWatcher + 去抖 Timer 存活至进程退出（绿色版单窗体场景危害小，但违背项目"服务组合根构造注入、显式释放"约定；也使单元测试难以隔离）。
- 建议：cs-refactor → 移交 Program.cs 组合根持有并注入，纳入 AppShutdownCoordinator 释放清单。

### Finding 10：ExtractBool 手写 JSON 解析缺陷

- 证据：`src/Gdterm.Tools/ToolConfigBase.cs:79-82` — `if (json.IndexOf("true", c) == c + 1) return true;`：① IndexOf 从字符串头搜索而非从值起点 `c + 1` 搜索，JSON 中更早位置出现过 `"true"` 字面量时后续布尔键全部误判回默认值；② 未跳过冒号后的空白，`{"k": true}` 解析失败返回 default。对照 ExtractString/ExtractInt（L60-79）定位冒号的写法正确，唯独此函数偏离。
- 影响：工具模块 JSON 配置中的布尔开关可能静默取错值（行为回退默认而非报错）。触发需要特定键序/空格风格，真实配置可构造出来。
- 建议：cs-issue → 按 ExtractInt 同款模式从 `c + 1` 起跳过空白后再匹配字面量。

### Finding 11：Gdterm.Tools 库面直接暴露 WinForms 类型

- 证据：`src/Gdterm.Tools/IToolModule.cs:26` — `CreateToolControl` 返回 `System.Windows.Forms.Control`；Gdterm.Tools 引用 System.Windows.Forms。
- 影响："库不依赖 UI 框架"边界被打破：库工程绑定桌面框架，未来移植/测试需整体重构。当前 net462 单体下无运行时危害，属架构债。注意与 D3"库内置模块非插件加载"的旧表述不同——扫描系统已插件化，ARCHITECTURE.md 该条目待更新（文档漂移一并记录于 Finding 15 关联背景）。
- 建议：cs-refactor → 长期看抽象为视图模型/工厂接口由 UI 层实现；短期至少在 ARCHITECTURE.md 如实记录现状。

### Finding 12：裸 catch{} 吞异常，含加密迁移静默失败

- 证据：`src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:108,116,140`（L140 位于 `UpgradeSecretsToMasterKey` 加密迁移路径）、`ToolboxPanel.cs:141`、`ScannerCenterForm.cs:225,229,242`、`src/Gdterm.Tools/Scanning/ScanRunner.cs:38`
- 影响：项目约定错误统一走 DiagLog；裸吞使故障（尤其密钥升级失败这种安全相关事件）无迹可循，排障成本高。
- 建议：cs-refactor → 统一补 DiagLog.Warn/Error；迁移失败应向用户可见。

### Finding 13：BuiltinPlugins 升级刷新不备份 manifest

- 证据：`RefreshOutdatedBuiltin`（ScanPluginStore.cs 内）：版本更新时仅将现有脚本复制为 `.bak` 后覆盖，manifest 直接覆盖。
- 影响：用户对 manifest 的本地修改（如临时禁用某插件 Enabled=false）随版本升级静默丢失，且无备份可查。
- 建议：cs-refactor → manifest 一并留 `.bak`，或在覆盖前 diff 提示。

### Finding 14：WMI 通道错误信息覆盖与远端残留

- 证据：`src/Gdterm.Tools/Scanning/WmiScanChannel.cs` — TryReadFile 读 stderr 失败时覆盖 `output.RuntimeError`（stdout 的错误提示丢失）；轮询超时分支不终止远端 Win32_Process（仅在文案中告知 PID）。
- 影响：诊断信息互相吞没；超时后远端 powershell 进程与三个临时文件残留至下次清理失败。
- 建议：cs-refactor → RuntimeError 用 Append 语义合并；超时分支尽力 Terminate 远端进程。

### Finding 15：批准台账落盘路径与文档不一致

- 证据：`src/Gdterm.Tools/Scanning/ScanPluginStore.cs:250` — LedgerPath = `<exe>\data\plugins\config\scanner-approved.json`（由 GetDirectoryName(_userRoot) 推导）；`.codestable/attention.md` 记载为 `data/config/scanner-approved.json`。
- 影响：运维按文档找台账文件会扑空；备份/清理脚本若针对文档路径写则会遗漏真实文件。属文档漂移，代码自洽。
- 建议：cs-refactor → 修正 attention.md/架构文档表述（顺带更新 D3 插件化表述）。

### Finding 16：小疵集合

- 证据：`src/Gdterm.UI/Forms/ScannerCenterForm.cs:529-535` — `TextBoxExtensionsForScanner.AppendLine` 与 `AppendRawLine` 功能重复（死代码候选）；`MainFormMenuBuilder.cs` 扫描中心与敏感信息扫描共用同一 eye 图标（辨识度）；`ScannerCenterForm.cs:344-359` — `RemoteSessionAvailable` 每次列表选中变化都 `new SshNetRemoteSession` 包装探测（轻量但高频冗余）；`ScanRunner.PluginCompleted` 增量事件通道当前无订阅者（预留接口闲置）；`ToolPanelHelper.cs` 存在从未加入父容器的孤儿 TextBox。
- 影响：均为低风险维护性问题。另有一条加固线索：Unsigned 首跑确认全仓唯一调用点为 `ScannerCenterForm.cs:422,430`（UI 层把关），`ScanRunner` 仅硬拒 Invalid 不校验台账——当前扫描中心是唯一执行入口故无洞，但未来新增执行入口时需自行补确认逻辑。
- 建议：cs-refactor → 合并扩展方法、区分图标；其余记录即可，勿急于删（PluginCompleted 可能是增量渲染的预留设计）。

## 下一步

按优先级排序（修复一律另开 cs-issue / cs-refactor，不在本审计内推进）：

1. **P1 批次（建议本迭代）**：F01 byte[] 比较、F02 SSH 超时、F03 OnRunClicked try/finally、F05/F06 UI 线程阻塞 —— 五项都是确定性缺陷且影响日常可用性。
2. **P2 安全加固批次**：F07 TOCTOU 重验、F08 目录前缀校验 —— 小改动，可与 F01 同一变更顺手处理。
3. **P2 结构批次**：F04 面板释放、F09 组合根收编、F10 ExtractBool、F12 日志补齐。
4. **P2 低优**：F11/F13/F14/F15/F16 排期处理；F15 含 ARCHITECTURE.md 文档更新。

## Verification Evidence

- 范围：14 个核心文件全量读取（src/Gdterm.Tools/Scanning/ 6 文件、IToolModule.cs、ToolRegistry.cs、ToolConfigBase.cs、ToolPanelHelper.cs、ScannerCenterForm.cs、ToolsDialogsLauncher.cs、MainFormMenuBuilder.cs、ToolboxPanel.cs），另 grep 抽查 PortScannerTool/CertificateInstallerTool/TimeSyncTool/RepoConfigTool 的 onRun 实现、ConnectionTreeControl（确认与 toolbox/tools 零耦合）、Program.cs/AppShutdownCoordinator/SshRemoteSession.Wrap 调用点。
- 维度：安全、正确性、性能、维护性、架构漂移（全 5 维）。
- agent：dispatched 2 / returned 1 成功 + 1 失败（del_mt89pf8c_gowf UI 侧成功并入；del_mt89pf8c_12fz 插件机制侧读完 6 文件后在产出报告前超时被 watchdog 终止，exit 143；其范围由主 agent 自查完全覆盖，其未竟的两个跨文件契约——ISshRemoteSession.RunCommand 无超时参数、IsApproved 唯一调用点在 UI——已由主 agent 补验并写入 F02/F16，未重新派发）。
- 发现：去重前 21 / 去重后 16（合并同根因：UI 阻塞类 3 条独立保留因分属不同模块、子代理与主 agent 重复发现 5 条归并）。
- 主 agent 回读：全部 14 个核心文件的原始代码逐行核对行号与上下文；关键 finding（01/02/05/06/10）二次回读精确到行。
- 架构对照：.codestable/architecture/ARCHITECTURE.md（D2/D3 条目、分层规则）、.codestable/attention.md（插件签名机制记载）、.codestable/reference/shared-conventions.md（落盘规范）；发现两处文档与代码不一致（D3 表述滞后、台账路径），见 F11/F15。
