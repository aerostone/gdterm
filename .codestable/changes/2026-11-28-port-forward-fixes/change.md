---
doc_type: change
kind: issue
slug: 2026-11-28-port-forward-fixes
status: accepted
phase: analyzed
mode: standard
severity: P1
risk:
  level: high
  reasons: [ssh_ownership, async_lifecycle, persistence]
contract:
  include:
  - src/Gdterm.Core/Models/PortForwardRule.cs
  - src/Gdterm.Core/L10n/**
  - src/Gdterm.Connections/PortForwardRuleStore.cs
  - src/Gdterm.Connections/Gdterm.Connections.csproj
  - src/Gdterm.Tunnel/PortForward*.cs
  - src/Gdterm.Tunnel/Gdterm.Tunnel.csproj
  - src/Gdterm.UI/Controls/PortForwardPanel.cs
  - src/Gdterm.UI/Controls/TabContainerControl.cs
  - src/Gdterm.UI/Forms/PortForwardRuleForm.cs
  - src/Gdterm.UI/Forms/MainForm.cs
  - src/Gdterm.UI/Services/PortForwardWorkspace.cs
  - src/Gdterm.UI/Services/ActiveSessionBridge.cs
  - src/Gdterm.UI/Services/TabActiveSessionQuery.cs
  - src/Gdterm.UI/Services/SidePanelFactory.cs
  - src/Gdterm.UI/Services/AppShutdownCoordinator.cs
  - src/Gdterm.UI/Gdterm.UI.csproj
  - src/Gdterm.Tests/**
  - tools/port-forward-smoke/**
  - tools/csc-check.sh
  - tools/ui-baseline/**
  - tools/ui-matrix-ratchet.txt
  - .github/workflows/ci.yml
  - docs/FEATURE-MAP.md
  - .codestable/tools/ui-coverage-registry.txt
  - .codestable/architecture/ARCHITECTURE.md
  - .codestable/changes/2026-11-28-port-forward-fixes/**
  architecture_impact: update
  architecture_reason: 规则与运行任务归应用级 PortForwardWorkspace，视图只订阅；协议对象归 PortForwardManager 并记录原 SSH owner。
  architecture_refs: [ARCHITECTURE.md]
  requirement_impact: unchanged
  requirement_reason: 补全已有本地、远程、SOCKS5 转发及规则管理承诺。
  requirement_refs: []
  context_refs:
    design: [.codestable/changes/2026-11-28-port-forward-audit/change.md]
    impl: [src/Gdterm.Tunnel/PortForwardManager.cs, src/Gdterm.UI/Controls/PortForwardPanel.cs]
    accept: [src/Gdterm.Tests/Ui/UiPageCatalog.cs]
  artifacts: []
  evidence_ledger: false
  baseline:
    git_head: f8137f71df8e9c3f7e083a049cf1fe6bb76ac36b
    dirty_hashes: {}
---

# 端口转发九项问题修复

## 目标与边界

用户要求全部修复 PF01–PF09，沿用提交、推送及监控两套 CI 的授权。根因、实连反例见相邻 port-forward-audit 包。

## 设计与契约

- PF01：接入 SSH.NET ForwardedPortDynamic；SOCKS5 仅编辑本地监听，不伪造固定目标。
- PF02：应用级 workspace 持有规则仓库和 manager；规则原子写入便携 data/config/port-forwards.json。临时面板仅订阅状态，隐藏/释放不影响转发。回收站恢复调用同一 workspace，持久化失败不得消耗条目。重启不自动启动监听。
- PF03：规则保存 ConnectionId，表单和列表显示 SSH 归属。启动时按 ID 查询当前已连接的真实终端（含分屏）；绝不回退其他活动连接。相同连接多个会话优先当前同连接，否则可用会话。重连后手动启动使用新客户端，原运行实例始终绑定原 owner。
- PF04/PF07：每条记录拥有原客户端、转发对象和状态；断线/底层停止归位，可重试。失败、停止、退出均尝试 Stop/Remove/Dispose，释放共享 SSH 不属于单条转发的权限。
- PF05：网络启停在后台执行，起停状态立即可见；停止启动中的规则标记取消，完成握手后清理，不能晚到复活。面板关闭不取消应用任务；应用退出撤销全部任务。禁止为取消而 Disconnect 共享客户端。
- PF06：共享校验类型、地址、1–65535 端口；表单保留错误输入，按类型标注监听/目标。显示逐条错误原因。
- PF08：勾选当前可见行优先，否则选中行，无目标禁用/无操作；启停删除同一集合。双击/回车只保留一个入口（编辑），批量逐条反馈，保持选择。
- PF09：专用创建/编辑表单，保留 ID、所属连接、说明；运行/启停中的规则禁止编辑，必须先停止。

## 行为增量

MODIFIED：PF01–PF09 全部修正，新增明确编辑入口及原有 SOCKS5 选项的实际执行。

## 范围与取舍

保留 Core 模型、Connections 持久化、Tunnel 协议、UI 编排分层。无需新增独立 SSH 连接或自动转发重连机制。仅移动必要状态所有权，不改其他 ListShell 页面的选择语义。SSHv2 远程监听是否开放非回环地址由服务器 GatewayPorts 决定，表单解释权限限制。

## 执行计划

- [x] manager 状态、异步起停、SOCKS5、原 owner 清理及真实 SSH 回归。
- [x] 持久化 workspace、连接解析、回收站、应用退出接线。
- [x] 规则表单、批量目标与错误/忙碌界面，Windows 行为和缩放矩阵。
- [x] 本地编译与静态检查、推送、GitHub/AppVeyor、截图审阅及逐项验收。

## 验证计划

隔离 OpenSSH 验证本地/远程/SOCKS5 数据、占用失败回滚、断线重试、owner 隔离、慢启动取消及释放竞态。规则保存/重开/失败、表单提交取消和批量目标用单元与 Windows UI 冒烟；6 组矩阵和关键截图复核文字、留白、窄栏。所有验证记录实际命令及退出码。

## 执行证据

- 生产 manager 已实现三种转发、原 owner 清理、异步状态、断线探测及关闭撤销。
- 应用 workspace 持久化规则、按连接 ID 解析当前宿主；侧栏销毁仅退订。回收站恢复与表单保存均先成功写盘再发布内存状态。
- 表单添加连接/说明/校验/编辑；动态类型隐藏目标字段；列表目标集合统一且保留勾选和选择。
- `bash tools/csc-check.sh` 退出 0：384 个源码、13 工程清单；`tools/port-forward-smoke/run.py` 退出 0：84 项真实 SSH/持久化及生产连接解析断言。命令：`GDT_NUGET_SOURCE=/home/aerostone/.nuget/packages python3 tools/port-forward-smoke/run.py`。
- 编码、UI lint、i18n、tokens、feature-map、sec-gate 和 impl checker 均退出 0。
- GitHub [46152072567](https://github.com/aerostone/gdterm/actions/runs/46152072567) success，AppVeyor [0.1.570 / 55184134](https://ci.appveyor.com/project/aerostone/gdterm/build/0.1.570) success，均为源码 fdb79dc。
- Windows：1089 单元断言、10 组 UI 冒烟、69 页×6 组=414 次矩阵采集、布局门和性能/静态门全通过。CI 像素基线 114 张通过；最终人工审图补入 offline/statuses 两页后，本地对同批 artifact 检查 116 张通过。
- 人工检查最终普通/窄栏、错误/状态、14pt/150% 三种规则表单和 OLED 图像；规则字段、说明、错误和按钮留白可见。见 evidence/README.md、verification.json 和选定 PNG/JSON。


## 验收结果

PF01–PF09 全部修复并通过验收。源码 fdb79dc 已通过两套 CI；最后的记录提交仅包含本验收证据及两条已审图像素基线，不改变业务或测试源码。

| 审计项 | 修复与证据 |
|---|---|
| PF01 | 真实 SOCKS5 无认证协商、CONNECT、SSH banner 数据，动态表单隐藏固定目标。 |
| PF02 | 应用 workspace 持有规则和运行任务；Windows 销毁/重建面板保持规则，跨面板回收站恢复和重载写盘通过。 |
| PF03 | 链接生产 TabActiveSessionQuery，以真实 SSH 客户端验证迟连接、切标签、替换客户端、分屏及无匹配不回退。WinForms 容器在 Linux 使用替身，范围仅解析逻辑。 |
| PF04 | 断线立即不报告运行，500ms 后台探测归位；原客户端重连、替换客户端均能重试。 |
| PF05 | 隔离 sshd 会话暂停时启动调用立即返回，Starting/Stopping 可观察；停止与退出竞态不复活、不关闭共享 SSH。Windows 状态夹具检查禁用编辑/重复启动及可停止。 |
| PF06 | 类型、主机、1–65535 范围；真实表单非法输入不关闭且显示原因；占用端口返回错误。 |
| PF07 | 失败回滚、原 owner、Stop/Dispose、10 轮×12 条批量清理；CI 曾暴露 SSH.NET 注册列表并发删除遗留，现以 client 弱引用锁保护注册表变更。 |
| PF08 | 当前可见勾选优先，否则当前选择，无目标不回退全部；过滤隐藏勾选排除；刷新保留选择；双击仅一次编辑。 |
| PF09 | 实际保存/取消及编辑保留 ID/说明/连接；磁盘拒写时不发布修改，删除/恢复失败保持原规则/回收站条目。 |

## CI 修复记录

- 471e8a2：UI 面板构造挂起。d4e0c06 运行时日志确认 SizeActionButton 字体量尺创建 handle，提前触发 OnLoad（list=False/shell=False）。只在构造完成后刷新列表，删除临时追踪输出。
- d4e0c06：新版 OpenSSH 对 banner-only 探针触发 PerSourcePenalties；隔离配置探测支持后禁用，仅测试进程有效，不改系统 sshd。
- af6c116：Windows 冒烟及 68 页×6 组矩阵通过；数据探针排除限流后，CI 确认同一 SSH client 注册表并发移除遗留，fdb79dc 定点同步 Add/Remove；本地 84 项断言退出 0。
- 新增面板状态 dump 使用 port-forward 前缀复用既有面板关闭路径规则，不改判据。相关像素基线仅在人工审查 af6c116 的原图后更新，保留其余页面基线。

## 收尾

`check-compliance.py --phase accept --converge` 退出 0，无失败/警告。规则所有权与弱引用注册表同步事实已回写当前架构；本次时序/CI 经验保留在本包，无额外独立知识条目。

## 遗留事项

远程非回环监听仍受 sshd GatewayPorts 控制，表单给出说明。关闭面板继续运行，退出应用停止，重启规则保留但不自动启动。真实鼠标/键盘系统输入未纳入自动化；Windows 测试调用真实控件的保存按钮与激活事件并采集真实绘制截图。
