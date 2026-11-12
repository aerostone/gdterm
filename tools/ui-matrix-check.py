#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""L2 矩阵门（docs/UI-UX-VERIFICATION.md §4.2）—— 判"同批 UI 在 DPI × 字号 × 主题下还成立吗"。

输入是 Gdterm.Tests --ui-matrix 产出的目录：

    ui-matrix/
      Dark-d100-f9/     {keepass-manager,keepass-unlock,port-forward,command-history}.json + combo.json
      Dark-d100-f14/    …
      Dark-d150-f9/     …
      …                 （主题 Dark/OLED × DPI 100/150 × 字号 9/14 = 8 格）

两件事：
  1. **复用 L1**：每个格子单独跑 tools/ui-tree-check.py（带 --dpi-factor=该格系数），
     规则一套不重写 —— 矩阵门加的是"换条件重跑"，不是"另一套标准"。
  2. **反空转**（这条是矩阵门存在的理由）：条件和几何必须有因果关系。
     若"字号 14pt"那格的总高与 9pt 一样、或"DPI 150%"那格与 100% 一样，
     说明条件没生效（缓存/被绕过/构造早于注入），此时全绿是假绿 —— 直接判失败。
     口径：跨格比同目标的控件高度总和，字号轴 ≥5%、DPI 轴 ≥10%（宽松下限，
     只用来抓"完全没变"，不用于精确度量）。

用法: python3 tools/ui-matrix-check.py <ui-matrixDir>
退出码 0=全过，1=有失败（任一格子的 L1 规则红、格子缺失、或反空转不成立）。
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TREE_CHECK = os.path.join(HERE, "ui-tree-check.py")

# 期望的矩阵形状（与 UiMatrixSmoke.cs 的三个轴一一对应）
EXPECT_THEMES = ("Dark", "OLED")
EXPECT_DPI = (1.0, 1.5)
EXPECT_FONT = (9.0, 14.0)
MIN_FONT_GROWTH = 1.05   # 14pt vs 9pt：控件总高至少 +5%
MIN_DPI_GROWTH = 1.10    # 150% vs 100%：控件总高至少 +10%

FAIL = []


def fail(msg):
    print("  FAIL: " + msg)
    FAIL.append(msg)


def ok(msg):
    print("  ok: " + msg)


def height_sum(path):
    """dump 里所有节点的 abs.h 之和（规模无关的几何指纹）。"""
    with open(path, encoding="utf-8-sig") as fp:
        tree = json.load(fp)
    total = 0
    stack = list(tree.get("controls") or [])
    while stack:
        n = stack.pop()
        a = n.get("abs") or {}
        h = a.get("h", 0)
        if isinstance(h, (int, float)) and h > 0:
            total += h
        stack.extend(n.get("children") or [])
    return total


def combos(root):
    """→ {(theme, dpi, font): dirName}"""
    out = {}
    for name in sorted(os.listdir(root)):
        d = os.path.join(root, name)
        if not os.path.isdir(d):
            continue
        cj = os.path.join(d, "combo.json")
        if not os.path.exists(cj):
            fail("格子缺 combo.json: " + name)
            continue
        with open(cj, encoding="utf-8-sig") as fp:
            meta = json.load(fp)
        out[(meta.get("theme"), float(meta.get("dpiFactor")),
             float(meta.get("uiFontSize")))] = d
    return out


def main():
    root = None
    for a in sys.argv[1:]:
        if not a.startswith("--"):
            root = a
            break
    root = root or "ui-matrix"
    print("=== ui-matrix-check on " + root + " ===")
    if not os.path.isdir(root):
        fail("矩阵目录不存在: " + root)
        return 1

    grid = combos(root)
    expected = {(t, d, f) for t in EXPECT_THEMES for d in EXPECT_DPI for f in EXPECT_FONT}
    missing = expected - set(grid)
    extra = set(grid) - expected
    if missing:
        fail("缺格子 %d 个: %s" % (len(missing), sorted(missing)))
    if extra:
        fail("多出未登记格子 %d 个: %s" % (len(extra), sorted(extra)))
    if not missing and not extra:
        ok("矩阵形状完整：%d 格（主题 %d × DPI %d × 字号 %d）" % (
            len(grid), len(EXPECT_THEMES), len(EXPECT_DPI), len(EXPECT_FONT)))

    # ── 1) 每格跑 L1 ──────────────────────────────────────────────
    targets = None
    for (theme, dpi, font), d in sorted(grid.items()):
        files = sorted(f for f in os.listdir(d) if f.endswith(".json") and f != "combo.json")
        names = [os.path.splitext(f)[0] for f in files]
        if targets is None:
            targets = set(names)
        if not files:
            fail("%s/%s-d%03d-f%02d 无 dump" % (root, theme, int(dpi * 100), int(font)))
            continue
        # 同一批目标必须在每格都出现（少了 = 某格子被静默跳过）
        absent = targets - set(names)
        if absent:
            fail("%s-d%03d-f%02d 缺目标 %s" % (theme, int(dpi * 100), int(font), sorted(absent)))
        cmd = [sys.executable, TREE_CHECK, d, "--dpi-factor=%s" % dpi]
        p = subprocess.run(cmd, capture_output=True, text=True)
        label = "%s-d%03d-f%02d" % (theme, int(dpi * 100), int(font))
        if p.returncode != 0:
            tail = [l for l in (p.stdout or "").splitlines() if "FAIL" in l][:8]
            fail(label + " L1 规则红 (%d 条):" % len(tail))
            for l in tail:
                print("      " + l.strip())
        else:
            ok(label + " L1 全过 (%d dumps)" % len(files))

    # ── 2) 反空转：条件必须真的改变了几何 ─────────────────────────
    def sum_of(theme, dpi, font, target):
        d = grid.get((theme, dpi, font))
        if not d:
            return None
        p = os.path.join(d, target + ".json")
        return height_sum(p) if os.path.exists(p) else None

    for target in sorted(targets or []):
        for theme in EXPECT_THEMES:
            for dpi in EXPECT_DPI:
                a, b = sum_of(theme, dpi, 9.0, target), sum_of(theme, dpi, 14.0, target)
                if a and b:
                    r = float(b) / a
                    if r < MIN_FONT_GROWTH:
                        fail("字号轴没生效 %s/%s d%03d: 14pt 总高/9pt=%0.3f (< %0.2f)"
                             % (theme, target, int(dpi * 100), r, MIN_FONT_GROWTH))
                    else:
                        ok("字号轴生效 %s/%s d%03d: 14pt/9pt 高度比=%0.2f" % (theme, target, int(dpi * 100), r))
            for font in EXPECT_FONT:
                a, b = sum_of(theme, 1.0, font, target), sum_of(theme, 1.5, font, target)
                if a and b:
                    r = float(b) / a
                    if r < MIN_DPI_GROWTH:
                        fail("DPI 轴没生效 %s/%s f%02d: 150%% 总高/100%%=%0.3f (< %0.2f)"
                             % (theme, target, int(font), r, MIN_DPI_GROWTH))
                    else:
                        ok("DPI 轴生效 %s/%s f%02d: 150%%/100%% 高度比=%0.2f"
                           % (theme, target, int(font), r))

    print()
    if FAIL:
        print("UI-MATRIX-CHECK FAIL: %d" % len(FAIL))
        return 1
    print("UI-MATRIX-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
