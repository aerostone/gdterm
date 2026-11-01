---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "maintainability-C3"
nature: maintainability
severity: P1
confidence: high
suggested_action: cs-refactor
status: reported
---

# Finding C3：等宽字体 40 处逐点复制且跟随 UI 字号

## 速答

同一个表达式在 40 个位置被复制粘贴：

```csharp
Font = new Font("Consolas", Program.GlobalAppearance != null ? Program.GlobalAppearance.UIFontSize : 9f)
```

它同时踩了三个坑：**① 等宽字体被 UI 字号（8–24pt）直接放大** → `ListView` 行高、`AntdUI.Table`
行高、固定字段高度不跟随，`UIFontSize=20` 时必然裁切；**② fallback pt 各写各的**
（8.5 / 9 / 9.5 / 10 / 11 / 14 六种值）→ DL §2.2 的字号阶梯事实上已分裂；**③ 逐处 `new Font` 不
`Dispose`** → 每次面板重建泄漏 GDI 句柄（Font 是 GDI 句柄包装）。

## 关键证据

`python3 tools/ui-ux-lint.py --strict` 的 R10 命中 **40 处**，分布于 20 个文件：

| fallback pt | 典型位置 |
|---|---|
| 8.5 | `LogonScriptPanel.cs:172`、`SshKeyManagerForm.cs:94`、`BatchCommandPanel.cs:93` |
| 9 | `NotificationCenterPanel.cs:46`、`TransferCenterPanel.cs:39`、`PortForwardPanel.cs:74`、`MultiChannelPanel.cs:120` …（多数） |
| 9.5 | `FilePaneControl.cs:110/538/576`、`KeePassManagerForm.cs:127/772`、`PasswordHealthForm.cs:192/239` |
| 10 | `SecretScanPanel.cs:257`、`ConnectionQuickJumpForm.cs:54`、`DangerousCommandConfigForm.cs:531` |
| 11 | `SnippetSearchPanel.cs:61`、`ChangeMasterPasswordForm.cs:186`、`SetupWizardForm.cs:294/310` |
| 14 | `PasswordGeneratorForm.cs:135`（密码展示，字号另加 Bold） |

同时**没有一个**这些字体：
- 走 `FormFontPolicy`（DL §2.2 要求字号来自阶梯）；
- 在控件 Dispose 时被 Dispose（`new Font` 直接赋给 `Control.Font`，控件不会替你释放）；
- 有独立策略（终端字体是**另一套**配置，DL §2.1 明确"UI 字体 vs 终端字体"两类，这里混用）。

## 影响

- **放大字号即坏**：UI 字号 14/20pt 是"外观设置"里给用户的合法选项，用户一调，20 个面板的等宽文本
  超出 `ListView` 行高 → 文字被裁/重叠。这条路径无任何自动化测试（见 C7）。
- **一改要改 40 处**：将来支持"用户自选等宽字族"或"等宽字号独立缩放"，代价是 40 处编辑 + 40 处回归。
- **GDI 句柄泄漏**：面板在切主题/切字号时重建（见 C6 的修复方向就是重建），重建次数 × 40 累积。

## 修复方向

`FormFontPolicy` 增加单一出口：

```csharp
// 等宽字体：不跟 UI 字号，跟 UICodeFontSize（默认 = UiFontSize * 0.92，夹 8–16pt）
public static Font MonoFont(Control owner, float scale = 1f);
```

- 40 处替换为 `FormFontPolicy.MonoFont(this)`（密码/路径类允许 `scale: 1.15`）；
- `MonoFont` 内部按 `(家族, 字号, 样式)` 缓存 `Font` 实例，Dispose 由策略统一管理（避免逐处泄漏）；
- 在 `ApplyGlobalUIFont` 里随字号变更清缓存并重刷（把 C6 的"重建"变成"重刷"）。

## 建议动作

`cs-refactor`（行为保持的结构收口）：先加 `MonoFont` + 缓存，再批量替换 40 处；
替换后 `--write-baseline` 收紧 R10 基线，让新代码无法再写逐点复制。
