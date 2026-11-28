---
doc_type: change
kind: issue
slug: 2026-11-28-ssh-jump-fixes
status: accepted
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
- 静态编码、UI 规范、i18n、安全闸门、设计 token、feature-map 均通过。首轮 Windows 单测 1080/0、UI 冒烟、控件树通过；截图复核发现表格 AntList 不渲染，改成已支持的 List 并补 RowCount 断言。新增高级页矩阵暴露原固定宽度、单行提示和 RDP 长复选框越界，改为视口宽度和换行。
- 保留目标凭据草稿到跳板批量保存成功；跳板失败时回滚本次新建目标凭据并恢复输入；删除回滚失败时保留已落库引用，重试不重复创建。
- CI SSH 工具以 runner 本人身份运行临时 sshd，与本地一致；避免提权服务对 runner 系统账户施加额外认证限制。代码提交 24c8ffa 的 GitHub run 46113892779：ssh-jump、Windows build、1080 单测、UI smoke、控件树及 62×6 矩阵均通过；AppVeyor 55182938 / 0.1.555 成功并生成便携包。
- 截图人工复核见 evidence/visual-review.json：核验普通/150%＋14pt 下跳板行、凭据类型、操作按钮、独立编辑表单、空库选择引导和三协议连接页。基本字段与高级设置共享滚动区域，底部按钮固定；矩阵断言滚动后跳板按钮完整位于视口，防止“高级区仅剩一条缝”假绿。
- 仅更新三张受影响连接页及五张新增跳板页的像素基线，阈值保持原值；最终代码/基线提交 1b2f620 在 GitHub run 46115098539 全绿（Windows 构建、1080/0 单测、UI smoke、树、像素、矩阵、性能、静态门；Linux SSH 实连全部通过），AppVeyor 55182982 / 0.1.556 成功。见 evidence/ci-result.json。

## 验收结果

逐项实现与验证对应：

| 问题 | 修复与证据 |
|---|---|
| F01 跳板凭据失效回落目标密码 | 显式 hop 引用独立解析，缺失与空密码分开；真实 KeePass 删除条目与实连缺凭据测试 |
| F02 首跳忽略代理 | 只向第一跳传 Proxy；本机 HTTP CONNECT 实际转发及不可用代理阻断 |
| F03 中间转发提前释放 | Session 持有转发到整链释放；真实两跳目标命令及 SFTP 列目录 |
| F04 编辑丢链/引用 | 配置副本与完整有序链 Load/Get；改名保存、磁盘重开、取消原对象不变 |
| F05 无独立密码/私钥入口 | 每跳选择或新建 KeePass；嵌套表单密码、加密私钥、草稿取消与保存失败重试 |
| F06 SFTP/RDP 绕过 JumpChain | 统一 RequiresTunnel；两个实际 UI 入口用服务替身核验端点 |
| F07 失效连接复用 | IsActive 检查客户端和转发，断线重建；实连断开后成功重试 |
| F08 无目标凭据跳板也不解析 | 目标和 hop 独立解析；真实 KeePass 无目标条目场景 |
| F09 空链静默直连 | 表单阻断，导入空链仍进入 Manager 校验，禁止直连旁路 |
| 并发重复建立 | pending 注册后仅启动一项，close/dispose 取消发布；8 并发与受控握手竞争 |
| 显示/可达性 | List 绑定并断言实际 RowCount；工具栏前置、凭据类型前置、整表单滚动；62 页 × 6 矩阵及截图 |

本轮审计九项及并发问题全部修复。最终两套 CI 成功，截图人工复核与像素基线通过，验收通过。

验收记录提交只更新本包文档/证据，使用 `[skip ci]` 防止重新构建完全相同的应用与测试代码；已验证的代码与基线提交为 1b2f620。

## 遗留事项

- DPI 矩阵通过 DpiScale 注入 100%/150%，字体使用真实 GDI 9/14pt；系统级 PerMonitorV2 与用户真实服务器环境未代测。
- 本地与 CI 临时 SSH 服务和密钥均在结束后清理；原审计临时服务及四个密钥文件也已清理并确认监听端口关闭。
- KeePass 不提供跨多条目的数据库事务；创建中途失败会尽力删除本批新条目，删除失败则保留已落库引用以避免重试重复创建。
