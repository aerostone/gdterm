---
doc_type: learning
track: pitfall
date: 2026-08-26
slug: winforms-permonitorv2-double-scaling
component: Gdterm.UI
tags: [WinForms, DPI, PerMonitorV2, 缩放]
status: active
---

# WinForms PerMonitorV2 双重缩放陷阱

## 问题

144dpi 高分屏下主窗体控件超大、布局错乱。

## 没用的做法

给窗体自身 `Size` / `MinimumSize` 再套 `DpiScale.S()` 手工缩放——系统已经缩过一次，叠加等于缩放两次。

## 解法

app.manifest 声明 PerMonitorV2 后，系统自动缩放**窗体自身**尺寸；`DpiScale.V/P/S` 只用于**子控件级**固定值（面板高度、padding、间距）。两者职责互斥，不可叠加。

## 原因

PerMonitorV2 模式下 WinForms 在窗体创建/跨屏移动时按当前 DPI 自动 scale 窗体边界；手工再乘系数导致尺寸翻倍。

## 预防

- 豁免清单见 `docs/UI-SCALING-CONVENTIONS.md`
- MainForm 自身尺寸保留字面量（`src/Gdterm.UI/Forms/MainForm.cs`），注释说明原因
- 禁止 `SetProcessDPIAware`（与 manifest 冲突）
