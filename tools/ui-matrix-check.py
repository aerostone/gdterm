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
     若"字号 14pt"那格与 9pt 一样、或"DPI 150%"那格与 100% 一样，
     说明条件没生效（缓存/被绕过/构造早于注入），此时全绿是假绿 —— 直接判失败。

     口径（2026-11-13 修正）：**按控件身份（类型/Name/Text）配对后比高度**，
     取全部可比控件里宽/高两维的最大比值。原先比"整棵树的 abs.h 总和"，对 Dock=Fill 的页面
     完全失效——宿主高度由窗口定死（480 → 480，比值恒 1.0），命令历史页因此假红；
     而真正缩放的控件（Select/Pagination/Input，实测恒 1.50）被它摊平了。
     阈值取宽松下限（DPI ≥1.35、字号 ≥1.15），只用于抓"完全没变"，不做精确度量。

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
MIN_FONT_GROWTH = 1.15   # 14pt vs 9pt：同身份控件最大高度比（实测 1.41~1.53）
MIN_DPI_GROWTH = 1.35    # 150% vs 100%：同身份控件最大高度比（实测 1.50~2.71）

FAIL = []


def fail(msg):
    print("  FAIL: " + msg)
    FAIL.append(msg)


def ok(msg):
    print("  ok: " + msg)


def heights_by_identity(path):
    """→ {(短类型, Name, Text前12): (abs.w, abs.h)}，只留宽高都 >0 的节点。

    键里带 Text 是因为同一页常有一堆同名/匿名控件（Button "刷新"、"删除"…），
    只按类型配对会把不同控件比到一起。
    """
    with open(path, encoding="utf-8-sig") as fp:
        tree = json.load(fp)
    out = {}
    stack = list(tree.get("controls") or [])
    while stack:
        n = stack.pop()
        a = n.get("abs") or {}
        h = a.get("h", 0)
        w = a.get("w", 0)
        if isinstance(h, (int, float)) and h > 0 and isinstance(w, (int, float)) and w > 0:
            key = (str(n.get("type") or "").split(".")[-1],
                   n.get("name") or "",
                   (n.get("text") or "")[:12])
            out[key] = (w, h)
        stack.extend(n.get("children") or [])
    return out


def max_growth(a, b):
    """同身份控件里最大的单维放大比 → (比值, 键)；无可比控件时 (None, None)。

    口径（2026-11-07 修正）：取宽/高两个维度里**较大**的那个比值，而不是只看高度。
    只看高度会把"高度被 38px 输入框地板吃住、但字宽明显变大"的页面判成条件没生效
    （health-monitor f9→f14：按钮 81→115px 宽、高度恒 38px，最大高度比 1.00 假红）。
    字宽同样是字体生效的直接证据，比"总高"更难被固定地板摊平。
    """
    best, best_key = None, None
    for k, (wa, ha) in a.items():
        pair = b.get(k)
        if not pair or not ha or not wa:
            continue
        wb, hb = pair
        r = max(float(hb) / float(ha), float(wb) / float(wa))
        if best is None or r > best:
            best, best_key = r, k
    return best, best_key


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
    def heights_of(theme, dpi, font, target):
        d = grid.get((theme, dpi, font))
        if not d:
            return None
        p = os.path.join(d, target + ".json")
        return heights_by_identity(p) if os.path.exists(p) else None

    for target in sorted(targets or []):
        for theme in EXPECT_THEMES:
            # 字号轴：同 DPI 下 14pt vs 9pt
            for dpi in EXPECT_DPI:
                a = heights_of(theme, dpi, 9.0, target)
                b = heights_of(theme, dpi, 14.0, target)
                if a is None or b is None:
                    continue
                r, k = max_growth(a, b)
                if r is None:
                    fail("字号轴无法验证 %s/%s d%03d: 两格没有可比控件" % (theme, target, int(dpi * 100)))
                elif r < MIN_FONT_GROWTH:
                    fail("字号轴没生效 %s/%s d%03d: 14pt/9pt 最大尺寸比=%0.2f (< %0.2f)"
                         % (theme, target, int(dpi * 100), r, MIN_FONT_GROWTH))
                else:
                    ok("字号轴生效 %s/%s d%03d: 14pt/9pt 最大尺寸比=%0.2f（%s %s）"
                       % (theme, target, int(dpi * 100), r, k[0], (k[1] or k[2] or "-")))
            # DPI 轴：同字号下 150% vs 100%
            for font in EXPECT_FONT:
                a = heights_of(theme, 1.0, font, target)
                b = heights_of(theme, 1.5, font, target)
                if a is None or b is None:
                    continue
                r, k = max_growth(a, b)
                if r is None:
                    fail("DPI 轴无法验证 %s/%s f%02d: 两格没有可比控件" % (theme, target, int(font)))
                elif r < MIN_DPI_GROWTH:
                    fail("DPI 轴没生效 %s/%s f%02d: 150%%/100%% 最大尺寸比=%0.2f (< %0.2f)"
                         % (theme, target, int(font), r, MIN_DPI_GROWTH))
                else:
                    ok("DPI 轴生效 %s/%s f%02d: 150%%/100%% 最大尺寸比=%0.2f（%s %s）"
                       % (theme, target, int(font), r, k[0], (k[1] or k[2] or "-")))

    print()
    if FAIL:
        print("UI-MATRIX-CHECK FAIL: %d" % len(FAIL))
        return 1
    print("UI-MATRIX-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
