---
doc_type: change
kind: issue
slug: 2026-11-28-ssh-jump-fixes
status: in-progress
phase: analyzed
mode: standard
severity: P1
risk:
  level: high
  reasons: [credential_isolation, tunnel_lifecycle, configuration_roundtrip]
contract:
  include:
  - src/Gdterm.Core/Models/**
  - src/Gdterm.Core/L10n/**
  - src/Gdterm.Tunnel/**
  - src/Gdterm.Connections/ConnectionStoreJson.cs
  - src/Gdterm.UI/Forms/KeePassEntryPicker.cs
  - src/Gdterm.UI/Forms/ConnectionDialog.cs
  - src/Gdterm.UI/Forms/JumpHopEditForm.cs
  - src/Gdterm.UI/Controls/JumpChainEditorControl.cs
  - src/Gdterm.UI/Controls/TerminalControl.cs
  - src/Gdterm.UI/Controls/SftpDualPanePanel.cs
  - src/Gdterm.UI/Services/CredentialResolver.cs
  - src/Gdterm.UI/Services/ProtocolTabOpener.cs
  - src/Gdterm.UI/Gdterm.UI.csproj
  - src/Gdterm.Tests/**
  - tools/ui-baseline/**
  - tools/ui-matrix-ratchet.txt
  - tools/ssh-jump-smoke/**
  - .codestable/tools/ui-coverage-registry.txt
  - .github/workflows/ci.yml
  - docs/SSH-KEY-AUTHENTICATION.md
  - .codestable/changes/2026-11-28-ssh-jump-audit/**
  - .codestable/changes/2026-11-28-ssh-jump-fixes/**
  exclude: []
  preexisting_changes:
  - .codestable/changes/2026-11-28-ssh-jump-audit/change.md
  - .codestable/changes/2026-11-28-ssh-jump-audit/evidence/probe-output.txt
  baseline:
    git_head: 1fd9b97
    dirty_hashes:
      .codestable/changes/2026-11-28-ssh-jump-audit/change.md: 79dfda5dcd782c79cfb6e990ac65bbffb0e4a1800db72b4effb8d5b133ab3a1a
      .codestable/changes/2026-11-28-ssh-jump-audit/evidence/probe-output.txt: 025d774dc154a4702a15273a8c758eaf886d7e241b0c38171df4ab6824ff9d0c
  architecture_impact: unchanged
  architecture_reason: 仍由 CredentialResolver 解析 KeePass、TunnelManager 管理隧道；跳板编辑控件和子表单仅承担有序链配置与暂存凭据，避免扩大 ConnectionDialog 职责。
  architecture_refs: [ARCHITECTURE.md]
  requirement_impact: unchanged
  requirement_reason: 修复已有手动跳板链能力与独立凭据入口，不新增认证协议或后端。
  requirement_refs: []
  context_refs:
    design: [.codestable/changes/2026-11-28-ssh-jump-audit/change.md]
    impl: [src/Gdterm.Tunnel/Models/TunnelSession.cs, src/Gdterm.UI/Services/CredentialResolver.cs, src/Gdterm.UI/Forms/ConnectionDialog.cs]
    accept: [src/Gdterm.Tests/Core/CredentialPayloadTests.cs, src/Gdterm.Tests/Ui/DialogsSmoke.cs]
  artifacts: []
  evidence_ledger: false
---

# SSH 跳板完整链路修复

## 目标与边界

用户明确要求全部修复前一轮审计的 9 项问题，并继续验证并发线索。授权覆盖本轮修复、相应回归和既有 CI 验证；此前提交/推送及监控 CI 的授权继续有效。

## 根因

详见相邻 ssh-jump-audit 包 F01–F09：跳板密码回落到目标密码；代理未传入首跳；中间转发过早释放；表单只保存第一跳且丢引用；无每跳凭据入口；SFTP/RDP 判断缺 JumpChain；活跃标记不反映断线；目标无凭据时跳板解析提前返回；启用跳板却允许空配置。

## 设计与契约

- 每跳显式绑定 KeePass；引用缺失或不可读就以具体跳板提示失败。若要与目标共用凭据，用户可在每跳选择同一条目，不引入隐式密码回落。跳板用户名可覆盖条目用户名。
- 目标凭据与跳板凭据独立解析；密码、私钥与口令仍只在运行时和 KeePass 内存在。
- 独立跳板编辑控件管理有序列表，子表单负责单跳配置和凭据选择/新建；未提交的凭据随草稿保存，取消清理。连接编辑使用配置副本，完整保留原链与其他字段。
- 中间转发持续到隧道销毁；首跳使用连接代理；活跃状态检查真实客户端与转发状态，失效后重建。
- 统一 ConnectionConfig 的隧道需求判定供 SSH/SFTP/RDP 使用。补启动前配置校验和可读错误。
- 并发问题先用受控本地服务复现，确认后在同一生命周期范围内保证同连接只有一个建立任务，关闭/销毁时不重新发布会话。

## 行为增量

MODIFIED：上述错误路径按预期工作；旧的未绑定跳板配置需要显式选凭据，禁止静默共享目标密码。
ADDED：原有多跳模型的完整配置入口、对应交互和回归证据。

## 执行计划

1. 凭据边界和统一路由，补数据流回归。
2. 隧道生命周期与代理，复跑隔离 SSH 服务的单跳、多跳、失效/重建及并发验证。
3. 完整跳板表单、保存/取消/失败往返，补 Windows 冒烟和页面矩阵。
4. 本地编译与静态门、目标平台 CI、截图复核和最终验收记录。

## 执行证据

- 本地 `bash tools/csc-check.sh`：377 源文件，编译与 13 工程 Compile 清单检查退出 0。
- `python3 tools/ssh-jump-smoke/run.py`：真实两跳命令、HTTP CONNECT、代理失败不旁路、凭据隔离、断线重建、8 并发单实例、close/dispose 期间取消发布均通过，退出 0；见 evidence/network-after.txt。
- 受控旧实现复现：8 个 GetOrAdd 工厂并发启动 8 个 SSH 客户端，见 evidence/race-before.txt。
- 新增真实 KeePass 凭据解析/删除/锁库/清零测试；Windows 表单新增多跳排序、删除、取消、部分写失败回滚重试、改名/磁盘重开；SFTP/RDP 实际 UI 路由入口使用替身服务断言。
- 静态编码、UI 规范、i18n、安全闸门、设计 token、feature-map 均通过。Windows CI 与截图复核待本次推送后完成。

## 验收结果

待完成验证。

## 遗留事项

Windows 表单和网络故障行为以本轮实际验证结果为准，不沿用上轮绿色 CI 作为完成证据。
