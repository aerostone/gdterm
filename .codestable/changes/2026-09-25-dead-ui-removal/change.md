---
doc_type: change
kind: refactor
slug: 2026-09-25-dead-ui-removal
status: in-progress
mode: standard
summary: 删除审计 F2/F3 死代码——SftpBrowserPanel(640行零实例) + PreviewBoxShim(唯一调用方随之死) + DarkMenuRenderer(零引用),并修正过时注释
tags: [refactor, dead-code, ui]
risk:
  level: low
  reasons: []
model_route:
  strategy: direct
  reason: 纯删除 + csproj 摘除 + 注释修正,无逻辑判断点
contract:
  include:
    - src/Gdterm.UI/Controls/SftpBrowserPanel.cs
    - src/Gdterm.UI/Controls/SharedMenuTip.cs
    - src/Gdterm.UI/Controls/FilePaneControl.cs
    - src/Gdterm.UI/Gdterm.UI.csproj
    - .codestable/changes/2026-09-25-dead-ui-removal/**
  exclude: []
  preexisting_changes:
    - .codestable/.runtime/current-package
  baseline:
    git_head: 5c1faba9571dc99e0dcc33cf3bed4341b7631056
    dirty_hashes:
      .codestable/.runtime/current-package: 8a0a97eb4c19188d64f08fc11a9a232a122b8a2c49476cbff0a82c0507c501ab
  architecture_impact: unchanged
  architecture_reason: 删除不可达 UI 类型,不改模块边界与依赖方向
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 死代码无对应需求
  requirement_refs: []
  context_refs:
    design: []
    impl: []
    accept: []
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-dead-ui-removal/change.md
      depends_on: []
  evidence_ledger: false
---

# 死 UI 删除:SftpBrowserPanel + DarkMenuRenderer

## 目标与边界

清除审计 F2/F3 确认的死代码,行为保持不变(删的是不可达类型):

- F2:`Controls/SftpBrowserPanel.cs`(640 行,全仓零实例;SFTP 实际入口 SftpDualPanePanel)。
- F2 连带:`FilePaneControl.cs` 内 `PreviewBoxShim`(internal,唯一调用方 SftpBrowserPanel:567,面板死则 shim 死)。PreviewBox 本体保留(FilePaneControl:404/426 在用)。
- F3:`Controls/SharedMenuTip.cs` 内 `DarkMenuRenderer` 类(零引用);文件其余(ButtonTipExtension,10 处使用)保留;类上"BottomBarPanel 仍在用"的过时注释一并修正。
- 删除前确认已做(审计+本轮复验):全仓 new/引用/Tests/冒烟四向零引用;csproj 130/162/164 行摘除对应 Compile 条目。

不做:FilePaneModel.cs 不删(文件内 FileEntry/IFilePaneProvider/双 Provider 均在用,仅文件名同名类不存在——审计已澄清)。

## 行为增量

- REMOVED:SftpBrowserPanel 类型及其编译单元;PreviewBoxShim;DarkMenuRenderer。
- ADDED/MODIFIED:无。注释修正(PreviewBoxShim 引用注释、DarkMenuRenderer 注释)不属行为。

## 设计与契约

### 术语约定

- 死代码判据:全仓(含 Gdterm.Tests)无 `new X`、无类型引用、无反射字符串引用;csproj 摘除后编译面收敛。

### 决策与约束

- D1:删文件 + csproj 摘行,不改 SftpDualPanePanel/FilePaneControl 任何逻辑。
- D2:PreviewBoxShim 删除方式=整段 internal class 移除,两处 /// 注释同步修正(504 行提到 SftpBrowserPanel 的措辞改为"预览框本体")。
- D3:DarkMenuRenderer 用整段删除;SharedMenuTip.cs 文件名保留(装 ToolTip 扩展,名字虽不精确但改名会动 csproj+全部 using,超出本包行为边界——记为后续可选清理)。

### 名词与编排

Step1 RED(驱动断言三者存在)→ Step2 csproj 摘行+删文件 → Step3 shim/renderer 摘除+注释修正 → Step4 GREEN(全仓零引用+balance)+合规。

## 验收契约

- S1 全仓(排除本包文档)grep SftpBrowserPanel/PreviewBoxShim/DarkMenuRenderer 零命中。
- S2 csproj 无三者的 Compile 行。
- S3 SharedMenuTip.cs 仍含 ButtonTipExtension(ToolTipText2),BottomBarPanel 等使用方不回归。
- S4 FilePaneControl PreviewBox 本体保留且 404/426 调用点不变。
- S5 balance/编码卫生:被改文件括号平衡、无 BOM 新增。

## 执行计划

1. Step1 RED 驱动。
2. Step2+3 删除与摘除。
3. Step4 GREEN+合规 impl。

## 执行证据 (impl 阶段追加)

**状态口径**:kind refactor 扁平状态机 draft→in-progress(用户 standing「请继续」授权,与包 B/C/E 同例)。

- Step1 RED:red 模式 11 checks 全 FAIL(死码在位判据+GREEN 判据未达标),exit 1。
- Step2 删除:os.remove SftpBrowserPanel.cs(640 行);csproj 摘 Compile 行 1 条;FilePaneControl 摘 PreviewBoxShim 段(唯一调用方已死,审计 F2 连带);SharedMenuTip 截断 DarkMenuRenderer(含其 /// 注释,该注释"BottomBarPanel 仍在用"与事实不符——BottomBarPanel 菜单仅设 BackColor 未挂 renderer)。
- Step3 GREEN:green 模式 11 checks ALL OK exit 0;cs-balance 两文件 ok;全仓 grep 三名字零命中。
- 驱动自身两坑(已修):①red/green 断言混排导致 green 模式先报 3 个 RED 判据 FAIL——重构为 phase 分支;②phase_green 定义在使用之后(NameError)——提前到使用点之前。均为驱动编排问题,非被检代码。
- 反向:ToolTipText2 扩展与 BottomBarPanel 10 处使用保留;PreviewBox 本体与 FilePaneControl:404/426 两调用点保留;FilePaneModel 未动(审计已澄清非死文件)。

## 验收结果 (accept 阶段追加)

