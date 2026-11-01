---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "theming-C6"
nature: theming
severity: P2
confidence: medium
suggested_action: cs-issue
status: reported
---

# Finding C6：运行期主题切换不重取 token（构造时快照）

## 速答

`GdtermColorTable.ApplyTheme()` 只改静态字段；`ToolsDialogsLauncher` 应用外观设置后调用
`ApplyTheme` + `MainForm.Refresh()`。但控件颜色是**构造时赋值**的：

```csharp
BackColor = GdtermColorTable.Surface   // Color 是 struct，赋值即拷贝
```

`Refresh()` 只重绘，**不会重新读取 token**。所以 Dark → Darker → OLED 切换后，
所有已创建控件保留旧色，直到重启（或控件被重建）。

## 关键证据

- `src/Gdterm.UI/Diagnostics/GdtermColorTable.cs:77 ApplyTheme(string)` —— 只写 `s_*` 静态字段；
- `src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:154-168` —— 注释明确写了
  "ToolStrip 不会自感静态颜色变化，所以调用 ApplyTheme 后手动 Invalidate 所有工具条"，
  即**已经知道**这个机制，但只处理了 ToolStrip 一类；
- `grep -rn "BackColor = GdtermColorTable" src/Gdterm.UI | wc -l` = **233 处**，
  其中 23 处在**常驻**面板/壳（`WelcomePanel` / `SidePanelHost` / `BottomBarPanel` /
  `TabContainerControl`）—— 这些不会被重建；
- AntdUI 侧：`Config.IsDark` 在启动时设一次，`ApplyTheme` 不重建 AntdUI 派生色板
  （AntdUI 内部从 `Config` 计算的背景/文字色同理不会刷新）。

三套主题的差异足够大，肉眼可见：

| token | Dark | Darker | OLED |
|---|---|---|---|
| Background | `#0D1116` | `#080B0E` | `#030404` |
| Surface | `#161D24` | `#11161C` | `#0A0F0F` |
| Border | `#29323D` | `#222B33` | `#1B2425` |

**置信度 medium 的原因**：未在 Windows 上实测切换效果（本机无桌面会话）；
推断链是"C# struct 赋值语义 + Refresh 不读 token"，逻辑确定，但受影响控件清单需实测确认。

## 影响

- 用户在设置里切主题 → 一部分界面变、一部分不变 → 看起来像"半坏"；
- 现有 3 个 dump（96dpi / 默认主题）都测不到这个问题（C7 的矩阵轴缺口）；
- 一旦修 C3/C6 引入"面板重建"，233 处快照会成为 233 个待验证点。

## 修复方向

两条路，按代价排：

**A. token 订阅（推荐，长期）**
`GdtermColorTable` 加 `static event Action ThemeChanged`；抽 `ThemedPanel : Panel`
基类（或 `UiTheme.Attach(control, role)`），把 `BackColor = token` 变成注册；
`ApplyTheme` 后统一 `Reapply()`。233 处逐步迁移，未迁移的仍走快照（增量收敛）。

**B. 重建外壳（短期止血）**
`ApplyTheme` 后销毁并重建侧栏/底栏/欢迎页（它们本来就是 `new` 出来的宿主内容），
对一次性对话框无需处理（每次新开都读当前 token）。
成本低，但会丢面板内状态（侧栏选中项、滚动位置），需保存/恢复。

## 建议动作

`cs-issue`（用户可见缺陷：主题切换不完整）。先做 B 止血并加入人工验收清单第 6 条
（见 `docs/UI-UX-VERIFICATION.md` §9），A 作为后续重构。
