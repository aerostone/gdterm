---
doc_type: change
kind: issue
status: accepted
phase: analyzed
mode: standard
slug: 2026-11-27-empty-action-spacing
severity: P2
risk:
  level: low
  reasons: [shared_empty_state]
contract:
  include:
  - src/Gdterm.UI/Controls/EmptyStateView.cs
  - src/Gdterm.UI/Controls/WelcomePanel.cs
  - src/Gdterm.UI/Forms/KeePassManagerForm.cs
  - src/Gdterm.Tests/Ui/*.cs
  - tools/ui-baseline/ui-smoke.json
  - docs/DESIGN-LANGUAGE.md
  - .codestable/changes/2026-11-27-empty-action-spacing/**
  exclude: []
  preexisting_changes: []
  baseline:
    git_head: 0575456a64422770f64b8c0d33fca630c7250fa5
    dirty_hashes: {}
  architecture_impact: unchanged
  architecture_reason: 沿用既有按钮尺寸策略和空状态组件。
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 恢复已有入口按钮的内容留白和可辨识性。
  requirement_refs: []
  context_refs:
    design: [docs/DESIGN-LANGUAGE.md]
    impl: [src/Gdterm.UI/Controls/EmptyStateView.cs, src/Gdterm.UI/Controls/WelcomePanel.cs]
    accept: [src/Gdterm.Tests/Ui/UiSmokeRunner.cs]
  artifacts: []
  evidence_ledger: false
---

# 欢迎页与空状态入口按钮留白

## 目标与边界
用户指出新建连接、添加第一条仍然贴字。0575456 的 Windows 截图确认：EmptyStateView 与 WelcomePanel 仍调用 SizeActionButton 的紧凑模式，Padding 缩进按钮底色，尺寸预算没有为可见内容留足空间。上次页脚修复未覆盖它们。

## 设计与契约
入口采用已验证的内容尺寸预算，留白计入 Size，Padding=0、WaveSize=0；流式容器的 Margin 负责外间距。空状态改文案后保持同一策略。密码库工具栏同名入口采用较小的文字留白并允许换行，搜索框独立占一行；其他工具栏和终端键条不变。

## 行为增量
MODIFIED：欢迎页与空状态主要操作按钮具有可见底色和四周内容留白，保持点击事件与文案更新行为。

## 执行计划
补充实际 ReadRectangle 和 GDI+ 文字度量断言；验证空状态与欢迎页在不同字号、布局缩放和主题下的显示。

## 执行证据
Windows 冒烟测得空库“添加第一条”绘制框相对文字的每侧水平余量为 25.1px、垂直余量为 10.6px；切换英文文案后分别为 25.0px 和 10.6px。密码库工具栏换行、搜索框独占一行，验证四个欢迎入口和八个工具栏入口可见。审查了 14 个受影响页面的截图后，按页更新基线。

## 验收结果
[Windows CI 45984835025](https://github.com/aerostone/gdterm/actions/runs/45984835025) 全部通过，包括 64 张冒烟截图和 41 页 × 6 组布局矩阵。复查了欢迎页在 150% 布局比例与 9/14pt 下的截图，以及大字号连接树和密码库管理器。窄窗内按钮换行，页面高度不足可滚动。DPI 轴使用注入系数，不代表实体显示器的跨屏行为。
