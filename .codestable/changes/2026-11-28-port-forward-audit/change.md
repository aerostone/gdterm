---
doc_type: change
kind: audit
slug: 2026-11-28-port-forward-audit
mode: standard
scope: 可视化端口转发面板、本地与远程转发、SOCKS5 入口、活动 SSH 绑定、侧栏及回收站生命周期
created: 2026-11-28
status: closed
total_findings: 9
---

# 端口转发功能审计

## 范围与总评

下一项选择「终端 → 端口转发」。独立 standard 审计，9 项确认发现（5 项 P1、4 项 P2），无 P0；本轮不修改端口转发业务代码。界面有入口、基本本地转发可用，但动态转发、规则生命周期、会话绑定和状态恢复尚不完整。既有功能地图 100% 是页面级登记，不能代表网络功能已经验证。

跳板认证链上轮已专门检查，本轮仅追踪端口转发复用活动 SSH 客户端的边界，不重复审查整个 TunnelManager。

## 发现清单

| ID | 性质 | 严重度 | 置信度 | 用户可见问题 | 代表位置 |
|---|---|---|---|---|---|
| PF01 | bug | P1 | high | SOCKS5 可以添加，启动必然失败 | `src/Gdterm.UI/Controls/PortForwardPanel.cs:244` |
| PF02 | bug | P1 | high | 关闭/换工具面板即停转发、丢规则；回收站恢复会落入已释放面板 | `src/Gdterm.UI/Services/SidePanelHost.cs:63` |
| PF03 | bug | P1 | high | 连接切换/重连后仍使用打开面板时的旧 SSH 客户端 | `src/Gdterm.UI/Services/SidePanelFactory.cs:177` |
| PF04 | bug | P1 | high | SSH 断开后仍报告运行，原 ID 阻止再次启动 | `src/Gdterm.Tunnel/PortForwardManager.cs:144` |
| PF05 | bug | P1 | high | 远程转发同步等待服务器，阻塞 UI 线程 | `src/Gdterm.Tunnel/PortForwardManager.cs:87` |
| PF06 | bug | P2 | high | 端口/地址缺校验，错误无原因；空端口被保存为 0 | `src/Gdterm.UI/Controls/PortForwardPanel.cs:199` |
| PF07 | bug | P2 | high | 启动失败不清理；面板释放后 SSH 客户端仍保留转发对象 | `src/Gdterm.Tunnel/PortForwardManager.cs:65` |
| PF08 | bug | P2 | high | 勾选、选择与批量启停/删除的范围不一致 | `src/Gdterm.UI/Controls/PortForwardPanel.cs:251` |
| PF09 | bug | P2 | high | 保存后没有编辑入口，地址写错只能删掉重建 | `src/Gdterm.UI/Controls/PortForwardPanel.cs:76` |

## 发现详情

### PF01：SOCKS5 仅有选项，没有执行分支

- 触发：添加规则时选择 Dynamic（SOCKS5），连接有效 SSH 后点击启动。
- 证据链：`PortForwardPanel.cs:181` 提供第三个选项，`:208` 保存 Dynamic 枚举；`:244–247` 只调用 Local/Remote，Dynamic 直接 false 并显示启动失败。`PortForwardManager.cs` 无动态转发启动方法。
- 影响：界面承诺的代理能力无法使用，任何有效输入都无法成功。
- 建议：接入 SSH.NET 动态转发；按类型切换字段，只显示 SOCKS 所需监听地址/端口。

### PF02：转发生命周期绑在临时面板上

- 触发：已添加/启动转发后关闭面板、打开命令历史/其他侧栏，或再次点击端口转发入口。
- 证据链：`MainForm.cs:545` 每次新建面板 → `SidePanelFactory.cs:177` 每次新建 manager → `SidePanelHost.cs:69,101` 释放原面板 → `PortForwardPanel.cs:293` 释放 manager 停止所有转发；规则只在该实例的 `_rules`（`:24`），重开为空。
- 回收站同根因：`:40–48` 注册的恢复闭包捕获面板 `_rules`，只在 UI 尚存时刷新。打开回收站会释放原面板，`TrashStore.cs:121` 调用闭包并按无异常判成功，规则被加回不可见的旧实例；回收站条目已移除，重开端口转发仍为空。
- 影响：只想收起界面或查看另一工具，也会中断数据库/Web 连接并丢失配置；「恢复」可能显示成功但规则不回来。即使产品只要求会话内保存，也不应在同一次应用会话内因切侧栏丢失。
- 建议：规则仓库与运行转发归应用/SSH 会话所有；视图仅订阅和展示，关闭视图与停止任务分开。

### PF03：界面没有可靠的 SSH 归属与重绑定

- 触发 A：先打开面板，再连接 SSH；B：在 SSH A 下打开面板后切到 B；C：A 断开重连创建新客户端。
- 证据链：`SidePanelFactory.cs:179–183` 只在创建时绑定；`ActiveSessionBridge.cs:54` 只查询一次，且 null 不清空；`PortForwardPanel.cs:232–246` 启动时读取旧 `_host`，无当前会话查询、连接选择或连接名称展示。`SshPortForwardHost.cs:13` 持有 readonly 客户端；全仓调用搜索没有切标签/重连事件更新该面板。
- 影响：A 情形一直提示先连接；B 情形可能把针对 B 的目标地址交给 A 转发且用户看不出归属；C 情形持续引用断开的旧对象。模型有 ConnectionId，但添加和启动均未使用它。
- 建议：规则明确归属 SSH 连接并展示名称；启动时解析有效客户端，响应断线与重连；不要让既有运行规则随标签切换静默换服务器。

### PF04：运行状态只是字典是否有 ID

- 触发：成功启动转发后 SSH 断开，再查看状态或用同一规则重试。
- 证据链：`PortForwardManager.cs:146` 只查 `_activeForwards.ContainsKey`，不检查 `IsStarted`/SSH 连接；无转发停止/异常回调移除条目。`:62,82` 以同一字典阻止重复启动。`PortForwardPanel.cs:155` 直接显示该状态，且没有订阅 `ForwardStatusChanged` 或断线事件。
- 动态验证：实际 SSH 转发可读取目标 SSH banner；断开后底层 port 已停止、manager 仍为 active；将原规则交给另一个已连接客户端仍返回 false。
- 排除：显式 Stop 可清理断线记录，不能声称「永远无法停止」；问题是断线未自动归位及普通重试失败。
- 建议：由真实连接和端口状态驱动运行/停止/错误；记录停止原因并允许重连重试。

### PF05：远程端口申请阻塞整个界面

- 触发：启动 Remote 转发时服务器响应慢或链路暂时不响应。
- 证据链：按钮 Click → `StartSelected` 同步调用 `StartRemote` → `port.Start()` 同步等待 SSH global request，整段在 UI 线程。无 busy 状态、取消或异步调度。
- 动态验证：只暂停临时 SSH 会话子进程 1 秒，随后自动恢复；生产 `StartRemote` 同步占用调用线程 1008 ms 后成功。没有暂停系统 SSH 服务或用户连接。
- 影响：真实网络延迟期间窗口无法响应输入、切换、关闭；长等待放大到 SSH 超时。
- 建议：把启动/停止网络工作移出 UI 线程，展示忙态与具体失败原因，避免重复点击和关闭竞态。

### PF06：保存前没有有效校验，出错后没有修正指引

- 触发：空端口、非数字、负数、超过 65535、空地址，或本地端口被其他服务占用。
- 证据链：`PortForwardPanel.cs:191` 保存按钮直接 DialogResult.OK；`:202–203` 忽略 TryParse 返回值，将失败解析当成 0；`:209` 原样保存；manager `:65,85` 强转 uint 交给 SSH.NET，所有异常在 `:72,92` 被抹成 false；UI `:247` 仅显示「启动失败」。
- 动态验证：本地端口 0 真正启动在系统分配的非零端口，但 `rule.LocalPort` 仍是 0，`:153` 的列表因此展示无法使用的地址。占用端口重试也无法获知占用原因。
- 建议：保存前校验必填/端口范围和各转发方向；若支持自动端口就显式选项并回填实际 BoundPort；失败保留输入并给字段级原因。

### PF07：失败和释放路径没有从 SSH 客户端完整撤销

- 触发：端口被占用时反复点击启动；或在 SSH 仍连接时释放转发 manager。
- 证据链：`PortForwardManager.cs:66` 先 AddForwardedPort，再 Start；Start 抛出后 catch 不 Remove/Dispose。Remote `:86` 同类。Dispose `:155` 只 Stop/Dispose port，没有从原 client.RemoveForwardedPort。
- 动态验证：占用端口启动连续失败 3 次，真实 `SshClient.ForwardedPorts` 增加 3 个；释放含一条转发的 manager 后，仍连接的客户端集合保留 1 个已释放对象。
- 影响：长期 SSH 会话累积无效对象和注册关系，失败重试越多残留越多。未测内存曲线，不声称固定泄漏大小或性能瓶颈。
- 建议：每条运行记录保留原始 owner；启动失败在 finally 回滚，停止/释放幂等撤销 owner 注册并释放对象。

### PF08：批量操作没有统一目标集

- 触发：勾选 A/B 后选中 C 再停止；或无勾选/选择时点击删除。
- 证据链：`PortForwardPanel.cs:95` 有复选框，注释声明批量停止/删除；`:234,242,253–255` 启停只操作第一个 SelectedItems，忽略勾选；删除 `:261` 调 ListShell.SelectedOrChecked，该方法 `ListShell.cs:293` 返回选中与勾选的并集，无两者时退回全部可见项。
- 影响：勾选的任务未停止，可能停了另一条；无选择的删除会弹「删除选中的 N 条」而实际是全部可见项。存在二次确认，因此不描述为无提示删除。
- 建议：统一“勾选优先，否则当前选择”，无选择禁用动作；批量报告逐条成功/失败，启动后保留选择。

### PF09：无法编辑已保存规则

- 触发：需要改端口、目标主机、名称或说明。
- 证据链：`PortForwardPanel.cs:76–86` 只有添加/启动/停止/删除，`:105–116` 双击与回车用于启停；唯一规则写入入口 `AddRule`，没有编辑或加载既有规则到表单。列表有说明列 `:103`，创建表单 `:180–188` 却没有说明输入。
- 影响：一个端口输入错误就必须删除重建；复杂地址要反复填写，也无法为同类规则记用途。
- 建议：增加明确编辑入口和说明字段；运行中修改先停止/重建或明确禁止，避免列表配置与实际转发不一致。

## 已核查并排除

- 基础 Local 不是整体不可用：实连启动且读取真实目标 SSH banner 成功。
- 断线后手动 Stop 可以清理旧记录，淘汰“停止永久失败”候选。
- ListView 和 ListShell 都订阅激活事件，但第一次 RefreshList 重建行会清除原选择，未证实必然双重启停，不列正式发现。
- 既有 UI 证据覆盖空态、窄窗和添加弹窗 9/14pt、取消路径；不能说完全没有 UI 测试，也不能用这些证明真实转发与保存往返完整。

## 验证证据与覆盖

- 直接完整读取 9 个文件：PortForwardPanel、PortForwardManager、ISshPortForwardHost、SshPortForwardHost、PortForwardRule、ActiveSessionBridge、TabActiveSessionQuery、SidePanelHost、TrashStore。
- 切片读取 7 个文件：SidePanelFactory、MainForm、TabContainerControl、ListShell、UiPageCatalog、UiStateSmoke、ButtonEditorsSmoke；不把一次搜索结果算全文件审查。
- 文档对照：`.codestable/architecture/ARCHITECTURE.md:80,105,150,233`、`docs/FEATURE-MAP.md` 的端口转发条目；架构写明活动会话桥已接入，但不等于已验证重绑定。
- 维度：正确性、安全（连接归属/监听地址）、性能（同步阻塞/资源清理）、维护性、架构边界；未发现可确认 P0，不以样式偏好或行数计问题。
- 主 agent 独立复核，子 agent 0。候选 11，淘汰 2，去重后 9。
- 实连：生产源码链接到临时 net8.0 项目，SSH.NET 2026.0.0；隔离回环 OpenSSH，13 项观察全部匹配，命令最终退出 0。源码、可重跑脚本和日志在 `evidence/`。
- UI：查看 GitHub run 46138278324 / 644edce 的添加规则大字号截图，另保留普通字号与空态截图；该模块源码与本轮 HEAD 相同。未修改端口转发业务代码或为其发起新 Windows 功能测试。
- 限制：Linux 实连不是 net462/Windows 端到端；未实测用户服务器、未操作真实凭据、未运行鼠标键盘完整网络流程、未做公网 IPv6/SOCKS 兼容性验证。

## 下一步

先完成规则/运行实例与视图生命周期分离、连接归属和断线状态，再补 SOCKS5、异步启停及校验；随后统一批量操作、编辑与回收站恢复，并增加 Windows 保存/重开/切面板、三种转发实连及断线恢复验收。本报告只给修复方向，不把审计结论冒充已完成实现。
