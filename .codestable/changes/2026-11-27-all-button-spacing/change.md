---
doc_type: change
kind: issue
status: in-progress
phase: analyzed
mode: standard
slug: 2026-11-27-all-button-spacing
severity: P2
risk:
  level: low
  reasons: [shared_empty_state]
contract:
  include:
  - .github/workflows/ci.yml
  - src/Gdterm.UI/**
  - src/Gdterm.Tools/**
  - src/Gdterm.Tests/**
  - tools/**
  - docs/**
  - .codestable/changes/2026-11-27-all-button-spacing/**
  exclude: []
  preexisting_changes: []
  baseline:
    git_head: 09745dba1f812200c559af2ffd962a7efb7d6bab
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

# 全项目按钮绘制留白和布局

## 目标与边界
覆盖所有按钮创建点和动态工厂，包括工具栏、页脚、图标、列表行、终端键条、自绘交互区和工具模块原生按钮。现有 Windows 控件树确认共享 SizeActionButton 紧凑分支把 Padding 当文字内边距，导致多个页面按钮绘制框小于文字宽度。

## 设计与契约
统一把文字留白计入 Size，AntdUI Padding/WaveSize 归零；按角色保持页脚、工具栏和终端的密度。外间距由 Margin 和容器负责；溢出使用换行、滚动或已有更多菜单。固定宽行按钮保留完整提示和可读省略。新增通用绘制框门禁覆盖所有可见按钮，包括禁用态，不能只按页面名白名单检验。

## 行为增量
MODIFIED：所有按钮的文字与可见绘制边界保留空间，工具栏在变宽后仍可达；不改变点击操作语义。

## 执行计划
盘点创建点与工厂，修公共算法和特例，扩 Windows 采集与全按钮绘制框断言，审查实际截图后更新受影响基线，通过本地编译和 Windows CI 后推送主分支。
