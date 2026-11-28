---
doc_type: change
kind: issue
slug: 2026-11-28-port-forward-fixes
status: in-progress
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

- [ ] manager 状态、异步起停、SOCKS5、原 owner 清理及真实 SSH 回归。
- [ ] 持久化 workspace、连接解析、回收站、应用退出接线。
- [ ] 规则表单、批量目标与错误/忙碌界面，Windows 行为和缩放矩阵。
- [ ] 本地编译与静态检查、推送、GitHub/AppVeyor、截图审阅及逐项验收。

## 验证计划

隔离 OpenSSH 验证本地/远程/SOCKS5 数据、占用失败回滚、断线重试、owner 隔离、慢启动取消及释放竞态。规则保存/重开/失败、表单提交取消和批量目标用单元与 Windows UI 冒烟；6 组矩阵和关键截图复核文字、留白、窄栏。所有验证记录实际命令及退出码。

## 执行证据

- 生产 manager 已实现三种转发、原 owner 清理、异步状态、断线探测及关闭撤销。
- 应用 workspace 持久化规则、按连接 ID 解析当前宿主；侧栏销毁仅退订。回收站恢复与表单保存均先成功写盘再发布内存状态。
- 表单添加连接/说明/校验/编辑；动态类型隐藏目标字段；列表目标集合统一且保留勾选和选择。
- `bash tools/csc-check.sh` 退出 0：384 个源码、13 工程清单；`tools/port-forward-smoke/run.py` 退出 0：45 项真实 SSH/持久化断言。
- 编码、UI lint、i18n、tokens、feature-map、sec-gate 和 impl checker 均退出 0。
- Windows 表单和面板行为冒烟、三类编辑器及有数据列表的 6 组矩阵已接入；推送后等待两套 CI 和像素证据。


## 验收结果

待验收。
