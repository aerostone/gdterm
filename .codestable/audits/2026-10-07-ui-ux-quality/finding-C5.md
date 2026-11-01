---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "a11y-C5"
nature: a11y
severity: P1
confidence: high
suggested_action: cs-feat
status: reported
---

# Finding C5：无障碍名称全仓为 0 + 自绘命中区过小 + 交互描边对比度不达标

## 速答

三件事叠加，使这个 UI 对**键盘/低视力/屏幕阅读器**用户基本不可用：

1. **`AccessibleName` / `AccessibleDescription` 全仓 0 处**。
   AntdUI 自绘控件 + 纯图标按钮（`Text = "↑" / "×" / "+" / "−"`）对 UI Automation 是空节点。
2. **标签页关闭钮命中区 16×18 设计 px**，且只在选中/悬停标签上出现 ——
   低于 WCAG 2.5.8 的 24×24，也低于项目自己的 `HIT_MIN=32`。
3. **交互描边对比度 1.31:1（Border）/ 2.05:1（BorderStrong）**，均低于 WCAG 1.4.11 的 3:1；
   而 v2 专门引入的 `BorderStrong`("交互件边缘") **全仓零引用**（见 C9）。

## 关键证据

### ① 无障碍名称为零

```
$ grep -rn "AccessibleName\|AccessibleDescription" src --include='*.cs' | wc -l
0
```

典型无名称节点：
- `Controls/LogonScriptPanel.cs:182-185` —— `Text = "+" / "−" / "↑" / "↓"` 四个纯符号按钮；
- `Controls/TabContainerControl` —— 标签关闭钮为 owner-draw 绘制，连 Control 节点都不是；
- `Controls/BottomBarPanel.cs:488/520/761` —— 图标按钮（分组/更多/折叠）。

### ② 自绘命中区（R1 看不见的洞）

`src/Gdterm.UI/Services/TabChromePainter.cs:21-22`

```csharp
public const int CloseButtonWidth = 16;
public const int CloseButtonHeight = 18;
```

96 DPI 下 16×18 px。用户还能用中键/右键菜单关标签，但可见的 × 是主路径。
R1（`HIT_MIN=32`）只遍历 Control 节点，自绘区域永不参与判定（见 C7-③）。

### ③ 对比度（按**代码实际色值**计算，非文档）

`GdtermColorTable` 当前 Dark 主题实测值（与 DL §1 表格不一致，见 C9）：

| 前景/背景 | 比值 | 要求 | 判定 |
|---|---|---|---|
| `Foreground #E8EEF3` / `Background #0D1116` | 16.19 | ≥4.5 | ✅ AAA |
| `Foreground` / `Surface #161D24` | 14.53 | ≥4.5 | ✅ AAA |
| `Foreground` / `Hover #222C36` | 12.12 | ≥4.5 | ✅ AAA |
| `Muted #8D9AA7` / `Background` | 6.59 | ≥4.5 | ✅ AAA |
| `Muted` / `Surface` | 5.92 | ≥4.5 | ✅ |
| `Muted` / `Surface2 #1E2731` | 5.26 | ≥4.5 | ✅ |
| `Accent #3DDC97` / `Background` | 10.72 | ≥4.5 | ✅ |
| `OnAccent #06251A` / `Accent` | 9.23 | ≥4.5 | ✅ |
| `Danger #F47C74` / `Surface` | 6.46 | ≥4.5 | ✅ |
| `Warning #E8C078` / `Surface` | 9.91 | ≥4.5 | ✅ |
| `Success #63D288` / `Surface` | 8.99 | ≥4.5 | ✅ |
| `Info #7FB3EE` / `Surface` | 7.76 | ≥4.5 | ✅ |
| **`Border #29323D` / `Surface`** | **1.31** | ≥3（非文本） | ❌ |
| **`Border` / `Background`** | **1.46** | ≥3 | ❌ |
| **`BorderStrong #41505F` / `Surface`** | **2.05** | ≥3 | ❌ |
| **`BorderStrong` / `Background`** | **2.29** | ≥3 | ❌ |
| `Surface` / `Background`（填充差） | 1.11 | — | 无区分力 |
| `Surface2` / `Surface`（层差） | 1.13 | — | 无区分力 |

**文字与状态色全部达标**（这块做得好）。问题只在边界：所谓"分级边框"两档都不到 3:1，
而输入区与背景的**填充差只有 1.11:1** —— 也就是说输入框边界**只能靠那条 1.31 的线**识别。

## 影响

- 屏幕阅读器（NVDA / 讲述人）在侧栏里读不出按钮用途，等于无法操作；
- 高 DPI 平板上点不准标签 ×（16px 目标）；
- 低视力用户在低亮度/OLED 上看不出输入框边界（`AppearanceSettingsForm` 这类表单密集页最明显）；
- 键盘用户缺 Tab 序（见 C7 的 R2 空转）。

## 修复方向

1. **语义拆分 + 名称补齐**（低成本高收益）
   - 图标/符号按钮统一走 `MakeIconButton(icon, accessibleName, tooltip)` 工厂，
     同时设 `AccessibleName` + `ToolTipText2` + `Text`；
   - `AntdUI.Input/Select/Table` 在 `DialogStyle.ApplyInput` / `FormFontPolicy.Apply` 里
     按相邻 `label.Text` 自动补 `AccessibleName`（一次覆盖多数表单）。
2. **命中区**：绘制保持 16×18（视觉不变），**命中矩形扩到 24×24 或标签右侧 32px 区块**
   —— "视觉小、目标大"。同时把自绘命中区纳入 L1 dump（`hitRegions`）。
3. **对比度**：把 `BorderStrong` 提到 **≥3:1 vs Surface**，实测候选：
   `#5A6A78`(3.05) / `#66757F`(3.57) / `#6A7986`(3.80)。
   推荐 `#66757F`（3.57 / 4.00），并让 `DialogStyle.ApplyInput` 与按钮描边用它；
   `Border` 保留纯分隔线语义（1.31 对分隔线可接受）。
   注意：改 `BorderStrong` 会同时影响 Darker/OLED 两套（各主题需各自核算）。

## 建议动作

`cs-feat`（无障碍 + 命中区基础设施）。对比度一步可单独做（1 个 token + 2 处引用），
建议先落地；`AccessibleName` 工厂随后。
