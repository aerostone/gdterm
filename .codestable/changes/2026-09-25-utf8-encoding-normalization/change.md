---
doc_type: change
kind: refactor
slug: 2026-09-25-utf8-encoding-normalization
status: accepted
mode: standard
summary: 编码归一：全仓文本统一为纯 UTF-8，.ps1 声明 BOM，并加机检守卫与 .editorconfig
tags: [encoding, utf8, tooling, ci]
risk:
  level: low
  reasons:
    - 仅文件字节层的 BOM 增删（每文件 3 字节）与新增只读校验工具，无行为改动
    - 不触碰第三方源码与随包二进制
model_route:
  strategy: retain-high
  reason: 只有一处判断点（BOM 策略口径与白名单边界），其余为机械归一；但涉及全仓字节与 CI 门，口径判断保留高阶模型
contract:
  include:
    - .editorconfig
    - appveyor.yml
    - src/Gdterm.Tests/Ui/MainFormSmoke.cs
    - src/Gdterm.Tests/Ui/UiSmokeRunner.cs
    - src/Gdterm.Tests/Ui/UiTreeDumper.cs
    - src/Gdterm.Tools/Gdterm.Tools.csproj
    - src/Gdterm.Tools/Scanning/BuiltinPlugins.cs
    - src/Gdterm.UI/Controls/CommandHistoryPanel.cs
    - src/Gdterm.UI/Controls/ConnectionQuickJumpForm.cs
    - src/Gdterm.UI/Controls/DangerousCommandDialog.cs
    - src/Gdterm.UI/Controls/KeyBindingPanel.cs
    - src/Gdterm.UI/Controls/MultiChannelPanel.cs
    - src/Gdterm.UI/Controls/NotificationCenterPanel.cs
    - src/Gdterm.UI/Controls/SnippetSearchPanel.cs
    - src/Gdterm.UI/Controls/ToolboxPanel.cs
    - src/Gdterm.UI/Forms/ConnectionDialog.cs
    - src/Gdterm.UI/Forms/KeePassEntryPicker.cs
    - src/Gdterm.UI/Forms/PasswordHealthForm.cs
    - src/Gdterm.UI/Forms/ScannerCenterForm.cs
    - src/Gdterm.UI/Services/MasterPasswordPrompt.cs
    - tools/check-encoding.py
    - tools/pack-release.ps1
    - .codestable/changes/2026-09-25-utf8-encoding-normalization/**
  exclude: []
  preexisting_changes:
    - .codestable/.runtime/current-package
  baseline:
    git_head: fd1780b289d51468fbf05332450dd9ac6d2110c2
    dirty_hashes:
      .codestable/.runtime/current-package: 3cf21d23df3a321f2b4a416860d1e9b6626ed21099620e629ef5b8d7240d60b9
  architecture_impact: unchanged
  architecture_reason: 不改变模块边界、依赖方向与公开接口；仅统一文件编码并新增构建期校验工具
  requirement_impact: unchanged
  requirement_reason: 编码声明不改变任何能力愿景、用户价值与验收标准，无需求文档需回写
  requirement_refs: []
  context_refs:
    design:
      - tools/check-encoding.py
      - .editorconfig
      - appveyor.yml
    impl:
      - tools/pack-release.ps1
      - src/Gdterm.UI/Forms/ScannerCenterForm.cs
      - src/Gdterm.Tests/Ui/UiSmokeRunner.cs
    accept:
      - tools/check-encoding.py
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-utf8-encoding-normalization/change.md
      depends_on: []
  evidence_ledger: false
---

## 目标与边界

把仓库的文本编码从"混着 BOM"收敛成**单一、可机检**的约定：默认纯 UTF-8 无 BOM，
只有三处有技术依据的位置允许（其中一处强制）带 BOM。

- **F1（修正）** 项目自有的 18 个 `.cs`/`.csproj` 去掉 UTF-8 BOM，成为纯 UTF-8。
- **F2（修正）** `tools/pack-release.ps1` 补上 BOM：Windows PowerShell 5.1 对无 BOM
  脚本按系统 ANSI 码页解释，该脚本含中文提示文本，一直被读成乱码（AI 与 PS 5.1
  都能读对，但 5.1 不会去猜）。这是全仓唯一一处"必须加 BOM"的缺口。
- **F3（新增）** `tools/check-encoding.py` 机检守卫 + `.editorconfig` 声明式 SSOT +
  AppVeyor `test_script` 接线，让上面两条口径从此不会再退化。

**成功标准**：守卫在仓库上 exit 0；19 项 RED 全部消失；18 个去 BOM 文件的正文
与 `HEAD` blob 去掉 3 字节前缀后**逐字节相同**；CI 日志出现守卫的 ALL OK。

**明确不做**：
- 不动 `third_party/`（VtNetCore 上游源码，含 67 个带 BOM 的 `.cs`）、`lib/`、`vendor/`
  —— 改了会让上游 diff 永远脏，且这些文件的 BOM 是上游的形状。
- 不动两个 CI 产物夹具的 BOM（理由见 D2）。
- 不改任何代码语义：本次 diff 的全部内容就是"首字节的 3 个字节"。
- 不批量统一缩进：全仓 532 个文本文件**已经是 LF**，`end_of_line = lf` 是把现状写下来。

## 行为增量

- **ADDED** `.editorconfig`（编码与换行的声明式 SSOT）、`tools/check-encoding.py`
  （只读守卫，仅 stdlib）、`appveyor.yml` 的一个门步骤。
- **MODIFIED** 18 个文件各少 3 字节（去 BOM）；`tools/pack-release.ps1` 多 3 字节（加 BOM）。
- **REMOVED** 无。
- **运行时行为** 零变化。Roslyn 对无 BOM 源码先按 UTF-8 严格解码、失败才回退系统码页，
  而删除 BOM 的这 18 个文件本就含非 ASCII 中文且是合法 UTF-8，故编译产物无差异。

## 设计与契约

### D1 默认纯 UTF-8，BOM 只留在三处有依据的位置

口径不是"审美洁癖"，每一处都有一条具体依据：

| 位置 | 规则 | 依据 |
|---|---|---|
| 其余全部文本（`.cs`/`.csproj`/`.md`/`.json`/`.yml`/`.py`/`.sln`…） | **纯 UTF-8，禁 BOM** | Roslyn `EncodedStringText` 对无 BOM 源码先按 UTF-8 严格解码；`.md` 的 frontmatter 解析要求首字节是 `---`（BOM 会直接破坏解析）；`.py` 的 shebang 必须在第 0 字节，BOM 会让 `ast.parse` 报 `invalid non-printable character U+FEFF` |
| `.ps1` 且含非 ASCII | **必须带 BOM** | Windows PowerShell 5.1 对无 BOM 脚本按系统 ANSI 码页解释（与 Roslyn、pwsh 的行为都不同） |
| `third_party/`、`lib/`、`vendor/` | **允许带 BOM** | 上游/随包形状，改了会永久污染与上游的 diff |
| `.codestable/changes/*/fixtures/*` | **允许带 BOM** | 控件树 dumper 用 `Encoding.UTF8` 写出，产物天然带 BOM（见 D2） |

### D2 夹具保留 BOM —— 对我前一版方案的一处修正（减项）

本包初稿把 `.codestable/changes/2026-09-25-dangerous-cmd-dock-overlap/fixtures/` 下
两个 `.json` 也列进"去 BOM"，**现已改为保留并加窄例外**，三条理由：

1. 它们不是源码，是 `UiTreeDumper` 的**逐字节冻结产物**；BOM 正是 dumper 的输出形状。
   去掉它，夹具就不再是"真实 CI 产物"，R5 的 RED 证明反而被削弱。
2. 消费者 `tools/ui-tree-check.py` 本就按 `utf-8-sig` 读，BOM 是被设计容忍的。
3. 该夹具所属的 `2026-09-25-dangerous-cmd-dock-overlap` 已是 `accepted`，不宜回头改
   已闭包包的产物。

代价：白名单多一条路径规则。收益：规则不再与自己的 dumper 长期对抗 ——
否则下一次从 CI 拷回产物夹具时，守卫会立刻报红。

### D3 `.editorconfig` 是声明，守卫是强制，两者同口径

`.editorconfig` 让编辑器"保存即正确"（VS Code / VS 都原生读），
`tools/check-encoding.py` 让 CI"提交即拦下"。两者口径逐条对齐：
`[*.ps1] charset = utf-8-bom` 对应守卫的"`.ps1` 允许带 BOM"，
其余 `charset = utf-8` 对应"禁 BOM"。为避免"声明的比强制的更严"造成的假冲突，
守卫对 `.ps1` 采取**允许但只对非 ASCII 强制**：纯 ASCII 的 `tools/gen-version.ps1`
（构建期由 `Gdterm.UI.csproj` 以 `powershell -File` 调用）在 ANSI 读法下字节相同，
故不必为本包多改它一个字节。

### D4 接线位置：排在控件树门之后

`test_script` 已是单个 `ps:` 块，守卫直接复用同一块的 `$py`/`$pyArgs` 与
`PYTHONIOENCODING=utf-8`，追加在 `ui-tree-check.py` 之后。这样顺序是
**跑测试 → 传产物 → 树检门 → 编码门**，任何一道门红了都还留着 PNG/JSON 可诊断。

### 名词与编排

- **白名单三处**：`third_party/`·`lib/`·`vendor/` 前缀；`/fixtures/` 路径段；
  `.ps1` 扩展名。三者都写在守卫的 `bom_allowed()` 里，是唯一事实来源。
- **两条规则**：R1 非二进制文件必须严格 UTF-8 解码；R2/R3 BOM 只许出现在白名单内，
  且非 ASCII 的 `.ps1` 必须带 BOM。
- **二进制豁免**：按扩展名白名单跳过（`.dll/.exe/.png/.zip/…`），当前 16 个。
- **枚举方式**：优先 `git ls-files`（只看受控文件，不误伤构建产物），
  非 git 目录回退到文件系统遍历 —— 后者让守卫可在临时目录里被反向测试。

## 执行计划

| # | 步骤 | 验证 |
|---|---|---|
| 1 | 先落地守卫本体与口径，在未修的仓库上跑出 RED | `tools/check-encoding.py` exit 1，19 项 FAIL（18 处 unexpected BOM + 1 处 .ps1 无 BOM） |
| 2 | 以 `HEAD` blob 为基准去 18 处 BOM、给 `pack-release.ps1` 补 BOM | 每文件正文与 `HEAD` blob 去前缀后逐字节相同；守卫转 GREEN exit 0 |
| 3 | 加 `.editorconfig` 并把守卫接进 `appveyor.yml` | YAML 可解析、门在树检门之后、字节卫生（无 BOM/无 CRLF）无回退 |
| 4 | 包内反向驱动 `fixtures/check-utf8-normalization.py` | 覆盖守卫本体、牙齿（临时目录合成样本）、字节不变性、白名单精确集合、接线、卫生 |

## 执行证据

完整证据（RED/GREEN 原始输出、逐文件字节比对、驱动明细、复现命令）见同级
[`evidence.md`](evidence.md)。摘要：

- **RED（权威）** 在 `git archive HEAD` 导出的快照上跑守卫：`ENCODING-CHECK FAIL: 19`、exit 1
  —— 18 处 `unexpected UTF-8 BOM` 加 1 处 `tools/pack-release.ps1` 的 ANSI 读法告警。
  （用快照而非当场跑，是因为本会话第一次跑时 `MainFormSmoke.cs` 已被另一个脚本先去掉了 BOM，
  当场只会报 18 项。）
- **GREEN** 19 个文件的 `git diff --numstat` 恰为 `1/1`，正文与 `HEAD` blob 逐字节相同；
  守卫转为 `551 text files checked` / `ALL OK` / exit 0（提交后含 4 个新受控文件为 555）。
- **CI** `appveyor.yml` 追加编码门（在树检门之后、复用同一块的 `$py` 与 `PYTHONIOENCODING`），
  非零退出即 `throw`，不静默跳过。
- **包内驱动** 42 项 ALL OK；其中 S2 是牙齿证明：合成样本恰好报 3 处 FAIL，而给那个无 BOM 的
  `.ps1` 补上 BOM 后该条消失 —— 规则按内容判定而非文件名判定。
- **反向检查** 受控改动 = 19 个字节层文件 + `appveyor.yml`（唯一实质改动），无越界；
  仓库 BOM 存量全在白名单内。四处实施偏离（D2 减项、`.ps1` 口径、白名单粒度、驱动自身缺陷）见 `evidence.md`。

## 验收结果

| 事实 | 值 |
| --- | --- |
| 构建 | `0.1.331` · commit `e26821d` · **success** · 2026-09-25T10:34:09Z → 10:43:31Z（9m22s） |
| 耗时说明 | 远超 ~1m55s 常态，因编辑 `appveyor.yml` 使 `freerdp-bin` 缓存键失效、FreeRDP 当次重建 |
| 数量 | 单元测试 163/0 · UI 冒烟 6/0 · `dialogs-one-fail=0` · 24 个产物 |

**S1 守卫在 CI 真跑** 日志 1805/1806 行：

```
encoding guard: 555 text files checked, 16 binary skipped (via git)
ENCODING-CHECK ALL OK
```

本地提交后同样报 **555**（提交前报 551，差的 4 个正是 `.editorconfig`、`tools/check-encoding.py`、
`change.md`、夹具驱动这 4 个新受控文件）——两侧逐数一致，说明 CI 里真的枚举了 `git ls-files`
而不是回退到遍历。

**S2 门序正确** 产物末行 `transfer-progress.json` 上传于 00:08:48 → 树检门（行 1605，199 行、
157 项 ok、0 FAIL）→ 编码门（行 1805，00:08:50）。门在产物之后，失败时仍留诊断物。

**S3 去 BOM 未损坏中文（用运行期取值而非编译通过来判）** 编译成功只能证明"没有编译错误"，
不能证明字面量正确——若 Roslyn 走错码页，Form 里的字面量与 `UiSmokeRunner.cs` 里的字面量会被
**同样**读错，断言照样通过；`ui-check.py` 比的是像素，也抓不到。故直接读 CI 331 产物的运行期
`text`（`utf-8-sig` 读，产物本身带 BOM）：

| 来源（本次去 BOM 的文件） | 控件 | 运行期取值 |
| --- | --- | --- |
| `Forms/PasswordHealthForm.cs` | `HealthScanStateLabel` | `上次扫描：10:42:22 · 2 个条目` |
| `Forms/ScannerCenterForm.cs` | `ScannerPluginEmptyHint` | `还没有插件：先放入脚本文件（下方提示有入口）` |
| `Forms/ScannerCenterForm.cs` | `ScannerFindingHeader` / `FindingEmptyHint` / `RawEmptyHint` | `发现（0）` / `还没有发现：…` `/ 还没有输出：…` |

两处中文逐字正确 ⇒ 去掉 BOM 没有影响 `csc` 对源码的解释，与 Roslyn `EncodedStringText`
（无 BOM 时严格按 UTF-8 解码）的读取路径一致。

**S4 驱动** 42 项 ALL OK（含 S2 牙齿证明与"补 BOM 后该条消失"的内容驱动证明）。
**S5 字节卫生** 19 个文件 diff 恰为 `1/1`，正文与 `HEAD` blob 逐字节相同；守卫自身无 BOM、仅 stdlib。
**S6 BOM 存量** `third_party/=70, ps1=2, fixtures/=2, lib/=1`，全部落在白名单内，越界 0。

### 诚实边界

1. **`tools/pack-release.ps1` 的 BOM 修复没有被 CI 跑到**：`appveyor.yml` 的 `after_test` 是内联打包，
   并不引用该脚本（全仓唯一引用处是 `docs/BUILD.md` 的发布指令），本地也没有 `pwsh`/`powershell`。
   因此这条**只有字节级证据、没有动态证据**，要闭环需在发布机上跑一次
   `powershell -File tools\pack-release.ps1`。反而是 CI 真正调用的 `tools/build-freerdp.ps1`（本就有 BOM）
   与 `tools/gen-version.ps1`（纯 ASCII、经 csproj `Exec` 调用）本次都实际执行且通过。
2. **编码门只校验"声明形状"**：它管不到编辑器是否照 `.editorconfig` 保存，也管不到运行时 IO 是否
   显式指定编码（`Encoding.UTF8` 会让 `File.WriteAllText` 写 BOM、让 `json.load` 需要 `utf-8-sig`）。
3. **`.ps1` 口径守卫比 `.editorconfig` 宽**（见实施偏离 2）：守卫允许任何 `.ps1` 带 BOM，
   故纯 ASCII 的 `gen-version.ps1` 保持无 BOM 不会被判错。
4. **白名单粒度是目录级**（见实施偏离 3）：`/fixtures/` 下目前只有 2 个 `.json` 带 BOM、无越界，
   但规则本身许可更宽。
