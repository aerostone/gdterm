#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""L2 矩阵门（docs/UI-UX-VERIFICATION.md §4.2）—— 判"同批 UI 在 DPI × 字号 × 主题下还成立吗"。

输入是 Gdterm.Tests --ui-matrix 产出的目录：

    ui-matrix/
      targets.json                目标清单（名字/分组/形态），由冒烟写出
      Dark-d100-f9/               几何轴基准格 + 主题轴基准格
      Dark-d100-f14/              字号轴上格
      Dark-d150-f9/               DPI 轴上格
      Dark-d150-f14/              两轴交叉格
      OLED-d100-f9/               主题轴（OLED）
      Darker-d100-f9/             主题轴（Darker，以前完全没进过矩阵）
      每格 = {目标名}.json + {目标名}.png + combo.json

四件事：
  1. **复用 L1**：每个格子单独跑 tools/ui-tree-check.py（带 --dpi-factor=该格系数），
     规则一套不重写 —— 矩阵门加的是"换条件重跑"，不是"另一套标准"。
  2. **覆盖度棘轮**（tools/ui-matrix-ratchet.txt）：目标清单是提交物。
     冒烟新增/删除目标却不改棘轮 → 红；棘轮里的目标某格没产出 → 红。
     这样"扩了一页但只在低 DPI 验过"无法悄悄发生。
  3. **形态多样性**：targets.json 声明每页覆盖的控件形态，requiredShapes 必须都被覆盖。
     否则 38 页全是表格也算"覆盖完整"。
  4. **反空转**：条件与几何/色必须有因果关系。
     字号 14pt 那格与 9pt 一样、DPI 150% 与 100% 一样、OLED/Darker 与 Dark 同色，
     都说明条件没生效 —— 此时全绿是假绿，直接判失败。

     几何口径（2026-11-13）：按控件身份（类型/Name/Text）配对后比宽高最大值。
     比"整棵树总高"对 Dock=Fill 页面完全失效（宿主高度由窗口定死），
     会既假红（命令历史页）又摊平真实缩放（Select/Input 恒 1.50）。
     阈值取宽松下限（DPI ≥1.35、字号 ≥1.15），只抓"完全没变"。

用法: python3 tools/ui-matrix-check.py <ui-matrixDir>
退出码 0=全过，1=有失败。
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TREE_CHECK = os.path.join(HERE, "ui-tree-check.py")
RATCHET = os.path.join(HERE, "ui-matrix-ratchet.txt")

# 期望的格子形状（与 UiMatrixSmoke.cs 的轴定义一一对应）
EXPECT_COMBOS = (
    ("Dark", 1.0, 9.0),     # 基准
    ("Dark", 1.0, 14.0),    # 字号轴上格
    ("Dark", 1.5, 9.0),     # DPI 轴上格
    ("Dark", 1.5, 14.0),    # 两轴交叉
    ("OLED", 1.0, 9.0),     # 主题轴
    ("Darker", 1.0, 9.0),   # 主题轴（新增）
)
BASE = ("Dark", 1.0, 9.0)
MIN_FONT_GROWTH = 1.15   # 14pt vs 9pt：同身份控件最大尺寸比（实测 1.41~1.53）
MIN_DPI_GROWTH = 1.35    # 150% vs 100%：同身份控件最大尺寸比（实测 1.50~2.71）

FAIL = []


def fail(msg):
    print("  FAIL: " + msg)
    FAIL.append(msg)


def ok(msg):
    print("  ok: " + msg)


def label(combo):
    theme, dpi, font = combo
    return "%s-d%03d-f%02d" % (theme, int(round(dpi * 100)), int(font))


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

    取宽/高两维里**较大**的比值：只看高度会把"高度被 38px 输入框地板吃住、
    但字宽明显变大"的页面判成条件没生效（health-monitor f9→f14 曾因此假红）。
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
        out[(meta.get("theme"), round(float(meta.get("dpiFactor")), 2),
             round(float(meta.get("uiFontSize")), 2))] = d
    return out


def load_ratchet(path=None):
    path = path or RATCHET
    if not os.path.exists(path):
        fail("覆盖度棘轮缺失: " + path)
        return set()
    names = set()
    with open(path, encoding="utf-8-sig") as fp:
        for line in fp:
            line = line.strip()
            if line and not line.startswith("#"):
                names.add(line)
    return names


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
    expected = {(t, d, f) for (t, d, f) in EXPECT_COMBOS}
    missing = expected - set(grid)
    extra = set(grid) - expected
    if missing:
        fail("缺格子 %d 个: %s" % (len(missing), sorted(missing)))
    if extra:
        fail("多出未登记格子 %d 个: %s" % (len(extra), sorted(extra)))
    if not missing and not extra:
        ok("矩阵形状完整：%d 格（几何轴 4 + 主题轴 2）" % len(grid))

    # ── 0) 目标清单 + 棘轮 + 形态多样性 ───────────────────────────
    tj = os.path.join(root, "targets.json")
    declared = {}
    required_shapes = []
    if not os.path.exists(tj):
        fail("缺 targets.json（冒烟没写出目标清单）")
    else:
        with open(tj, encoding="utf-8-sig") as fp:
            meta = json.load(fp)
        for t in meta.get("targets") or []:
            declared[t.get("name")] = t
        required_shapes = meta.get("requiredShapes") or []
        ok("目标清单：%d 页（%d 侧栏 / %d 对话框）" % (
            len(declared),
            sum(1 for t in declared.values() if t.get("group") == "panel"),
            sum(1 for t in declared.values() if t.get("group") == "dialog")))

    rp = None
    for a in sys.argv[1:]:
        if a.startswith("--ratchet="):
            rp = a.split("=", 1)[1]
    ratchet = load_ratchet(rp)
    if ratchet:
        absent = sorted(ratchet - set(declared))
        if absent:
            fail("棘轮登记但冒烟没产出 %d 页: %s" % (len(absent), absent))
        new = sorted(set(declared) - ratchet)
        if new:
            fail("冒烟新增/改名 %d 页但没登记棘轮（tools/ui-matrix-ratchet.txt）: %s"
                 % (len(new), new))
        if not absent and not new:
            ok("覆盖度棘轮对齐：%d 页（扩页必须同时改棘轮）" % len(ratchet))

    if required_shapes:
        shapes = " | ".join(t.get("shape") or "" for t in declared.values())
        lacking = [s for s in required_shapes if s not in shapes]
        if lacking:
            fail("形态覆盖不足，缺: %s（全部形态: %s）" % (lacking, shapes))
        else:
            ok("形态覆盖齐全：%s" % ", ".join(required_shapes))

    # ── 1) 每格跑 L1 ──────────────────────────────────────────────
    targets = None
    for combo in sorted(grid):
        d = grid[combo]
        files = sorted(f for f in os.listdir(d) if f.endswith(".json") and f != "combo.json")
        names = [os.path.splitext(f)[0] for f in files]
        if targets is None:
            targets = set(names)
        if not files:
            fail("%s 无 dump" % label(combo))
            continue
        absent = targets - set(names)
        if absent:
            fail("%s 缺目标 %s" % (label(combo), sorted(absent)))
        # 截图与 dump 必须成对（R8 白块只能判有图的页面）
        pngs = {os.path.splitext(f)[0] for f in os.listdir(d) if f.endswith(".png")}
        no_png = sorted(set(names) - pngs)
        if no_png:
            fail("%s 有 dump 没截图 %d 页: %s" % (label(combo), len(no_png), no_png[:6]))
        cmd = [sys.executable, TREE_CHECK, d, "--dpi-factor=%s" % combo[1]]
        p = subprocess.run(cmd, capture_output=True, text=True)
        if p.returncode != 0:
            tail = [l for l in (p.stdout or "").splitlines() if "FAIL" in l][:8]
            fail(label(combo) + " L1 规则红 (%d 条):" % len(tail))
            for l in tail:
                print("      " + l.strip())
        else:
            ok(label(combo) + " L1 全过 (%d dumps)" % len(files))

    # ── 2) 反空转：字号轴 / DPI 轴 ────────────────────────────────
    def heights_of(combo, target):
        d = grid.get(combo)
        if not d:
            return None
        p = os.path.join(d, target + ".json")
        return heights_by_identity(p) if os.path.exists(p) else None

    font_ok = dpi_ok = 0
    for target in sorted(targets or []):
        for dpi in (1.0, 1.5):
            a = heights_of(("Dark", dpi, 9.0), target)
            b = heights_of(("Dark", dpi, 14.0), target)
            if a is None or b is None:
                continue
            r, k = max_growth(a, b)
            if r is None:
                fail("字号轴无法验证 %s d%03d: 两格没有可比控件" % (target, int(dpi * 100)))
            elif r < MIN_FONT_GROWTH:
                fail("字号轴没生效 %s d%03d: 14pt/9pt 最大尺寸比=%0.2f (< %0.2f)"
                     % (target, int(dpi * 100), r, MIN_FONT_GROWTH))
            else:
                font_ok += 1
        for font in (9.0, 14.0):
            a = heights_of(("Dark", 1.0, font), target)
            b = heights_of(("Dark", 1.5, font), target)
            if a is None or b is None:
                continue
            r, k = max_growth(a, b)
            if r is None:
                fail("DPI 轴无法验证 %s f%02d: 两格没有可比控件" % (target, int(font)))
            elif r < MIN_DPI_GROWTH:
                fail("DPI 轴没生效 %s f%02d: 150%%/100%% 最大尺寸比=%0.2f (< %0.2f)"
                     % (target, int(font), r, MIN_DPI_GROWTH))
            else:
                dpi_ok += 1
    if targets:
        ok("字号轴生效 %d/%d 点，DPI 轴生效 %d/%d 点" % (
            font_ok, len(targets) * 2, dpi_ok, len(targets) * 2))

    # ── 3) 主题轴反空转 ─────────────────────────────────────────
    # 字号/DPI 两轴比几何；主题轴对几何几乎无影响，它影响的是色。三件事：
    #   a) 每格自报的调色板背景（combo.json back）必须彼此不同 —— 否则主题就没切过去；
    #   b) 换主题后，**至少多数页**的控件色集合发生了变化 —— 否则页面不吃主题（写字面色）；
    #   c) 报出"不吃主题"的页面清单，作为存量可见（不红：有的对话框本来就走系统底色）。
    self_back = {}
    for combo, d in grid.items():
        cj = os.path.join(d, "combo.json")
        try:
            with open(cj, encoding="utf-8-sig") as fp:
                self_back[combo] = (json.load(fp) or {}).get("back")
        except Exception:
            self_back[combo] = None
    # 同一主题的多个几何格背景必然相同 —— 要判的是"不同主题之间有没有真的换色"。
    per_theme = {}
    for combo, b in self_back.items():
        per_theme.setdefault(combo[0], set()).add(b)
    multi = {t: v for t, v in per_theme.items() if len(v) > 1}
    if multi:
        fail("同主题内背景色不一致（主题切换有粘滞/漏刷）: %s" % multi)
    if len(set(frozenset(v) for v in per_theme.values())) < len(per_theme):
        clash = {}
        for t, v in per_theme.items():
            clash.setdefault(tuple(sorted(v)), []).append(t)
        dup = [ts for ts in clash.values() if len(ts) > 1]
        fail("主题轴没切换：这些主题自报同一套调色板背景 %s（combo.json back）" % dup)
    else:
        ok("主题轴调色板已切换：%s" % " / ".join(
            "%s=%s" % (t, sorted(v)[0]) for t, v in sorted(per_theme.items())))

    def colors_of(combo, target):
        d = grid.get(combo)
        if not d:
            return None
        p = os.path.join(d, target + ".json")
        if not os.path.exists(p):
            return None
        with open(p, encoding="utf-8-sig") as fp:
            tree = json.load(fp)
        out, stack = set(), list(tree.get("controls") or [])
        while stack:
            n = stack.pop()
            for k in ("back", "fore"):
                v = n.get(k)
                if v:
                    out.add(v)
            stack.extend(n.get("children") or [])
        return out

    base_dir = grid.get(BASE)
    for theme in ("OLED", "Darker"):
        other = grid.get((theme, 1.0, 9.0))
        if not base_dir or not other:
            continue
        moved, inert, checked = [], [], 0
        for target in sorted(targets or []):
            a = colors_of(BASE, target)
            b = colors_of((theme, 1.0, 9.0), target)
            if a is None or b is None:
                continue
            checked += 1
            (moved if a != b else inert).append(target)
        if not checked:
            fail("主题轴无法验证 %s：没有可比页面" % theme)
            continue
        if len(moved) * 2 < checked:
            fail("主题轴没生效 %s：只有 %d/%d 页的色集合变化（阈值过半）"
                 % (theme, len(moved), checked))
        else:
            ok("主题轴生效 %s：%d/%d 页色集合随主题变化" % (theme, len(moved), checked))
        if inert:
            print("  info: %s 下色集合未变的页面 %d 个: %s"
                  % (theme, len(inert), ", ".join(inert[:8])))

    print()
    if FAIL:
        print("UI-MATRIX-CHECK FAIL: %d" % len(FAIL))
        return 1
    print("UI-MATRIX-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
