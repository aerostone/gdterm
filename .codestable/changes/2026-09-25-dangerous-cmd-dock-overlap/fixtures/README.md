# 夹具说明（2026-09-25-dangerous-cmd-dock-overlap）

三个文件都是**长期可回放**的，不依赖 CI 产物是否还在。

| 文件 | 性质 | 期望结果 |
|---|---|---|
| `dangerous-cmd-prefix.json` | CI 323 该窗 dump 的**原样冻结**（真实产物，非合成） | `ui-tree-check` 对它 **必须 FAIL 3**（停靠遮挡 36800 / 129600 / 6812 px²），exit 1 —— 这是**负向夹具** |
| `dock-sim.py` | 从三份真实 dump 反推的停靠模型 + 修复后几何预测 | `python3 dock-sim.py <dir>` **必须 exit 0**；自证失败即拒绝输出预测 |
| `check-dock-gate.py` | 本地驱动（本机无 dotnet，动态证据归 CI） | `python3 check-dock-gate.py` **必须 exit 0**，全部 ok |

注意：**直接对整个 `fixtures/` 目录跑 `ui-tree-check` 会得到 FAIL 3，这是设计如此**——`dangerous-cmd-prefix.json` 就是那条已知违例的快照，用来证明规则对它仍然红。修复后的几何由 `dock-sim.py` 生成到**另一个目录**再由 `check-dock-gate.py` 送检（生成目录与受检目录必须分开，否则负向夹具会把自己的 3 处失败混进来）。

`dock-sim.py` 的模型必须能逐值复现修复前 dangerous-cmd、修复前 scanner-center、修复后 scanner-center 三组真实几何——**自证不通过就拒绝给出预测**。这样"改装配顺序是否真的修好"从信念变成可被 CI 证伪的预测。
