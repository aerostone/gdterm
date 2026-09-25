# 夹具说明（2026-09-25-dangerous-cmd-dock-overlap）

四份文件都是**长期可回放**的，不依赖 CI 产物是否还在。

| 文件 | 性质 | 期望结果 |
|---|---|---|
| `dangerous-cmd-prefix.json` | **CI 323** 该窗 dump 原样冻结（真实产物） | `ui-tree-check` 对它 **必须 FAIL 3**（36800 / 129600 / 6812 px²），exit 1 |
| `dangerous-cmd-fixed.json` | **CI 327** 该窗 dump 原样冻结（真实产物） | 对它 **必须 `ALL OK`**，exit 0，`停靠无遮挡=0` |
| `dock-sim.py` | 从三份真实 dump 反推的停靠模型 + 修复后几何预测 | `python3 dock-sim.py <dir>` **必须 exit 0**；自证失败即拒绝输出预测 |
| `check-dock-gate.py` | 本地驱动（本机无 dotnet，动态证据归 CI） | `python3 check-dock-gate.py` **必须 exit 0** |

一对**真实**产物构成红绿：同一口径在 `prefix` 上红、在 `fixed` 上绿，因此这不是"合成夹具自证"，而是规则口径在真实数据上的可分性证明。

两个坑（都已在驱动里处理）：

1. **两份夹具必须分开送检**——放同一目录时，负向夹具的 3 处失败会混进正向结果，让"修复后是否真的绿"无法判断。驱动用两个独立临时目录分别送检。
2. **生成目录与受检目录必须分开**——`dock-sim.py` 会把预测几何写成 dump 文件；若直接指向本目录，就会在这里留下随模型改动而陈旧的合成物。驱动把 SIM 的输出一律指向临时目录，并反向断言本目录只含那两份真实产物。

`dock-sim.py` 的模型必须逐值复现 CI 323 修复前 dangerous-cmd、CI 321 修复前 scanner-center、CI 323 修复后 scanner-center 三组真实几何——**自证不通过就拒绝给出预测**。CI 327 实测 4/4 值与其预测逐像素一致。
