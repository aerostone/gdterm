---
doc_type: change
kind: issue
slug: 2026-09-25-ci-tree-gate
status: in-progress
phase: analyzed
mode: standard
summary: 把已存在但从不执行的全控件树校验器接进 CI，让"停靠遮挡/触击目标/焦点链/关闭绑定"四类缺陷不再假绿
tags: [ci, gate, ui, layout]
risk:
  level: medium
  reasons:
    - 改动的是构建基础设施（appveyor.yml），失败会让所有后续提交变红
    - 编辑 appveyor.yml 会使 freerdp-bin 缓存键失效，下一次构建需重编 FreeRDP（更慢、更易抖动）
    - 门一旦接上，既有形式缺陷会阻塞主线（已用全量实测确认当前为零命中，故不会立刻爆）
contract:
  include:
    - appveyor.yml
    - tools/ui-tree-check.py
    - .codestable/changes/2026-09-25-ci-tree-gate/**
  exclude: []
  preexisting_changes: [.codestable/.runtime/current-package]
  baseline:
    git_head: 70901de5d277e4df9b4441e0b5b9d1e3f21c4f4a
    dirty_hashes:
      .codestable/.runtime/current-package: e0bb617bb50bdece920a358b7e16367869ce892e09e1a61b18968ca85f7cf569
  architecture_impact: unchanged
  architecture_reason: 只给流水线增加一个校验步骤并清除校验器里的一个编码隐患，不改运行时代码、模块边界或依赖方向
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 不新增或改变用户可见能力，只是让既有约束在 CI 里真正生效
  context_refs:
    impl:
      - tools/ui-tree-check.py
      - appveyor.yml
    design:
      - .codestable/changes/2026-09-25-dangerous-cmd-dock-overlap/change.md
    accept: []
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-ci-tree-gate/change.md
      depends_on: []
  evidence_ledger: false
model_route:
  strategy: retain-high
  reason: 只有 2 步、全是低风险改动，但"门挂在哪个阶段、python 缺失时怎么办"是需要判断的取舍点
---

# 把控件树校验器接进 CI

## 目标与边界

### 目标

`tools/ui-tree-check.py` 已实现 R1-R5 五类结构缺陷检测（触击目标、焦点链、关闭绑定、键盘可达性、停靠遮挡），在 CI 323/327 的真实产物上全绿；但 **AppVeyor 流水线里没有任何一行调用它**。结果是：这个校验器只在"有人手工下载产物并手动执行"时才生效——而"停靠遮挡"这类缺陷恰恰在 CI 里静默通过了多轮（dangerous-cmd 的 3 处遮挡从引入到被修，一路绿灯）。

本包把它接成**真正的门**，并顺手清掉它自身的一个编码隐患，使"结构缺陷 → CI 红"这条链路端到端成立。

### 边界（明确不做）

- **不改** `tools/ui-check.py`（截图启发式）。它硬依赖 Pillow + numpy，阈值（方差/纯白占比）脆，且只覆盖 3 个固定文件名；保持"审查者手动跑"的定位。
- **不做** `keepass-manager-empty.png` 的像素断言。该图目前无任何校验器覆盖，属于**已知残余缺口**，登记而不在本包修（它需要 Pillow，与"CI 只跑零依赖校验器"的口径冲突）。
- **不碰**任何 `src/` 下代码。本包若让业务代码也变了，"门是否真的生效"就无法归因。
- **不新增**第三方依赖。

### 成功标准

1. CI 在一次真实构建里执行到树检，且日志里能看到它的输出；构建结果为 success。
2. 该步骤**在产物上传之后**执行——门即使红了，PNG/JSON 仍然留在产物里可诊断。
3. python 解释器在 AppVeyor 镜像上找不到时**显式失败**（大声红），而不是静默跳过（静默跳过等于没接门）。
4. `tools/ui-tree-check.py` 不再含任何非 GBK 可编码字符（当前有 4 个 `²`），消除 cp936 管道下的 `UnicodeEncodeError` 风险。

## 行为增量

**ADDED**

- `appveyor.yml` 的 `test_script` 末尾新增一个 python 树检步骤：解析解释器 → 设 `PYTHONIOENCODING=utf-8` / `PYTHONUTF8=1` → 跑 `tools/ui-tree-check.py ui-smoke` → 非零退出即 `throw`。
- `tools/ui-tree-check.py` 输出中的 `²` 全部改为 ASCII `px2`。

**MODIFIED**

- 无。`test_script` 既有步骤（单测 → UI 冒烟 → 上传产物）顺序与语义不变，新步骤**追加在最后**。

**REMOVED**

- 无。

## 设计与契约

### D1 门挂在 `test_script` 末尾、产物上传之后

**选择**：放在 `Get-ChildItem ... Push-AppveyorArtifact` 两行之后。

**理由**：AppVeyor 的 `after_test` 才做打包，`test_script` 里抛异常会中断后续。若把门放在上传之前，一旦门红，正好在最需要看截图/dump 的时候把产物全丢了——诊断成本反而最高。**先留证据、再宣判**。

**否决的方案**：放在 `after_test` 里。那样打包逻辑会被牵连进"门失败"的语义里，且 `after_test` 已在做 ILRepack 与产物发布，混入校验会让失败归因变模糊。

### D2 解释器解析：`Get-Command python` → `py -3` 回退 → 都没有就 throw

**选择**：

```
$py = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $py) { $py = (Get-Command py -ErrorAction SilentlyContinue).Source; $pyArgs = @("-3") }
if (-not $py) { throw "python not found — 树检门无法执行（拒绝静默跳过）" }
```

**理由**：这是本包唯一真正需要判断的取舍点。三种候选行为里：

| 方案 | 后果 | 判定 |
|---|---|---|
| 找不到 python 就 `Write-Host` 跳过 | 门永远可能"悄悄不存在"；半年后没人知道它是否还在跑 | 否决——这正是本包要消灭的失败模式 |
| 找不到就 `throw` | 构建红，人被迫处理 | **采用** |
| 内置纯 PowerShell 重写校验器 | 零依赖，但要维护两套实现 | 否决——两套实现必然漂移，且丢掉已用真实产物验证过的口径 |

VS2022 镜像自带 python，故 `throw` 分支在实践中不会触发；它存在的意义是把"配置漂移"从静默故障变成响亮的红。

### D3 `²` → `px2`：不靠设环境变量掩盖，而是把字符本身去掉

**理由**：`PYTHONIOENCODING=utf-8` 只保护**本步骤**。任何人在 cp936 控制台上手工跑 `python tools/ui-tree-check.py <dir>` 仍会撞 `UnicodeEncodeError`——而这恰恰是本仓反复踩过的坑（CI 控制台已把中文断言名压成 `?`，日志检索因此失效）。与其在调用方到处设编码，不如让工具本身 ASCII 安全。输出里的 `px2` 与 `px²` 信息量相同。

**注**：中文断言名保留不动（它对本仓审查者可读性重要），GBK 无法编码的**符号**才清掉。

### D4 不改截图校验器，但把缺口写下来

`ui-check.py` 需要 Pillow + numpy、阈值脆、只覆盖 3 个文件名，且 `keepass-manager-empty.png`（本仓新增的空态截图）不在其列。本包**不动它**，只在本文件与后续 note 中登记该缺口，避免"以为有覆盖其实没有"。

## 名词与编排

### 名词层

| 名词 | 含义 |
|---|---|
| 树检门 | `appveyor.yml` 中调用 `tools/ui-tree-check.py` 的步骤 |
| R1-R5 | 校验器的五条规则：触击目标 / 焦点链 / 关闭绑定 / 键盘可达性 / 停靠遮挡 |
| 零依赖 | 只用 stdlib `json`/`os`/`sys`，不需要 pip 安装任何东西 |

### 编排层

```
单测(Gdterm.Tests.exe)
   → UI 冒烟(--ui-smoke, 写 PNG/JSON 到 ui-smoke/)
      → 上传 PNG/JSON 产物            ← 证据先落地
         → 树检门(tools/ui-tree-check.py ui-smoke)   ← 本包新增，红了会中断
            → after_test: 打包 + 发布
```

**挂载点清单**

| 位置 | 动作 |
|---|---|
| `appveyor.yml` `test_script` 末尾 | 新增 D2 的解释器解析 + 树检调用 + 非零 throw |
| `tools/ui-tree-check.py` | 4 处 `²` → `px2` |

## 验收契约

| 编号 | 内容 | 判定 |
|---|---|---|
| S1 | 树检门在一次真实构建里被执行 | CI 日志含校验器输出（`UI-TREE-CHECK`）且构建 success |
| S2 | 门有牙齿 | `ui-tree-check.py` 对已知缺陷夹具 exit 1（已在 2026-09-25-dangerous-cmd-dock-overlap 的 S1 中证实：修复前真实产物报 3 处遮挡 exit 1）；PowerShell 侧 `if ($LASTEXITCODE -ne 0) { throw }` 与已生效的单测/冒烟步骤同一机制 |
| S3 | 证据先落地 | 步骤位于两行 `Push-AppveyorArtifact` 之后 |
| S4 | 不静默跳过 | 解释器缺失分支为 `throw` 而非 `Write-Host` 跳过 |
| S5 | ASCII 安全 | `tools/ui-tree-check.py` 全文可用 GBK 编码（4 个 `²` → 0） |
| S6 | 反向检查 | 校验器仍零依赖（仅 stdlib）；`tools/ui-check.py` 未被改动；`src/` 下无改动 |

## 执行计划

| Step | 动作 | 验证 |
|---|---|---|
| 1 | `tools/ui-tree-check.py`：`²` → `px2` | 本地断言"全文可 GBK 编码"由红转绿；对 CI 327 全部 dump 仍 `ALL OK` |
| 2 | `appveyor.yml`：`test_script` 末尾追加树检门 | 本地无 PowerShell，无法就地跑红；由下一次真实构建（CI）仲裁 |
| 3 | 收敛：`--phase accept --converge` + 更新指针 | 合规检查通过；CI 真实构建 success |

## 执行证据

**状态口径**：kind `issue` 的 `state_order` 只允许 `in-progress: analyzed`（无 `impl` 相位词），实现动作按执行计划两步完成，故此处 `phase: analyzed`。

**环境约束**：本机（Linux）无 dotnet、无 PowerShell，`appveyor.yml` 的语法有效性与"门是否真的被执行"无法在本地判定，**只能由下一次真实构建仲裁**；本地完成的是可判定的静态与口径部分。

**Step 1｜`²` → `px2`**

- 命令：以 utf-8 读原文（前置断言无 BOM）→ 断言 `²` 恰好 4 处 → 替换 → utf-8 写回 → `ast.parse` 复检
- 证据：4 处替换、`AST OK`、BOM=False；`check-ci-gate.py` 的 S1 由 1 处失败转 0 处（`无非 GBK 字符  0 个`）
- 牙齿：S1 断言用探针 `面积 100px²` 复检同一函数，回报 `{'²': 1}`（非空），证明该检查不是恒真

**Step 2｜`appveyor.yml` 追加树检门**

- 位置证据（终态行号）：产物上传 53/54 行 → `Get-Command python` 59 → `py -3` 回退 62 → `python not found ... throw` 65 → `PYTHONIOENCODING`/`PYTHONUTF8` 66/67 → 树检调用 69 → 非零 `throw` 70；`after_test:` 在 71（门在其之前、打包段之外）
- 编码证据：`appveyor.yml` 无 BOM、LF 换行、纯 ASCII（不含 `²`，故自身无 cp936 隐患）
- 牙齿：把门整块临时挪到产物上传**之前**，驱动立即报 `FAIL: 门在产物上传之后（门红了仍留证据可诊断）  上传行=68 门行=66`、`CI-GATE-CHECK FAIL 1`；还原后 `cmp` 逐字节一致且复跑 `ALL OK`

**Step 3｜口径未变**

- 改字符后对 CI 327 的**全部 19 份 dump** 复跑：`UI-TREE-CHECK ALL OK`，exit 0（S2）

**本地驱动**：`.codestable/changes/2026-09-25-ci-tree-gate/fixtures/check-ci-gate.py`，`CI-GATE-CHECK ALL OK`，覆盖 S1 编码安全 3 项 + S2 口径不变 2 项 + S3 门结构 8 项 + S4 字节卫生/零依赖/未越界 7 项。

**反向检查**

- 改动范围：`git diff --name-only HEAD` = `appveyor.yml`、`tools/ui-tree-check.py`、`.codestable/.runtime/current-package`（受控）
- `src/` 下零改动；`tools/ui-check.py` 零改动；校验器 import 仍为 `json,os,sys`（零第三方依赖）

**实施偏离**

1. 包内置的 `tool-config.json` 计划未执行：校验器当前**硬编码** `ui-smoke/` 下的 dump 清单，无目录参数化机制；驱动改为**从该硬编码清单派生** dump 计数与逐文件判定，避免引入第二份清单造成漂移。该参数化登记为后续改进项。
2. 包的 `contract.artifacts` 未按 reference 的 `{id,path,depends_on}` 形态声明，合规检查因此报 `artifacts.graph` 失败，已按 `.codestable/reference/change-package.md` 归位（`contract` 之下、顶层 `model_route.strategy`）。
3. D4 原计划"登记 `keepass-manager-empty.png` 无覆盖"，实际确认 `tool-config.json` 机制根本不存在，缺口登记口径随之改为"截图校验器不参数化且未覆盖该图"。

**未纳入本包（登记待办）**

- `tools/ui-check.py` 参数化 + `keepass-manager-empty.png` 覆盖：其 Pillow/numpy 依赖与"CI 只跑零依赖校验器"的口径冲突
- `appveyor.yml` 编辑会使 `freerdp-bin` 缓存键失效，下一次构建需重编 FreeRDP，构建时长与抖动概率上升（本包已接受此代价）

## 验收结果

（accept 阶段追加）
