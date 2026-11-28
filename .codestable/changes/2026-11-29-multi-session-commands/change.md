---
doc_type: change
kind: feature
slug: 2026-11-29-multi-session-commands
status: in-progress
mode: standard
summary: 完善多主机会话选择、广播发送、SSH 批量命令结果与取消、录制查看
tags: [ssh, broadcast, batch, ui]
risk:
  level: high
  reasons: [remote_commands, cancellation, concurrency]
contract:
  include:
  - src/Gdterm.Terminal/BatchCommandExecutor.cs
  - src/Gdterm.Terminal/MultiChannelManager.cs
  - src/Gdterm.Terminal/MultiChannelRecorder.cs
  - src/Gdterm.Terminal/TerminalReadyDetector.cs
  - src/Gdterm.Terminal/TerminalSession.cs
  - src/Gdterm.Terminal/Gdterm.Terminal.csproj
  - src/Gdterm.UI/Gdterm.UI.csproj
  - src/Gdterm.UI/Controls/BatchCommandPanel.cs
  - src/Gdterm.UI/Controls/MultiChannelPanel.cs
  - src/Gdterm.UI/Services/SidePanelFactory.cs
  - src/Gdterm.UI/Services/TabActiveSessionQuery.cs
  - src/Gdterm.Core/L10n/L10n.cs
  - src/Gdterm.Tests/**
  - tools/multi-session-smoke/**
  - tools/port-forward-smoke/SessionQueryFixture.cs
  - tools/ui-baseline/**
  - tools/ui-matrix-ratchet.txt
  - .github/workflows/ci.yml
  - docs/FEATURE-MAP.md
  - .codestable/architecture/ARCHITECTURE.md
  - .codestable/changes/2026-11-29-multi-session-commands/**
  architecture_impact: update
  architecture_reason: 现有 Terminal 多会话执行与录制职责不迁移；明确 SSH exec 与交互广播的语义及生命周期。
  architecture_refs: [ARCHITECTURE.md]
  requirement_impact: unchanged
  requirement_reason: 完善既有多主机操作能力，用户已按上轮五项范围要求开发。
  requirement_refs: []
  context_refs:
    design: [src/Gdterm.Terminal/BatchCommandExecutor.cs, src/Gdterm.UI/Services/SidePanelFactory.cs]
    impl: [src/Gdterm.Terminal/BatchCommandExecutor.cs, src/Gdterm.Terminal/MultiChannelManager.cs]
    accept: [src/Gdterm.Tests/Ui/UiPageCatalog.cs]
  artifacts: []
  evidence_ledger: false
  preexisting_changes: []
  baseline:
    git_head: 6a0d11354f8e20d3e0ecfd4f19dbe5794888e90f
    dirty_hashes: {}
---

# 多主机命令操作完善

## 目标与边界

承接用户“那请你开发”与上一轮确定的会话选择、广播输入、批量执行、操作保护、结果界面五项范围。用户已经授权实现、验证以及既有提交/推送/CI闭环，本轮持续处理，无未决产品选择。旧 roadmap 多通道增强已为 done，本次是既有功能可靠性增量，不重启历史任务。

广播是向勾选交互终端提交完整命令；批量命令是多个 SSH 主机上独立执行同一脚本并比较结果。不是键盘实时镜像；不新增任务调度或服务器管理平台。

## 设计与契约

现状证据：
- BatchCommandExecutor.ExecuteOnSession 以超过两段后空输出判断完成，但 TerminalSession.ProcessOutput 丢弃空字符串；实连通常全超时。事件在 SendInput 抛错时不摘除，耗时恒为 timeout。
- BatchCommandPanel 仅创建时 SetSessions，Enter 绕过 disabled 按钮，缺取消/finally，结果仅取20段；历史新记录插首、Up却从尾读。
- MainForm.OnActiveSessionChanged 会同步广播，不能说完全没有同步；连接异步完成和不切标签时无刷新。批量面板未订阅。分屏前后 GetConnectedSessions ID 改变。
- TerminalReadyDetector 的 ':' 和 '>' 子串规则误拒 Bash/PowerShell/CMD 提示符；TerminalSession 最近输出未包含尚未换行的提示符。
- 广播同步 WaitAll 阻塞UI，超时返回可继续被后台修改的列表；取消确认依旧清输入；UI勾选延迟用索引会在过滤/重排后错指会话。
- 录制写虚构 broadcast ID，无会话输出订阅；回放查不到ID仍报成功，若真有匹配直接重发输入，缺统一闸门；JSON靠首个']'截断正文。

方案：
1. 保留 Terminal/Manager/Recorder 与 UI/SidePanelFactory 归属。使用已有 SSH.NET ExecuteAsync(ct)，逐主机创建/释放 SshCommand；复用共享客户端，绝不关闭客户端。返回退出码、stdout、stderr、实际耗时、失败/取消/超时。并发上限8；取消排队项也返回结果。只列真实 SSH 会话；界面解释独立shell，不继承交互目录/变量。拒绝继续用prompt猜执行成功，拒绝另建SSH连接和凭据入口。
2. 广播异步快照目标及会话对象，发送前复核注册对象和连接；无目标/未就绪/断线/替换/取消有结果。单面板防重复发送。发送成功代表已提交终端，不冒充命令执行成功。保存拒绝/失败输入，成功才清空。
3. 会话提供器在面板存活时以UI timer刷新，执行前刷新；保留相同ID且相同对象选择，重连替换必须重新选。分屏使用稳定会话对象ID。过滤时全选/取消仅当前可见行；每次提交前显示选中目标，包括隐藏勾选计数。
4. 就绪检测剥离ANSI并识别实际最后提示符，保留密码、REPL、交互确认保护；SSH最近输出包含未换行尾部。
5. 录制绑定实际目标输入/输出；关闭面板停止并摘事件，应用持有录制数据。回放只查看时间线，取消可用，绝不向主机重放。文件使用框架JSON序列化，保留v1格式，损坏读取不清空当前记录；HTML转义会话名和内容。
6. 批量多行输入，Ctrl+Enter提交、Alt+Up/Down历史，完整有界结果展示/复制/导出；超限提示。按钮用既有字体尺寸工厂；窄栏上下布局，状态文案可换行。危险命令由 DangerousCommandPrompt.Confirm + InputSource 统一处理，目标快照在确认前固定。

## 行为增量

- MODIFIED：连接/断线/重连/分屏后列表自动刷新，选择不转移到替换会话；同名主机可区分。
- MODIFIED：广播已发送/跳过/失败按目标可见，未成功提交不丢输入；中文、多行换行只追加一个提交回车。
- MODIFIED：SSH批量结果按退出码判成功；空输出成功、stderr/非零失败、断线、超时、取消分别呈现。
- ADDED：批量任务取消、执行期间防重复、逐主机结果、超时设置、多行脚本、复制/导出结果。
- MODIFIED：录制保存实际会话ID及输入输出，回放是可取消的时间线查看，无远程副作用。

## 执行计划

- [ ] S1：执行器与就绪/身份，独立SSH实连证明成功、失败、超时、取消和共享client存活。
- [ ] S2：广播异步状态与录制生命周期，替身断言选择/替换/重排安全及录制往返。
- [ ] S3：两面板完整交互、状态反馈、布局，接入工厂及会话刷新。
- [ ] S4：原生单测/专项实连/Windows交互冒烟与矩阵，检查截图后更新相关基线。
- [ ] S5：回写架构/证据，提交推送并监控GitHub与AppVeyor，失败继续修复。

## 验证计划

既有无多通道/批量行为单测（rg已查，仅页面构造）。新增用例放现有src/Gdterm.Tests布局；Linux专项链接生产代码并复用隔离sshd流程，不修改系统sshd。真实SSH覆盖并发主机独立stdout/stderr/退出码、多行与中文、无输出、超时/取消、断线、关闭任务后client可用。替身覆盖慢/异常发送、不会发到新对象、记录线程安全、JSON特殊字符和无副作用回放。
本机csc-check、编码/i18n/sec/UI静态门；Windows CI原生构建/全部单测/交互/六组矩阵/实际截图。旧证据不能替代本轮验证。

## 执行证据

本机首轮 46 项专项断言全部通过（含真实隔离 SSH），命令 GDT_NUGET_SOURCE=/home/aerostone/.nuget/packages python3 tools/multi-session-smoke/run.py 退出 0。
bash tools/csc-check.sh 退出 0：386 源码/13 工程；编码、i18n、sec-gate、ui-ux-lint、feature-map 均退出 0。
本轮属于既有缺陷与行为增量，采用静态触发链确认 + 新行为回归；未声称全套 strict TDD/旧代码动态RED。Windows UI与CI待验证。

## 验收结果

待验证，不宣称完成。
