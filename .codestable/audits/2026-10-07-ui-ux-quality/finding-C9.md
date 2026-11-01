---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "ssot-C9"
nature: ssot-drift
severity: P1
confidence: high
suggested_action: cs-issue
status: reported
---

# Finding C9：DL §1 token 表与代码实测值不一致 + BorderStrong 零引用

## 速答

`docs/DESIGN-LANGUAGE.md` §1 的色彩表写的是 **v1 色板**（`#11161A` / `#1A2127` / `#35414A` /
`#34D399` …），而 `GdtermColorTable` 实际是 **v2 色板**（`#0D1116` / `#161D24` / `#29323D` /
`#3DDC97` …）。差异不是舍入，是两套不同的颜色。任何"照文档取色"的人都会得到错的视觉结果
（本次审计初稿的对比度计算就是照文档算的，全部偏了 —— 这就是这类漂移的真实危害）。

另外 v2 引入的 `BorderStrong`（注释写着"交互件边缘"）**全仓零引用**：token 建了，语义没接上。

## 关键证据

### 表 vs 代码

| Token | DL §1 文档 | `GdtermColorTable` 实际 | 差异 |
|---|---|---|---|
| Background | `#11161A` | `#0D1116` | 明显 |
| Surface | `#1A2127` | `#161D24` | 明显 |
| Surface2 | （文档未列） | `#1E2731` | **文档缺失** |
| Border | `#35414A` | `#29323D` | 明显 |
| BorderStrong | （文档未列） | `#41505F` | **文档缺失** |
| Accent | `#34D399` | `#3DDC97` | 不同色相 |
| Foreground | `#E8EEF2` | `#E8EEF3` | 微差 |
| Muted | `#9AA7B1` | `#8D9AA7` | 明显 |
| Hover | `#263039` | `#222C36` | 明显 |
| Pressed | `#323D47` | `#2A3540` | 明显 |
| Tint | （文档未列） | `Color.FromArgb(26, 0x3D,0xDC,0x97)` | **文档缺失** |

同类：`GdtermColorTable.cs:64-71` 的 XML 注释也过期 —— 注释写
`Danger #F06A64 / Warning #E4B86A / Success #59C77B / Info #75A9E6`，
实际字段值是 `#F47C74 / #E8C078 / #63D288 / #7FB3EE`。

### BorderStrong 零引用

```
$ grep -rn "BorderStrong" src/Gdterm.UI --include='*.cs'
src/Gdterm.UI/Diagnostics/GdtermColorTable.cs:17   // 注释：P2 边框分级
src/Gdterm.UI/Diagnostics/GdtermColorTable.cs:48   // 属性定义
```

两处都不是使用。v2 的设计意图（"Border 弱化 + BorderStrong 用于交互件边缘"）在代码里
只完成了一半：弱化的 Border 生效了（1.31:1），强化的 BorderStrong 从未被任何控件引用，
`DialogStyle.ApplyInput` 也没有设 `BorderColor` —— 输入框的边界实际由 AntdUI 默认决定。

### 为什么这是 P1

- **SSOT 的权威性被破坏**：文档说 A，代码是 B，两处都"官方"；新人无法判断以哪个为准；
- **自动化被误导**：本次审计的对比度计算、L0 规则里引用的色值，都可能照文档走偏；
- **设计意图静默丢失**：BorderStrong 的缺失直接导致 C5 的对比度问题（交互边界 1.31:1）。

## 影响

- 设计师按文档出原型 → 实现照代码 → 双方对不上（`design/prototype/*.html` 也需核对）；
- 对比度/主题类评审结论不可复现（不知道基准色是哪个）；
- 无法回答"OLED 主题的 Surface 是多少"这类基础问题。

## 修复方向

1. **让 token 表可生成**：`GdtermColorTable` 三套主题的色值导出为机器可读
   （如 `docs/tokens.json` 或直接由 L0 脚本解析 `ApplyTheme` 的 switch），
   文档章节改为引用生成结果 —— 手抄表格必然再漂。
2. **文档补齐**：Surface2 / BorderStrong / Tint / OnInfo / Overlay 写进 §1 表，
   并删除过期 XML 注释色值（或改为"见 §1"）。
3. **接上 BorderStrong**：`DialogStyle.ApplyInput` + 按钮/AntdUI 边界用它
   （先按 C5 把它提到 ≥3:1），否则这个 token 应删除（避免"看起来有分级其实没有"）。

## 建议动作

`cs-issue`（文档与实现不一致，且导致设计意图丢失）。第 1 步（token 导出 + 文档引用生成）
是根治，建议一并做 —— 它与 C8 的 L0 门同源：**能机械一致的就不该靠手抄**。
