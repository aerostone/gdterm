# UI/UX 验证体系（L0–L4）

> 版本：1.0（2026-10-07）
> 配套：`docs/DESIGN-LANGUAGE.md`（设计 SSOT）、`docs/UI-SCALING-CONVENTIONS.md`（缩放 SSOT）
> 配套审计：`.codestable/audits/2026-10-07-ui-ux-quality/`

这份文档回答一个问题：**设计语言写了那么多"必须"，谁来保证它没被违反？**

答案不是"评审时看一眼"，而是五层门 + 一张覆盖登记表。每一层只负责它能可靠判定的事，
判不了的必须**显式标为人工**，不允许"看起来有门其实没门"。

---

## 1. 为什么需要分层

历史 UI 事故按根因分布（见 2026-07-26 审计、UI-SCALING-CONVENTIONS 背景段、本次审计）：

| 根因 | 撞过的现象 | 靠什么才发现 | 可自动判定 |
|---|---|---|---|
| 几何越界/重叠/遮挡 | 工具栏盖住表体、文字互相压 | dump 断言（L1 R5） | ✅ 结构 |
| 文字放不下/被裁 | 长主机名截断、按钮文字挤两行、标签贴顶 | CI 截图 + 人眼 | ✅ 度量（当前缺） |
| DPI / 字号放大 | 双倍缩放、14pt 下窗体溢出 | 用户报障 | ✅ 矩阵（当前缺） |
| 规范漂移 | "确定"当 Primary、裸色、固定行距 | 人工 grep | ✅ 静态（已补） |
| 状态缺失 | 空列表一片黑、按钮可重复点、无等待感 | 用户报障 | ⚠️ 半自动 |
| 语义/无障碍 | 屏幕阅读器读不出、命中区太小 | 几乎从不 | ⚠️ 半自动 |
| 主题 | 切 OLED 后部分面板还是旧色 | 用户报障 | ⚠️ 需矩阵 |

结论：**几何类已经被 L1 覆盖，剩下的洞集中在"文字度量、状态轴、矩阵轴、语义轴"。**

---

## 2. 五层模型

| 层 | 手段 | 工具 | 依赖 | 在 CI 的位置 | 红了意味着 |
|---|---|---|---|---|---|
| **L0** 静态规范 | 源码规则匹配 + 基线棘轮 | `tools/ui-ux-lint.py` | 无（stdlib） | build 后、ui-smoke 前，秒级 | 新代码违反 SSOT 明确条款 |
| **L1** 结构 | 真实控件树 dump 断言 | `tools/ui-tree-check.py` + `UiTreeDumper` | Windows 桌面会话 | ui-smoke 之后 | 布局算错了（几何/停靠/触击/键盘） |
| **L2** 矩阵 | 同 dump 在 DPI×字号×主题 下重跑 | 待建（§4.2） | Windows + 缩放注入 | 独立 job（慢） | 只在某些缩放/字号下坏 |
| **L3** 视觉基线 | 截图与基线 diff | `tools/ui-check.py`（当前未进 CI） | Windows + 稳定渲染 | 独立 job（可选） | 视觉回归（颜色/间距/字形） |
| **L4** 运行时+人工 | 埋点/耗时/失败提示 + 验收清单 | `ObservabilityTests` + 清单（§4.4） | 真实使用 | 发版前 | 体验退化但布局没错 |

原则：
- **能从源码判的不要放 CI 桌面 job**（L0 把 90% 的规范检查变成秒级）；
- **能用 dump 判的不要用截图**（dump 是数值断言，截图是像素启发式，后者天生脆）；
- **矩阵只在必要时跑**（L2 慢，按 `src/Gdterm.UI/**` 或 SSOT 变更触发）。

---

## 3. L0：静态规范门（已落地）

### 3.1 命令

```bash
python3 tools/ui-ux-lint.py                  # 门禁模式：基线外有新增 => exit 1
python3 tools/ui-ux-lint.py --strict         # 全量：打印全部存量债（清理时用）
python3 tools/ui-ux-lint.py --json out.json  # 机器可读
python3 tools/ui-ux-lint.py --write-baseline # 修完存量后收紧基线
python3 tools/gen-tokens.py                  # 重新生成 docs/tokens.json + DESIGN-LANGUAGE §1 表（改色后跑）
python3 tools/gen-tokens.py --check          # 门禁模式：文档/JSON 与 GdtermColorTable.cs 不一致 => exit 1
```

CI 里已挂在 `appveyor.yml` 的 test_script 段（编码守卫之后、ui-smoke 之后）。
`gen-tokens --check` 同段紧随其后：色彩 token 的 SSOT 是 `GdtermColorTable.cs`，
`docs/tokens.json` 与 DESIGN-LANGUAGE §1 表都是它的机器投影（审计 C9-1：手抄表已实证漂移）。

### 3.2 规则表

| ID | 规则 | 依据 | 当前存量 |
|---|---|---|---|
| R01 | 裸 `Color.FromArgb(` | DL §1.2 | 0 |
| R02 | 裸 `new Font("非等宽字体"` | DL §2.2 / SC 规则 3、4c | 0 |
| R03 | 字面量 `new Size(N, N)` | SC 规则 1/4 | 0 |
| R04 | 字面量 `Height/Width = N`(≥20) | SC 规则 4 | 8 |
| R05 | 字面量 `y += N` | DL §2.3 / SC 规则 4b | 0 |
| R06 | "确定/OK" 作 Primary | DL §4.1 | **0**（2026-10-07 已全部改为具体动词） |
| R07 | `AntdUI.Button` 同时写 `Type` 与 `BackColor` | DL §9.3 | **0**（审计时 7 处系旧规则误报 + 双写已清） |
| R08 | 成功/完成类信息用阻断 MessageBox | DL §9.3 | 3 |
| R09 | 列表型 UI 无空态引导（认 `EmptyStateView` 组件） | UX 审计 C1 | 8（11→8：历史/转发/通知已挂组件） |
| R10 | `new Font("Consolas", … UIFontSize …)` | DL §2.1/§4.2 | **0**（已收口 `FormFontPolicy.MonoFont()`） |
| R11 | Form/Control 未登记覆盖状态 | 本文 §5 | 0 |
| R12 | `AntdUI.Select` 设了 AutoSize | SC 规则 4d | 4 |
| R13 | 裸像素 `Margin/Padding(a,b,c,d)` | SC 规则 4/4d | 24 |

### 3.3 基线棘轮（为什么不直接"必须为 0"）

存量债 47 条（2026-10-07 审计修复前 107 条），直接归零意味着一次性大改 UI 代码，风险高于收益。棘轮的正确姿势：

- **今天绿**（基线 46 键）；
- **新增即红**（新写的 `确定`+Primary 立刻失败）；
- **修一条收紧一条**（`--write-baseline`），基线只降不升 —— 这条要写进 PR 模板。

已知局限（诚实记录）：
- 基线键 = `规则|文件|片段`，**不含行号**（容忍插入行）。同文件内两处完全相同片段会折叠成 1 键（当前 47 命中 → 46 键）。文本级棘轮，不追求精确到实例。
- 只做词法匹配，无法识别"变量拼出来的颜色"或"运行时注入的尺寸"。这类归 L1/L2。

---

## 4. L1–L4 现状与缺口

### 4.1 L1 结构门（已覆盖 R1–R6 + 自绘命中区）

现有规则（`tools/ui-tree-check.py`，CI 对 ui-smoke 全量 dump 跑）：

| ID | 断言 | 备注 |
|---|---|---|
| R1 | 可见 enabled 交互叶 abs 宽高不同时 < 32px | 另见下方 hitRegions 扩展 |
| ~~R2~~ | ~~同父 tabIndex 非零值不重复~~ | **已删**（审计 C7-④）：全仓显式 TabIndex 仅 1 处 → 规则恒过，永不失败的门比没有更坏；焦点链覆盖由 R4 承接 |
| R3 | 每窗至少一个关闭语义控件 | 有免判清单（含 6 个侧栏面板：关闭语义由宿主侧栏承担） |
| R4 | 交互叶 TabStop=false 数为 0 | mainform 独立分支；**2026-11-01 复核激活**：原判定 `tabStop is False` 对字符串 dump 永不成立，规则从未生效（2026-09-25 spike 的“零命中”结论来自人工分析，非此规则） |
| R5 | Dock=Fill 与边停靠兄弟不相交 | "工具栏盖住表体"的可测签名；C# 侧 `AssertNoDockOverlap` 同口径 |
| R6 | 文本适配：单行所需宽/换行所需高 vs 可用空间 | 默认 warn-only（`--textfit-strict` 可翻硬门；**warn-only 是告警不是阻断门**）；度量来自 dump 的 `textSingleW/textWrapH/wrap`（真实 GDI）；**2026-11-01 复核激活**：autoSize/wrap 字符串布尔修复前整条规则恒 0 受检 |

自绘命中区（审计 C5/C7-③ 落地）：实现 `IHitRegionProvider` 的控件（当前：`TabContainerControl`
的标签关闭钮）在 dump 里输出 `hitRegions`（屏幕坐标），检查器按最小边 ≥24 逻辑 px 判
（WCAG 2.2 SC 2.5.8，与 `TabChromePainter.GetCloseHitRect` 的扩张口径同源）。
**量纲（2026-11-01 复核修正）**：app 是 PerMonitorV2，dump 几何为设备像素，阈值 = 24×dpi/96
设备 px（高 dpi 要求更多设备像素）；此前 24×96/dpi 方向反了，被 96dpi CI 掩蔽。
**mainform 同样受判**：此前 mainform 分支提前 continue 把命中区与 R6 的真实目标（主窗标签）
跳掉了；现两条通用规则在分支前执行，mainform 专属规则（树宽/底栏/Tab Fill）不变。

外加：兄弟重叠、子越界、零尺寸可见叶、表行高 24–34、AntdUI.Button 左右 padding 全 0。

**面板覆盖（审计 C7-2 落地）**：`UiSmokeRunner` 除窗体外，额外在 360×480 宿主窗里
Show+dump 6 个高频侧栏（command-history / port-forward / secret-scan / key-binding /
toolbox / logon-script）；空数据源面板（command-history / port-forward）`EmptyStateView`
**先断言存在、再断言可见**（2026-11-01 复核：“找到才检查”会在组件被删后假绿；
其余面板空态未接，属已知遗留，不强制存在）。

**主窗场景要求（2026-11-01 复核）**：MainFormSmoke 开 1 个本地终端标签
（`OpenLocalTerminal`，本地 PTY 不连主机）并断言 `GetHitRegions()≥1`——
空会话下 dump 的 hitRegions 永远为空，命中区规则受检=0 即空过。

**判定器可达性硬门（2026-11-01 复核）**：空控件树 → 该文件 FAIL（不再 ALL OK）；
每条新规则打印受检对象数（`自绘命中区受检=N`/`文本适配受检=N`），0 对象只报 info 不假绿；
CI 失败路径修正：smoke 非零不再立即 throw，dump/截图先上传再宣判（先留证据、后红）。

**已闭合的缺口（2026-10-07 审计修复）**：① 文字适配断言（R6 + dump 度量字段）；
② 面板零覆盖（+6 dump）；③ 自绘命中区不可见（hitRegions + R1 扩展）；⑤ R2 空转（已删）。

**2026-11-01 复核修正（as-desktop-ui-verify 技能按证据契约逐条验证后）**：
① 字符串布尔陷阱：dump 布尔是 `"true"/"false"` 字符串，直接 truthy 判使 R4/R6 成死规则、
不可见节点过滤失效——统一 `sbool()` 修复；② 命中区量纲倒置（高 dpi 假过）；
③ mainform 分支跳过新规则（真实目标 0 受检）；④ 空树假绿；⑤ `--textfit-strict` 放
第一位崩（argv 未过滤 flag）；⑥ CI 失败不留证；⑦ 空态“找到才检查”反模式；
⑧ smoke 无 tab 场景。反例验证：96/192dpi 正反例、空树、tabStop=false、不可见零尺寸叶，
均按预期规则标识失败（合成例只证明判定算法，不等同真实高 DPI 布局验证）。

**仍开放的缺口**：

1. **状态轴只有“默认”。** 只有 `keepass-manager` 跑了空+满两态，其余全是满/默认态。
   禁用、错误、忙碌、窄窗全部无 dump。
2. **L2 矩阵未建**（见 §4.2）——历史事故全在 DPI/字号/主题两根轴上，一次都没跑过。

### 4.2 L2 矩阵门（未建）—— 最大缺口

必须在三个轴上重跑 L1 断言（判断标准仍是"无越界/无重叠/文字不裁切/触击达标"）：

| 轴 | 取值 | 历史事故 |
|---|---|---|
| DPI | 96 / 120 / 144 / 192 | 双倍缩放、控件被裁 |
| UI 字号 | 9 / 11 / 14 / 20pt | 14pt 下窗体溢出、行重叠 |
| 主题 | Dark / Darker / OLED | 切主题后旧色残留 |

实现前提（两个小改动，都不难）：
- `DpiScale` 加测试钩子（`DpiScale.SetOverrideForTests(float)`），使非 UI 线程也能按目标比例算；
  否则只能靠真实改系统 DPI，CI 不可行。
- `Program.GlobalAppearance.UIFontSize` 与 `GdtermColorTable.ApplyTheme` 已可编程注入（现成）。

成本估计：钩子 0.5d + 矩阵脚本 1d + 收紧既有 dump 假设 0.5d。

### 4.3 L3 视觉基线（有工具，未进 CI）

`tools/ui-check.py` 用 PIL 做像素启发式（圆角半径、禁用态对比、暗色纯度）。
三个问题：① 依赖 PIL → 违背零依赖原则，故未进 CI；② 只覆盖 3 张固定 PNG；
③ 没有基线 diff，只能判"像不像暗色主题"，判不了"改坏了没有"。

正确形态：`PrintWindow(hwnd)` 稳定抓图 → 与 `design/baseline/*.png` 做
**容差 diff（允许 ≤0.3% 像素差异）** → 失败上传 diff 图产物。只在白名单窗体（无动画、
无时间戳、无闪烁光标）上跑。

### 4.4 L4 运行时 + 人工（补位，不是借口）

自动化判不了的必须显式人工化，且**清单要跟着功能走**：

- 发版前人工过一遍 `docs/UI-UX-VERIFICATION.md` §6 验收清单（12 项）；
- 关键路径埋点：主窗首次绘制耗时、连接建立耗时、面板首次打开耗时、UI 线程 >200ms 卡顿计数
  （`ObservabilityTests` 已有骨架，扩展到 UI 线程计时）；
- 失败提示质量：错误必须以 `AntdUI.Message.error` 出现在窗体内 + 可复制详情，
  不允许"静默失败"（这是 UX 问题，也是可观测性问题）。

---

## 5. 覆盖登记表（R11 的机制）

文件：`.codestable/tools/ui-coverage-registry.txt`

```
Forms/MainForm.cs            | dump:mainform
Forms/PasswordGeneratorForm.cs | dump:pwd-generator
Controls/PortForwardPanel.cs | none            <- 显式技术债
Controls/MenuTip.cs          | manual:瞬态
Controls/FilePaneModel.cs    | n/a:非UI
```

规则：
1. `Controls/` 与 `Forms/` 下每个 `.cs`（除 `*.Designer.cs`）**必须在表里有行**，否则 R11 红。
   新增面板不可能"悄悄没有验证"。
2. `dump:` 条目数有下限（文件头 `# floor: 22`，2026-10-07 审计 C7-2 后：16→22）。删掉/降级任何 dump 覆盖 → 门红。
3. `none` 是允许的，但必须写上——它把"未验证"从隐性变成**可统计的显性债**
   （当前 `dump=23 / manual=5 / none=24 / n/a=1`，门每次输出这行）。

---

## 6. SSOT 条款 → 门 映射（谁保证这一条）

✅=已自动覆盖　⚠️=半自动（需人工确认）　❌=无门（缺口）　🔧=本次新增

| SSOT 条款 | 要求 | 门 | 状态 |
|---|---|---|---|
| DL §1.1–1.2 | 颜色只走 token，禁裸值 | L0 R01 | ✅🔧 |
| DL §2.1 | UI 字体 vs 终端字体分离 | L0 R02 / R10 | ✅🔧（R10 揭示 40 处债） |
| DL §2.2 | 字号来自阶梯 | L1(缺 font.size 断言) | ❌ |
| DL §2.3 | 行距 `RowStep` | L0 R05 | ✅🔧 |
| DL §3.1 | 间距 4px 基 + DpiScale | L0 R13 | ✅🔧（24 处债） |
| DL §3.2 | 容器优先 Dock/TableLayout | 无 | ❌ 人工评审 |
| DL §4.1 | 每窗体 ≤1 Primary；禁"确定"作 Primary | L0 R06 + L1(缺计数) | ⚠️🔧 |
| DL §4.1 | 按钮 AutoSize + 水平 Padding | L1(padding=0 检查) | ✅ |
| DL §4.2 | 输入框底色/描边/聚焦反馈 | 无（需 dump 带颜色） | ❌ |
| DL §4.4 | 弹窗关闭绑定 / ESC | L1 R3 | ✅ |
| DL §4.6 | 列表/树空态 | L0 R09 | ✅🔧（11 处债） |
| DL §6 | 动效与反馈（等待/成功/失败可见） | 无 | ❌（本次审计 C2） |
| DL §9.3 | 非阻断提示用 Message，阻断确认用 MessageBox | L0 R08 | ✅🔧 |
| DL §9.3 | AntdUI.Window 不叠 Apply | 无 | ❌ 可加 R14 |
| DL §9.3 | 不覆盖 AntdUI 语义色 | L0 R07 | ✅🔧 |
| DL §7.3 | 预提交 grep 快查（3 条） | L0 R01/R02/R05 | ✅🔧 取代手工 grep |
| SC 规则 1 | 文本控件禁固定 Size | L0 R03 | ✅🔧 |
| SC 规则 2 | 禁绝对坐标 | 无 | ❌ 人工评审 |
| SC 规则 3/4 | 固定值必须 DpiScale | L0 R04 | ✅🔧（8 处债） |
| SC 规则 4b | 行距字体驱动 | L0 R05 | ✅🔧 |
| SC 规则 4c | 禁裸 YaHei（Win7） | L0 R02 | ✅🔧 |
| SC 规则 4d | Select 不设 AutoSize；密集条走 DpiScale | L0 R12 / R13 | ✅🔧 |
| SC 规则 5 | Form 收口（Apply / 不设 AutoScaleMode） | 无 | ❌ 可加 R14 |
| 触击目标 ≥32px（Control） / ≥24px（自绘交互区） | | L1 R1 + hitRegions | ✅ |
| 焦点链合理 | | L1 R4（R2 已删：空转门不保留） | ⚠️ 无真实 Tab 序数据 |
| 键盘可达（TabStop） | | L1 R4 | ✅ |
| 停靠不遮挡 | | L1 R5 | ✅ |
| **文字不裁切** | | L1 R6（默认 warn，`--textfit-strict` 可翻硬门） | ⚠️🔧 基线积累中 |
| **DPI×字号×主题** | | L2 | ❌ 最大缺口 |
| 视觉回归 | | L3 | ❌ 未进 CI |
| 无障碍名称 | | `UiA11y.AutoName`（Form/TabPage/ListBox/ListView/TextBox/RichTextBox/Tree/ProgressBar/CheckBox/Button 自动命名） | ⚠️🔧 图标钮待逐一补 |
| 空态/加载态/禁用态 | | L0 R09 + `EmptyStateView`（历史/转发/通知已挂，dump 断言可见） | ⚠️🔧 剩 8 处 |
| 防重复提交 | | `BusyScope`（扫描中心/转发启动已接入） | ⚠️🔧 样板已立，待铺开 |

**这张表就是验收标准**：新增任何 SSOT 条款，必须同时在表里指出它由哪一层守；
写不出层，就说明这条只能靠人 —— 那也要写"人工"，不能留空。

---

## 7. 收敛计划

### P0（本迭代，闭合"最大的洞"）—— 2026-10-07 审计已全部落地 ✅

| # | 事项 | 工作量 | 状态 |
|---|---|---|---|
| 1 | L1 加 `textFit` 断言：dump 增字段 `textSingleW/textWrapH/wrap`（真实 GDI 度量），R6 断言 | 0.5d | ✅（默认 warn，`--textfit-strict` 可翻硬门） |
| 2 | 面板进 dump：6 个高频侧栏在 360×480 宿主窗里 Show + dump | 0.5d | ✅（SidePanels 用例 + 空态可见断言） |
| 3 | L0 R11：6 个 `none` 侧栏提升为 `dump:`，floor 16→22 | 0.2d | ✅ |
| 4 | `FormFontPolicy.MonoFont()` 收口 R10 的 40 处 | 0.5d | ✅（L0 R10 归零，基线 94→46 键） |

### P1（下一迭代）

| # | 事项 | 工作量 | 状态 |
|---|---|---|---|
| 5 | L2 矩阵门（先做 96/144 DPI × 9/14pt 四格，主题后补） | 1.5d | ❌ **当前最大缺口** |
| 6 | L1 加 `accessibleName` 断言 + 图标按钮补 `AccessibleName` | 1d | ⚠️ 半完成：`UiA11y.AutoName` 自动命名已铺（Form/TabPage/列表/文本/进度等 9 类）；图标钮逐一补 + dump 带 accessibleName 字段待做 |
| 7 | 自绘命中区纳入 R1（dump 增 `hitRegions` 节点） | 0.5d | ✅（`IHitRegionProvider` + 检查器 ≥24 逻辑 px） |
| 8 | 空态标准组件 `EmptyStateView` 铺 11 处 | 1d | ⚠️ 3/11（历史/转发/通知）；组件已含可见性语义 |
| 9 | 忙碌/防重入统一封装 `BusyScope` | 1.5d | ⚠️ 组件已落地（按钮+Cursor+状态行+finally 恢复）；已接入扫描中心/转发启动，其余长操作待铺 |
| 9b | 主题切换止血：`ThemeChanged` 订阅（外壳+标签栏+空态重映射） | 0.5d | ✅（审计 C6；完整重建/订阅是后续） |

### P2（清理）

| # | 事项 | 状态 |
|---|---|
| 10 | R06 "确定"→具体动词（✅ 10 处已改）；R07 去语义色覆盖（✅）；R04/R13 走 DpiScale（8/24 处债待清） | ⚠️ |
| 11 | L3 基线 diff 进 CI（独立 lane，允许失败不阻断 master） | ❌ |
| 12 | 主题切换改为"重建外壳控件"或"token 订阅"（审计 C6；`ThemeChanged` 止血已完成） | ⚠️ |
| 13 | 边框 token 增 `BorderStrong`（✅ `#66757F` = 3.57:1；`TabChromePainter`/`EmptyStateView` 已用） | ✅ |
| 14 | 色彩 token 表生成化：`gen-tokens.py`（SSOT=GdtermColorTable.cs，--check 进 CI） | ✅（审计 C9-1） |

---

## 8. 失败处置

| 门 | 红了先看 | 不许做的 |
|---|---|---|
| L0 | 报错的 `file:line` + 规则依据条款 | ❌ 直接 `--write-baseline` 把新违规吸收进基线 |
| L1 | `ui-smoke/*.json`（产物已上传）+ 失败断言行 | ❌ 调大 `TOL`/`HIT_MIN` 让断言过 |
| L2 | 矩阵里哪一个格子红（DPI/字号/主题） | ❌ 只修 96dpi 那格 |
| L3 | diff 图 | ❌ 无条件重刷基线 |

基线收紧的唯一合法时机：**修完存量、测试通过、同 PR 内**。

---

## 9. 人工验收清单（发版前，12 项）

1. 空状态：把 data/ 清空启动，每个面板都能看到"下一步做什么"。
2. 首次运行：向导可从任意步退出且不残留半配置。
3. 长文本：主机名 80 字符、分组路径 5 层，树与标签不裁切。
4. 大字号：UIFontSize=14，主窗、连接对话框、各侧栏无重叠。
5. 高 DPI：150% 缩放下按钮/输入框不糊、不裁。
6. 主题：Dark → Darker → OLED 来回切，无残留旧色、无闪白。
7. 键盘：全流程只用键盘可达（Tab 序合理、ESC 关闭、Enter 提交）。
8. 忙碌态：连接中/扫描中/传输中，有明确反馈且按钮不可重复点。
9. 错误态：断开网络后连接，错误提示可读、可复制、有下一步建议。
10. 破坏性操作：删除连接/清空历史有二次确认，且默认焦点在"取消"。
11. 通知中心/传输中心：与 Toast 显示一致，未读计数正确。
12. 100% 缩放的 1366×768 笔记本屏：主窗最小尺寸下不出现横向滚动。
