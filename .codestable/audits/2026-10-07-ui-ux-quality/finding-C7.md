---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "monitoring-C7"
nature: monitoring
severity: P1
confidence: high
suggested_action: cs-feat
status: reported
---

# Finding C7：UI 验证体系四处硬缺口

## 速答

现有验证链**做得不错但只覆盖了它擅长的那一段**：`ui-tree-check.py` 的 R1–R5 是真正的
数值断言（几何/停靠/触击/键盘），不是像素启发式。缺口在它**看不见**的地方：

| # | 缺口 | 后果 |
|---|---|---|
| ① | **30 个 `none` + 1 个 `n/a` = 31/52 不在任何 dump 里** | 用户 90% 时间面对的面板零自动验证 |
| ② | **无文字适配断言** | 历史最高频 bug（文字裁切/重叠）无门 |
| ③ | **R1 只看 Control 节点** | 自绘命中区（标签 ×、树图钉、滚动条）永不被判 |
| ④ | **R2 事实空转** | 显式 TabIndex 仅 1 处，其余全 0 → 规则直接跳过 |
| ⑤ | **无 DPI×字号×主题矩阵** | 历史事故全在这两根轴上，一次都没跑过 |
| ⑥ | **视觉门未进 CI** | `ui-check.py` 依赖 PIL，违背零依赖原则，只能本地跑 |

## 关键证据

### ① 覆盖率

`.codestable/tools/ui-coverage-registry.txt`（本次新建，52 条）统计：

```
dump=16  manual=5  none=30  n/a=1    (合计 52)
```

- 窗体级 **15/16**（仅 `SftpPermissionForm` 未进 dump）—— 这块做得好；
- 面板级 **1/36**（只有 `TransferProgressDialog`）—— 而侧栏/标签页正是主界面；
- `MainFormSmoke` 只 dump 锁定态主窗，侧栏面板从未 `Show`。

### ② 文字适配

`UiTreeDumper` 输出字段只有 `abs / type / name / text / fontSize / tabIndex / dock /
enabled / visible …`，**没有文字实际度量宽高**。因此：

- `clientRect` 只有控件矩形，无法判断"这段中文在这个宽度下会不会换行/被裁"；
- 需要 `TextRenderer.MeasureText(text, font, proposedSize)` 的 `textWidth/textHeight`
  （`TabChromePainter.ComputeTabWidth` 已经在用同一 API，说明可行）。

历史证据：`TabChromePainter` 注释里写着"固定 120px 会在长主机名下硬截断"、
"关闭钮常驻占位挤占文字"，这些正是"量了才知道"的 bug —— 而现在只有绘图代码内部量，
门禁侧不量。

### ③ 自绘命中区

`ui-tree-check.py` 的 R1 从 dump 的 `controls/children` 递归取节点。自绘区域不是节点：

- 标签关闭钮 16×18（C5）；
- 树节点的固定/收藏图标（`ConnectionTreeControl` 自绘）；
- 终端滚动条与搜索条内嵌钮。

结果：R1 报"全部达标"，但用户实际点不准。

### ④ R2 空转

```python
# ui-tree-check.py R2：tabIndex 非零值不得重复（0=WinForms 默认无序，跳过）
```

`grep -rn "TabIndex =" src/Gdterm.UI --include='*.cs' | wc -l` = **1**。
所以 R2 恒过。一个永不失败的规则比没有规则更坏 —— 它让"焦点链已覆盖"进入评审的错觉。

### ⑤ 矩阵

`appveyor.yml` 只跑一次 `--ui-smoke`（默认 96 DPI + 9pt + Dark）。
DL/SC 两处规范反复强调"字号 8–24pt 可调""PerMonitorV2""三套暗色"，零覆盖。

### ⑥ 视觉门

`tools/ui-check.py` 用 PIL 做圆角/禁用态/暗色纯度判断，未进 CI（无 pip 依赖策略），
也没有基线图（只能判"像暗色"，判不了"改坏了"）。

## 影响

- 修一个面板的布局 bug 后，**没有任何回归保护**（下次改别的面板又复发）；
- 文字类 bug 只有用户能发现（历史上多次如此）；
- 规范类漂移靠人工 grep（已由 C8 的 L0 门部分解决）；
- 团队对"UI 有测试"的信心高于实际覆盖。

## 修复方向（详见 `docs/UI-UX-VERIFICATION.md` §7 收敛计划）

1. **P0**：`UiTreeDumper` 增 `textWidth/textHeight`（`TextRenderer.MeasureText`），
   `ui-tree-check.py` 加 `textFit` 断言（`textWidth > clientWidth - padding*2` 即失败）。
2. **P0**：`MainFormSmoke` 打开 6 个高频侧栏面板并各自 dump；登记表下限 16 → 22。
3. **P1**：`DpiScale.SetOverrideForTests(float)` + 矩阵脚本（96/144 × 9/14pt 四格起步）。
4. **P1**：dump 增 `hitRegions`（自绘命中矩形），R1 扩展到命中区。
5. **P2**：R2 要么补真实 Tab 序，要么删除；L3 基线 diff 作为独立 lane（允许失败）。

## 建议动作

`cs-feat`（验证基础设施增强）。第 1、2 项是本审计里**性价比最高**的两件事：
约 1 人日，直接补上历史最高频 bug 类型的门。
