---
doc_type: change
kind: issue
status: accepted
phase: analyzed
mode: standard
slug: 2026-11-28-ssh-key-credentials
severity: P1
risk:
  level: medium
  reasons: [credential_read_write, authentication_ui]
contract:
  include:
  - src/Gdterm.Core/L10n/**
  - src/Gdterm.KeePass/**
  - src/Gdterm.Tunnel/**
  - src/Gdterm.UI/Forms/**
  - src/Gdterm.Tests/**
  - tools/ui-baseline/**
  - tools/ui-matrix-ratchet.txt
  - docs/SSH-KEY-AUTHENTICATION.md
  - .codestable/changes/2026-11-28-ssh-key-credentials/**
  exclude: []
  preexisting_changes: []
  baseline:
    git_head: 6b8dbfa
    dirty_hashes: {}
  architecture_impact: unchanged
  architecture_reason: 沿用 KeePass、CredentialResolver 和 SSH 连接信息工厂，不增加认证后端。
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 补齐既有 SSH 私钥认证的可达入口和凭据读写。
  requirement_refs: []
  context_refs:
    design: [docs/DESIGN-LANGUAGE.md]
    impl: [src/Gdterm.UI/Forms/ConnectionDialog.cs, src/Gdterm.KeePass/KeePassService.cs]
    accept: [src/Gdterm.Tests/Security/SshKeyAuthenticationTests.cs, src/Gdterm.Tests/Ui/DialogsSmoke.cs]
  artifacts: []
  evidence_ledger: false
---

# SSH 密钥凭据入口与读写

## 目标与边界

恢复用户从连接与凭据编辑器配置 SSH 私钥登录的完整流程；不修改 SSH 协议实现、不配置远端公钥授权。密钥不得明文进入连接配置或日志。

## 根因

SSH.NET 工厂已实现私钥认证，但连接窗口和凭据编辑器只有密码输入，用户必须绕到独立工具导入。`KeePassService.GetFullEntry` 未读取私钥附件，因此自动匹配返回的凭据不带私钥。原编辑器只改普通字段，已有附件仍留在库中，但用户无法查看、更换密钥或修改口令。`UpdateEntry` 没有明确删除私钥的操作语义。

## 设计与契约

连接页增加直接入口，待提交密钥仅在保存连接时入库，取消不落库。凭据编辑器支持密码和私钥两种模式，私钥模式显示文件选择及口令，保存前用 SSH.NET 校验。列表与连接页显示认证类型。完整条目读取包含附件；更新时 null 保留原密钥，空数组明确删除，非空数组替换。密钥与口令只保存在 KDBX，连接 JSON 保留引用。

## 行为增量

ADDED：连接页直接配置 SSH 私钥入口，凭据编辑器认证方式、密钥选择和口令输入。

MODIFIED：自动匹配带出密钥附件；列表和连接页标注认证类型；更新支持明确移除密钥。导入失败留在编辑界面，连接取消不保存待提交的私钥。

## 执行计划

确认入口和凭据传递缺口，补齐编辑器与连接流程，修复附件完整读取及删除语义，加入格式校验和真实 KDBX/Windows UI 回归，复核截图并运行两套 CI。

## 执行证据

本地全量编译、13 个工程清单和静态门通过。[Windows 46103101095](https://github.com/aerostone/gdterm/actions/runs/46103101095) 的 1053 项单测、UI smoke、控件树、57 页 × 6 格矩阵通过。测试使用真实 KDBX 验证保存与重新打开，检查自动匹配、显式引用、文件夹继承及有/无代理的私钥认证参数；界面验证取消不写库、失败保留草稿重试、原文件删除后编辑及重开连接。

该 run 的像素门只报凭据编辑页新增认证方式行带来的色块占比变化。人工复核普通与大字号截图后更新 keepass-entry、connection-ssh，并新增 credential-ssh-key 基线，阈值未放宽。本地用下载的原始截图重跑像素门通过（100 张基线）。首次 UI 测试未把默认 RDP 切换为 SSH，已修正场景设置。相同功能代码在 [AppVeyor 0.1.551](https://ci.appveyor.com/project/aerostone/gdterm/builds/55182653) 完整通过。

## 验收结果

私钥从新建、取消、保存/失败重试到重新读取及认证构建的链路通过测试，界面留白和可达性通过 Windows 冒烟及六格矩阵。使用入口见 docs/SSH-KEY-AUTHENTICATION.md。

## 遗留事项

DPI 矩阵使用注入比例，不代表已在实体多屏切换上实测。未使用用户的真实主机/密钥进行联网登录，具体支持格式以 SSH.NET 的解析结果为准。
