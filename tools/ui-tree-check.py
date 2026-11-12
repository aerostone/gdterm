#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""全控件树盒模型断言：读 CI 产出的 *.json（UiTreeDumper），查：

R1 触击目标：可见且 enabled 的交互叶，abs 宽高二者同时小于 32 逻辑 px 才算失败
   （曾评估 either-dim，会立刻命中主窗 10 个 109-118x25 合法紧凑控件，故否决）；
   Label/Divider/Panel/Splitter 等展示型控件不参与，省略号溢出钮豁免。
   量纲（2026-11-01 复核修正）：app.manifest 是 PerMonitorV2，dump 的 abs 是设备像素，
   阈值按 T×dpi/96 换算（96dpi 下恒等，高 dpi 需要更多设备像素）。
R2（已删）：原“同父非零 TabIndex 不重复”规则恒过（全仓显式 TabIndex 仅 1 处，其余皆 0）——
   永不失败的规则比没有规则更坏（审计 C7-④），按“补真实 Tab 序或删除”的决策删除；
   焦点链覆盖由 R4（交互叶 TabStop=false 即红）承接。
R3 关闭绑定：每窗至少一个名含 Close/Cancel/OK/确定/取消/关闭 的控件；无关闭语义的窗体走前缀豁免。
R4 键盘可达性：交互叶 TabStop=false 数必须为 0（精确短名匹配，见该段注释）；mainform 走独立分支。
R6 文本适配（2026-10-07 审计 C7-1）：dump 自带 textSingleW/textWrapH/wrap，
   单行控件所需宽 > 可用宽+TOL 记为“文字溢出”；换行控件所需高 > 可用高+TOL 记为“文字裁切”。
   默认 **warn-only**（打印 WARN 不影响退出码）：AntdUI.Button 有意省略号截断、以及
   96/120dpi 度量差异都会造成个位数像素级告警。基线已于 2026-11-07 清零（全部 dump 0 命中），
  该规则**默认是硬门**；排查度量噪音时用 --textfit-lenient 退回告警。
   注意：warn-only 是告警不是阻断门，报告时不得称其为硬门。
   主窗分支（2026-11-01 复核修正）：hitRegions 与 R6 是通用规则，mainform 同样受判
   （此前 mainform 分支提前 continue，把这两条新规则的真实目标主窗跳掉了）。

自绘命中区（审计 C5/C7-③）：节点带 hitRegions 字段时，每个区最小边 ≥ 24 逻辑 px
   （WCAG 2.2 SC 2.5.8，与 TabChromePainter.GetCloseHitRect 的扩张口径同源）——
   R1 的“Control 节点”口径看不见的自绘交互区（标签 × 等）由此纳入。
   量纲同上：阈值 = 24×dpi/96 设备像素。0 个命中区时只报“受检=0”不假绿，
   “场景必须存在”由 smoke 侧显式断言（先断言存在，再判尺寸）。

R5 停靠遮挡：同父可见兄弟中 Dock=Fill 与边停靠(Top/Bottom/Left/Right)不得相交 ——
   "工具栏/表头盖住表体"类布局 bug 的可测签名；Fill vs Fill 豁免（同格覆盖层靠可见性互斥）。

另含：兄弟重叠（交叠面积 > OVERLAP_MIN(16px2)，过滤包含关系）、子越界（容差 TOL(2px)）、
零尺寸可见叶、表行高 24-34、AntdUI.Button 左右 padding 全 0。

用法: python3 tools/ui-tree-check.py <jsonDir> [--dpi-factor=1.5]
退出码 0=全过，1=有失败。

--dpi-factor=f（L2 矩阵门用）：dump 的几何是在「DpiScale 系数 f」下算出的 96dpi 设备像素，
  故所有逻辑阈值（R1 触击 32 / 命中区 24 / 表行高 24-34 / R6 容差 4px）统一乘 f。
  默认 1 = 常规 ui-smoke 口径（行为与加参数前完全一致）。
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import pnglite
except Exception:      # 读不了 PNG 时 R8 自动降级为 info（老证据目录没有 png）
    pnglite = None

FAIL = []


def hex_luminance(hex_str):
    """"#RRGGBB"/"#AARRGGBB" → 相对亮度 0..1（WCAG 口径）；空/透明/解析失败 → None。"""
    if not hex_str or not isinstance(hex_str, str) or not hex_str.startswith("#"):
        return None
    h = hex_str[1:]
    try:
        if len(h) == 8:
            a = int(h[:2], 16)
            if a == 0:
                return None          # 全透明 = 继承父色，不判
            h = h[2:]
        if len(h) != 6:
            return None
        r, g, b = int(h[0:2], 16) / 255.0, int(h[2:4], 16) / 255.0, int(h[4:6], 16) / 255.0
    except ValueError:
        return None
    def lin(c):
        return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)
WARN = []
TOL = 2          # 越界容差 px
OVERLAP_MIN = 16  # 重叠告警阈值 px2（4x4）
HIT_MIN = 32      # 最小触击目标 px（R1；change 2026-09-24 定稿，44 噪音过大否决）
TEXTFIT_TOL = 4   # 文本适配容差 px（R6；96/120dpi 度量差异的经验上限）
# R6 文本适配：2026-11-07 起默认硬门。
# 翻转依据：R16（AutoSize 钮不吃 Padding）+ R17（AutoSize 标签窄 10%）清零后，
# ui-smoke 全部 50 个 dump 文本适配=0（受检 500+ 个承载文本控件），基线不再是“积累中”。
# 反向口子留给 DPI/字体矩阵里的度量噪音排查：--textfit-lenient 退回 warn-only。
TEXTFIT_STRICT = "--textfit-lenient" not in sys.argv


def flag_value(name, default=None):
    """读 --name=value 形式的旗标（值自带 = 避免与位置参数目录混淆）。"""
    for a in sys.argv[1:]:
        if a.startswith(name + "="):
            return a.split("=", 1)[1]
    return default


# L2 矩阵门（docs/UI-UX-VERIFICATION.md §4.2）：矩阵里的 dump 是"在 DPI 系数 f 下算出来的 96dpi
# 设备像素"，所有**逻辑**阈值（触击 32 / 命中区 24 / 表行高 24-34 / 文本容差）都要乘 f，
# 否则 150% 那一格会按 100% 的尺子量，出现大批假红。
try:
    DPI_FACTOR = float(flag_value("--dpi-factor", "1") or "1")
except ValueError:
    DPI_FACTOR = 1.0


def sbool(v, default=False):
    """dump 布尔字段是字符串 "true"/"false"（UiTreeDumper 约定）。
    直接 truthy 判断会把 "false" 当 True，使过滤/断言静默失效
    （R4 的 tabStop is False、R6 的 autoSize/wrap 都因此成死规则，2026-11-01 复核修正）。"""
    if isinstance(v, str):
        return v.lower() == "true"
    if v is None:
        return default
    return bool(v)


def check(ok, what):
    print(("  ok: " if ok else "  FAIL: ") + what)
    if not ok:
        FAIL.append(what)


def walk(node, parent_abs=None, parent_name="", parent_node=None, parent_key="ROOT", out=None):
    out = out if out is not None else []
    if "controls" in node:
        # 根：form 节点，遍历顶层控件
        for ch in node["controls"]:
            walk(ch, None, node.get("form", ""), None, "ROOT", out)
    else:
        out.append((node, parent_abs, parent_name, parent_node, parent_key))
        for ch in node.get("children", []):
            walk(ch, node.get("abs"), node.get("name") or node.get("type"), node,
                 parent_key + "/" + node.get("type", "?") + "@" + str(id(node)), out)
    return out


def _kids(node):
    """子节点：root 用 controls、其余用 children（UiTreeDumper 的两种键）。"""
    return node.get("children") or node.get("controls") or []


def _nm(n):
    return n.get("name") or (n.get("text") or "")[:12] or "?"


def area(r):
    return max(0, r["w"]) * max(0, r["h"])


def intersect(a, b):
    x0, y0 = max(a["x"], b["x"]), max(a["y"], b["y"])
    x1, y1 = min(a["x"] + a["w"], b["x"] + b["w"]), min(a["y"] + a["h"], b["y"] + b["h"])
    return max(0, x1 - x0) * max(0, y1 - y0)


def contains(a, b, tol=TOL):
    return (b["x"] >= a["x"] - tol and b["y"] >= a["y"] - tol
            and b["x"] + b["w"] <= a["x"] + a["w"] + tol
            and b["y"] + b["h"] <= a["y"] + a["h"] + tol)


def siblings_overlap(nodes, av):
    """同父兄弟重叠：只查有效可见（自身+全部祖先可见）、非包含关系的两两交叠。"""
    vis = [n for (n, _, _) in [(t[0], t[1], t[2]) for t in nodes] if av.get(id(n), True) and area(n.get("abs", {})) > 0]
    bad = []
    for i in range(len(vis)):
        for j in range(i + 1, len(vis)):
            a, b = vis[i].get("abs"), vis[j].get("abs")
            if contains(a, b) or contains(b, a):
                continue
            ov = intersect(a, b)
            if ov >= OVERLAP_MIN:
                bad.append((vis[i], vis[j], ov))
    return bad


def ancestor_visible_map(tree):
    """id(node) -> “该节点自身及全部祖先 visible”。

    WinForms 隐藏父窗即隐藏整棵子窗树（渲染层面 Visible=false 向下传递），
    所以隐藏祖先链里的控件永不绘出——几何规则应视为不存在。
    背景（CI 0.1.389 dump 实证）：AntdUI.Tabs 隐藏页被置 Bounds(0,0,0,0)，
    其内 AutoSize 子件按宽 0 折行叠高，若只看控件自身 Visible（true）会把
    隐藏页里的布局判成“越界”——但源头修好后（工具条/空态盒随零面积隐藏），
    隐藏链上的节点必须整链免判，否则仍会假红。注：零面积祖先不算隐藏
    （子窗不裁剪），只有 visible=false 的祖先才免判。
    """
    m = {}
    def rec(n, anc):
        vis = anc and sbool(n.get("visible"))
        m[id(n)] = vis
        for c in _kids(n):
            rec(c, vis)
    m[id(tree)] = sbool(tree.get("visible"), True)
    for c in _kids(tree):
        rec(c, m[id(tree)])
    return m



def pixel_residue(node_list, av, png_path, origin):
    """按截图像素判"白块"：几何全对、属性全对，画出来还是一块白。

    为什么需要它：AntdUI 自绘控件用库自己的色板绘制，BackColor/ForeColor 根本不参与
    （R7 只能跳过它们），于是"暗色主题下按钮是白底"这类问题属性层面无迹可寻，
    R7 上线当天就实证过：底栏八个按钮在截图里是一排纯白块，而所有属性检查全绿。

    判法：每个可见叶控件（≥24×12）在截图里的矩形，采样近白像素占比 > 0.5 → 白块。
    大矩形隔像素采样（几何不变的统计量，20000px 以上降采样不影响结论）。
    返回 (受检数, [问题描述...])。
    """
    if pnglite is None or not origin:
        return 0, []
    try:
        img = pnglite.load(png_path)
    except Exception:
        return 0, []
    ox, oy = origin
    checked, bad = 0, []
    for n, _pabs, _pn, _pnode, _pk in node_list:
        if not av.get(id(n), True):
            continue
        if n.get("children"):
            continue
        a = n.get("abs") or {}
        x0, y0 = int(a.get("x", 0)) - ox, int(a.get("y", 0)) - oy
        w, h = int(a.get("w", 0)), int(a.get("h", 0))
        if w < 24 or h < 12 or w * h < 600:
            continue
        x1, y1 = min(img.w, x0 + w), min(img.h, y0 + h)
        x0, y0 = max(0, x0), max(0, y0)
        if x1 - x0 < 24 or y1 - y0 < 12:
            continue
        short = (n.get("type") or "").split(".")[-1]
        # 天生就是浅色的控件（二维码白底、图片）从规则里豁免
        if short in ("QrCode", "PictureBox", "Image", "WebView2"):
            continue
        # 只数「中心一半」：整块矩形里带白字的深色控件白像素也有 10~20%，
        # 而真正的白底 bug（底栏按钮实测）白只占中间一条 —— 全矩形占比 0.37，
        # 中心一半却接近 1.0。这个指标对"文字发白"和"底色发白"区分得很干净。
        cx0 = x0 + (x1 - x0) // 4
        cx1 = x1 - (x1 - x0) // 4
        cy0 = y0 + (y1 - y0) // 4
        cy1 = y1 - (y1 - y0) // 4
        if cx1 - cx0 >= 8 and cy1 - cy0 >= 6:
            x0, y0, x1, y1 = cx0, cy0, cx1, cy1
        step = 1 if (x1 - x0) * (y1 - y0) <= 20000 else 2
        white = tot = 0
        px = img.px
        for y in range(y0, y1, step):
            base = y * img.w * 4
            for x in range(x0, x1, step):
                i = base + x * 4
                tot += 1
                if px[i] > 200 and px[i + 1] > 200 and px[i + 2] > 200:
                    white += 1
        if tot == 0:
            continue
        checked += 1
        ratio = white / float(tot)
        if ratio > 0.5:
            bad.append("白块 %s[%s] %dx%d 近白像素 %.0f%%（暗色主题下应为 Surface/Background）\"%s\"" % (
                n.get("name") or "?", short, w, h, ratio * 100, (n.get("text") or "")[:16]))
    return checked, bad
def mainform_rules(tree, check):
    """主窗外壳盒模型（96dpi 基准）：菜单 Top/树 Left250/底栏 Bottom≈30/Tab Fill 余量。"""
    nodes = []
    def walk(n, pabs, pname):
        nodes.append((n, pabs, pname))
        for c in n.get("children", []):
            walk(c, n.get("abs"), n.get("name") or n.get("type", "?"))
    walk(tree, None, "")
    bytype = {}
    for n, _, _ in nodes:
        bytype.setdefault(n.get("type", "").split(".")[-1], []).append(n)
    # 1) 树宽 250（pin 态）
    for t in bytype.get("ConnectionTreeControl", []):
        w = t.get("abs", {}).get("w", 0)
        check(240 <= w <= 260, "连接树宽=%d≈250" % w)
    # 2) 底栏高≈30（GetPreferredHeight=Max(30,RowStep)）
    for b in bytype.get("BottomBarPanel", []):
        h = b.get("abs", {}).get("h", 0)
        check(28 <= h <= 44, "底栏高=%d≈30" % h)
    # 3) Tab 容器 Fill：宽≈窗宽-树-缝，高≈窗高-菜单-底栏
    fw = tree.get("abs", {}).get("w", 0)
    fh = tree.get("abs", {}).get("h", 0)
    for t in bytype.get("TabContainerControl", []):
        a = t.get("abs", {})
        check(abs(a.get("w", 0) - (fw - 250 - 4)) <= 24, "Tab宽=%d≈窗宽-254" % a.get("w", 0))
        check(a.get("h", 0) >= fh - 24 - 44 - 24, "Tab高=%d吃掉余量" % a.get("h", 0))
    # 4) 欢迎页与锁遮罩 Fill 且互斥可见（无 tab 时欢迎可见）
    for w in bytype.get("WelcomePanel", []):
        a = w.get("abs", {})
        check(a.get("w", 0) >= fw - 250 - 4 - 24, "欢迎页宽=%d≈Fill" % a.get("w", 0))


def main():
    # flag（--textfit-lenient 等）与位置参数分离：此前 argv[1] 无条件当目录，
    # flag 放第一位会 FileNotFoundError 崩溃（2026-11-01 复核发现）。
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    d = args[0] if args else "ui-smoke"
    # combo.json 是 L2 矩阵的"这一格的显示条件"，不是控件树 dump —— 不能当 dump 判，
    # 否则每格都会多出一条毫无意义的"关闭路径=0"假红。
    files = sorted(f for f in os.listdir(d)
                   if f.endswith(".json") and f != "combo.json")
    print("=== ui-tree-check on %s (%d files) ===" % (d, len(files)))
    if not files:
        check(False, "无 json dump")
        return 1

    # 截图覆盖：dump 与 png 必须成对。R8 是像素判据，没图的页面等于在像素层零证据——
    # 而"没有证据"和"通过"在报告里长得一模一样，这是最危险的一种假绿。
    # 免判清单只允许写"捕获手段本身做不到"的页面，并必须注明原因（当前为空）。
    NO_PNG_OK = set()
    missing_png = [f[:-5] for f in files
                   if not os.path.exists(os.path.join(d, f[:-5] + ".png"))
                   and f[:-5] not in NO_PNG_OK]
    print("  截图覆盖: %d/%d" % (len(files) - len(missing_png), len(files)))
    if missing_png:
        for m in missing_png[:10]:
            check(False, "缺截图 " + m + ".png（R8 无法判该页面）")

    for fn in files:
        with open(os.path.join(d, fn), encoding="utf-8-sig") as fp:
            tree = json.load(fp)
        print("--- " + fn + " (" + tree.get("form", "?") + ") ---")
        fname = fn
        all_nodes = walk(tree)
        print("  控件总数=%d" % len(all_nodes))
        # 有效可见性：自身+全部祖先 visible（隐藏祖先链整链免判，见函数注释）
        av = ancestor_visible_map(tree)

        # 按父分组查兄弟重叠
        by_parent = {}
        for n, pabs, pname, pnode, pkey in all_nodes:
            by_parent.setdefault(pkey, []).append((n, pabs, pname))
        overlaps = 0
        for pname, group in by_parent.items():
            if len(group) < 2:
                continue
            for a, b, ov in siblings_overlap(group, av):
                # Dock 相对布局天然相邻：Dock!=None 的跳过（停靠不算重叠）
                if a.get("dock") != "None" or b.get("dock") != "None":
                    continue
                overlaps += 1
                check(False, "重叠 %s[%s]%s vs %s[%s]%s 交叠%dpx2" % (
                    a.get("name") or "?", a.get("type", "").split(".")[-1], a.get("abs"),
                    b.get("name") or "?", b.get("type", "").split(".")[-1], b.get("abs"), ov))
        if overlaps == 0:
            check(True, "兄弟重叠=0")

        # 越界：子超出父（Dock.Fill / AutoScroll 豁免）
        # 免判2条（285 实测结论）：①父 h<=1 的 AntdUI 自绘容器（StackPanel/__IN__ 未布局量测局限）；
        # ②文本为 … 的溢出钮（它是窄窗下剩余命令唯一入口，无处可收，设计取舍）。
        # ③父 AutoScroll=true（滚动容器内容高出是正常态，如 ConnectionDialog 高级区，288 实测）。
        oob = 0
        for n, pabs, pname, pnode, _pkey in all_nodes:
            if pabs is None or not av.get(id(n), True):
                continue
            if n.get("dock") == "Fill":
                continue
            if (n.get("text") or "") == "\u2026":
                continue
            a, b = pabs, n.get("abs")
            if area(b) <= 0:
                continue
            if pabs.get("h", 99) <= 1:
                continue
            if pnode is not None and (pnode.get("extra") or {}).get("AutoScroll") == "True":
                continue
            if not contains(a, b):
                oob += 1
                check(False, "越界 %s[%s]%s 超出父 %s%s" % (
                    n.get("name") or "?", n.get("type", "").split(".")[-1], b, pname, a))
        if oob == 0:
            check(True, "子越界=0")

        # 零尺寸可见叶（Divider 线型 h<=2 豁免；Dock.Fill 未激活页豁免；零面积父链后代豁免）
        zero = [n for (n, pabs, _, _pnode, _pkey) in all_nodes
                if av.get(id(n), True) and not n.get("children")
                and (n.get("abs", {}).get("w", 1) <= 0 or n.get("abs", {}).get("h", 1) <= 0)
                # 空文本 AutoSize Label 正常态（如 errorLabel，有错才撑开）
                and not ("Divider" in n.get("type", "") and n.get("abs", {}).get("h", 99) <= 2)
                and not ("Label" in n.get("type", "") and not (n.get("text") or ""))
                and n.get("dock") != "Fill"
                and not (pabs is not None and area(pabs) <= 0)]
        check(len(zero) == 0, "零尺寸可见叶=%d" % len(zero))
        for n in zero[:5]:
            check(False, "零尺寸 " + (n.get("name") or n.get("type", "?")))

        # === 通用规则（mainform 同样受判，2026-11-01 复核修正）===
        # 空控件树 = 采集失败或窗体构造失败；旧逻辑 0 对象全打 ok → ALL OK 假绿。
        if len(all_nodes) == 0:
            check(False, "空控件树：采集失败或窗体构造失败，本文件不可判")

        # 自绘命中区（审计 C5/C7-③）：每个 hitRegion 最小边 ≥ 24 逻辑 px。
        # 量纲（复核修正）：PerMonitorV2 → abs 是设备像素；逻辑阈值 T 换算 T×dpi/96。
        # 此前写成 24×96/dpi（方向反了）：96dpi 下 s=1 恰好隐藏，192dpi 下 16逻辑px 的
        # 命中区（32 设备px）会假过 —— 高 dpi 只能要求更多设备像素，不能更少。
        dpi = tree.get("dpi", 96) or 96
        scale = (dpi / 96.0) * DPI_FACTOR
        hit_min_d = 24.0 * scale
        hit_total = 0
        hit_nodes = 0
        hit_small = 0
        for n, _, _, _, _ in all_nodes:
            hrs = n.get("hitRegions") or []
            if not hrs:
                continue
            hit_nodes += 1
            hit_total += len(hrs)
            for hr in hrs:
                a = hr.get("abs", {})
                if a.get("w", 0) < hit_min_d or a.get("h", 0) < hit_min_d:
                    hit_small += 1
                    check(False, "自绘命中区过小 %s %dx%d dev < %d dev(=24逻辑@%ddpi) (%s)" % (
                        n.get("name") or n.get("type", "?").split(".")[-1],
                        a.get("w", 0), a.get("h", 0), int(hit_min_d), dpi,
                        hr.get("label", "?")))
        if hit_total == 0:
            # 0 个命中区不假绿：只报受检数。“场景必须存在”由 smoke 侧断言
            # （MainFormSmoke 已断言开标签后 GetHitRegions()≥1），此处是采集层兜底。
            print("  info: 自绘命中区受检=0（无 hitRegions 数据）")
        elif hit_small == 0:
            check(True, "自绘命中区达标 %d 区/%d 控件 (%d 失败=0)" % (hit_total, hit_nodes, 0))

        # R6 文本适配（审计 C7-1；基线 2026-11-07 清零，默认硬门，--textfit-lenient 退回告警）：
        # 历史最高频 bug 是“文字被裁/贴顶/挤成两行”，dump 带真实 GDI 度量后按“所需 > 可用”判。
        # 口径：
        #   - 只判承载标签文本的控件（Button/Label/CheckBox/RadioButton 等），
        #     输入框/表格/列表本身可滚动或自带省略号，纳入会产生大量噪音；
        #   - AutoSize=true 的控件由 WinForms 自己撑开，跳过；
        #   - 老 dump 没有 textSingleW 字段 → 跳过（向后兼容），受检数照实记录。
        TEXTFIT_TYPES = ("Button", "Label", "Checkbox", "CheckBox", "Radio", "RadioButton")
        overflow = 0
        measured = 0
        skipped_ellipsis = 0
        for n, _a, _b, _c, _d in all_nodes:
            if not sbool(n.get("visible")):
                continue
            text = (n.get("text") or "").strip()
            if not text or text == "\u2026":
                continue
            short = (n.get("type") or "").split(".")[-1]
            if short not in TEXTFIT_TYPES:
                continue
            if sbool(n.get("autoSize")):
                continue
            # 声明了 AutoEllipsis 的控件（底部状态行/提示行）设计上就用 "…" 收尾，
            # 完整语义在 tooltip / AccessibleDescription 里 —— 判它"溢出"是误报。
            # 前提是 dump 真带了这个字段（老 dump 没有 → 照旧判）。
            if sbool(n.get("autoEllipsis")):
                skipped_ellipsis += 1
                continue
            if "textSingleW" not in n:
                continue
            box = n.get("abs", {})
            if box.get("w", 0) <= 0 or box.get("h", 0) <= 0:
                continue
            measured += 1
            pad = n.get("padding") or {}
            avail_w = box.get("w", 0) - pad.get("l", 0) - pad.get("r", 0)
            avail_h = box.get("h", 0) - pad.get("t", 0) - pad.get("b", 0)
            if sbool(n.get("wrap")):
                need_h = n.get("textWrapH", 0)
                if need_h > avail_h + TEXTFIT_TOL * scale:
                    overflow += 1
                    WARN.append("文字裁切 %s[%s] 需%dpx高 可用%dpx「%s」" % (
                        _nm(n), short, need_h, avail_h, text[:20]))
            else:
                need_w = n.get("textSingleW", 0)
                if need_w > avail_w + TEXTFIT_TOL * scale:
                    overflow += 1
                    WARN.append("文字溢出 %s[%s] 需%dpx宽 可用%dpx「%s」" % (
                        _nm(n), short, need_w, avail_w, text[:20]))
        # R7 主题色残留（2026-11-07 新增）：暗色外壳里还留着浅底/黑字。
        # 这类 bug 只有肉眼看得出来——几何全对、断言全绿，用户看到的是"一张白页"。
        # 判定：外壳三套主题（Dark/Darker/OLED）全为深色，因此
        #   ① 容器/标签的实际 BackColor 不得是浅色（相对亮度 > 0.5）；
        #   ② 有文字的标签/勾选框 ForeColor 不得是深色（亮度 < 0.2）。
        # 免判：AntdUI 自绘控件（用库主题绘制，BackColor/ForeColor 不参与绘制）、
        #       Transparent/Empty（继承父色）、不可见节点。
        theme = (tree.get("theme") or "Dark")
        if theme in ("Dark", "Darker", "OLED"):
            COLOR_CONTAINERS = ("Panel", "FlowLayoutPanel", "TableLayoutPanel", "SplitContainer",
                                "SplitterPanel", "UserControl", "GroupBox", "TabControl", "TabPage")
            COLOR_TEXT = ("Label", "CheckBox", "Checkbox", "RadioButton", "LinkLabel")
            residue = []
            checked = 0
            for n, _a, _b, _c, _d in all_nodes:
                if not sbool(n.get("visible")) or not sbool(n.get("enabled")):
                    continue
                full = n.get("type") or ""
                if full.startswith("AntdUI."):
                    continue
                short = full.split(".")[-1]
                # TabControl 的 BackColor 是不可信的幽灵值：开了视觉样式后 WinForms 直接
                # 忽略它（代码里赋了 Background，读回来还是系统 #F0F0F0），而标签栏由
                # TabChromePainter 自绘、内容区被 TabPage 盖满 —— 实测整块矩形里
                # #F0F0F0 像素数为 0（mainform 截图 2026-11-07）。属性错、画面对，
                # 这种"错在属性"的项不该判红；画面是否真有白块由 R8 按像素判。
                if short == "TabControl" and (n.get("extra") or {}).get("DrawMode") == "OwnerDrawFixed":
                    continue
                back = n.get("back") or ""
                fore = n.get("fore") or ""
                if short in COLOR_CONTAINERS or short in COLOR_TEXT:
                    lum = hex_luminance(back)
                    if lum is not None:
                        checked += 1
                        if lum > 0.5:
                            residue.append("浅底 %s[%s] back=%s（暗色主题下应为 Surface/Background）\"%s\"" % (
                                n.get("name") or "?", short, back, (n.get("text") or "")[:16]))
                if short in COLOR_TEXT and (n.get("text") or "").strip():
                    lum = hex_luminance(fore)
                    if lum is not None:
                        if lum < 0.2:
                            residue.append("黑字 %s[%s] fore=%s\"%s\"" % (
                                n.get("name") or "?", short, fore, (n.get("text") or "")[:16]))
            if checked == 0:
                print("  info: 主题色受检=0（无 back/fore 字段）")
            elif residue:
                for r in residue[:8]:
                    check(False, "主题色残留 " + r)
            else:
                check(True, "主题色残留=0 (受检=%d, 主题=%s)" % (checked, theme))

        # R8 截图像素残留：属性全绿但画面是白块（AntdUI 自绘色板、漏贴色的自绘控件）。
        # 依赖同目录同名 .png + dump 的 clientOrigin；缺一即降级为 info（老证据目录兼容）。
        origin = tree.get("clientOrigin")
        png_path = os.path.join(d, fname[:-5] + ".png")
        if pnglite is None or not origin or not os.path.exists(png_path):
            print("  info: 像素检查跳过（pnglite/origin/png 缺一）")
        else:
            px_checked, px_bad = pixel_residue(
                all_nodes, av, png_path, (origin.get("x", 0), origin.get("y", 0)))
            if px_checked == 0:
                print("  info: 像素受检=0（控件矩形都在截图外）")
            elif px_bad:
                for b in px_bad[:6]:
                    check(False, "截图" + b)
            else:
                check(True, "截图白块=0 (受检=%d 叶)" % px_checked)
        if measured == 0:
            print("  info: 文本适配受检=0（无 textSingleW 字段或无文本承载控件）")
        elif overflow == 0:
            check(True, "文本适配=0 (受检=%d%s)" % (
                measured, "，省略号豁免 %d" % skipped_ellipsis if skipped_ellipsis else ""))
        elif TEXTFIT_STRICT:
            for w in WARN[-overflow:]:
                check(False, w)
        else:
            print("  warn: 文本适配候选=%d (受检=%d；默认硬门，--textfit-lenient 只告警)" % (overflow, measured))
            for w in WARN[-overflow:]:
                print("    - " + w)

        if fname.startswith("mainform"):
            mainform_rules(tree, check)
            continue

        # 表行高
        for n, _, _, _, _ in all_nodes:
            rh = (n.get("extra") or {}).get("RowHeight", "")
            if rh and rh != "null" and rh != "":
                try:
                    v = int(float(rh))
                    # 行高地板是逻辑 px（24）→ 阈值随 DPI 系数走（150% 下 36-51 才是对的）
                    check(24 * scale <= v <= 34 * scale,
                          "%s 行高=%d 在%d-%d" % (n.get("name") or "表", v,
                                                int(24 * scale), int(34 * scale)))
                except ValueError:
                    pass

        # AntdUI.Button 全零 padding 告警
        for n, _, _, _, _ in all_nodes:
            t = n.get("type", "")
            if "AntdUI" in t and "Button" in t:
                # 固定尺寸钮靠库内 sps 居中（字高*0.4/侧），Padding=0 不贴边；只判 AutoSize 钮
                if not sbool(n.get("autoSize")):
                    continue
                p = n.get("padding", {})
                if p.get("l", 1) == 0 and p.get("r", 1) == 0:
                    check(False, "按钮左右padding=0(贴边风险) %s[%s]" % (n.get("name") or "?", n.get("text", "")))

        # R1 最小触击目标：可见可交互控件 abs 宽高至少一维 >= 32 逻辑 px（按 dpi 换算设备像素）。
        # 口径：visible && enabled && 子节点为空（叶）&& 非线型（Divider h<=2 豁免沿用零尺寸口径）。
        # 免判 R1-1：文本为 … 的溢出钮（窄窗唯一入口，设计取舍，沿用越界免判②）。
        # 主窗 11 处合法紧凑控件（109-118x25 快捷卡片 + 隐藏覆盖钮）走 mainform 分支不覆盖，
        # 故 R1 只对非 mainform 文件生效。
        NON_HIT = ("Label", "Divider", "Panel", "Splitter")
        hit_min_r1 = HIT_MIN * scale  # 设备 px（96dpi 下 = 32，与历史口径恒等）
        small = 0
        for n, _, _, _, _ in all_nodes:
            if not sbool(n.get("visible")) or not sbool(n.get("enabled"), True):
                continue
            if n.get("children"):
                continue
            if any(k in n.get("type", "") for k in NON_HIT):
                continue  # 展示型控件无触击语义（CI 321 实测：Label 24x16/27x16/18x16 全属此类）
            a = n.get("abs", {})
            if a.get("w", 0) <= 0 or a.get("h", 0) <= 0:
                continue  # 零尺寸规则已判，不重复
            if "Divider" in n.get("type", "") and a.get("h", 99) <= 2:
                continue
            if (n.get("text") or "") == "\u2026":
                continue
            if a.get("w", 0) < hit_min_r1 and a.get("h", 0) < hit_min_r1:
                small += 1
                check(False, "触击过小 %s[%s]%dx%d dev < %d dev(=32逻辑)" % (
                    n.get("name") or "?", n.get("type", "").split(".")[-1],
                    a.get("w", 0), a.get("h", 0), int(hit_min_r1)))
        if small == 0:
            check(True, "触击目标=0")

        # R3 关闭绑定：每窗至少一个关闭路径（名含 Close/Cancel/OK/确定/取消/关闭其一）。
        # 免判 R3-1（2026-09-24 定稿 + CI 320 补 dangerous-cmd 配置页 + CI 321 补 scanner-center 工具窗）。
        # 侧栏面板（审计 C7-2 新增 dump）：非独立窗口，关闭语义由宿主侧栏承担（关 Tab/收侧栏），
        # 面板自身没有也不该有 Close/Cancel 钮。
        if fname.startswith(("transfer-progress", "pwd-generator", "setup-wizard", "dangerous-cmd",
                              "scanner-center",
                              "command-history", "port-forward", "secret-scan",
                              "key-binding", "toolbox", "logon-script",
                              # 2026-11 新增的 10 个侧栏面板 dump（同为面板，无 Close 语义）
                              "macro", "bookmarks", "command-template", "highlight-rule",
                              "transfer-center", "notification-center", "multi-channel",
                              "batch-command", "health-monitor", "snippet-search",
                              # 2026-11 补的 6 个"以前从没进过 dump"的页 + 失败态宿主（同为面板）
                              "remote-monitor", "bottom-bar", "lock-overlay", "welcome",
                              "connection-tree", "terminal-search", "fail-history-load",
                              "fail-scan-error")):
            check(True, "关闭绑定免判(" + fname + ")")
        else:
            closelike = [n for (n, _, _, _, _) in all_nodes
                         if any(k in (n.get("name") or "") + (n.get("text") or "")
                                 for k in ("Close", "Cancel", "OK", "Ok", "确定", "取消", "关闭"))]
            check(len(closelike) > 0, "关闭路径=%d(>0, Close/Cancel/OK/确定/取消/关闭)" % len(closelike))

        # R4 键盘可达性（change 2026-09-25 D2 spike 定稿）
        # ① “聚焦是否可见”（AntdUI 自绘焦点框）在控件树 dump 里不可观测 → 不能成规则，故不落。
        # ② 可观测代理 = 交互叶 TabStop=false（键盘不可达）。spike 证据（CI 321 全 18 份 dump）：
        #    主窗以外零命中；主窗 11 处（快捷卡片 109x25 与两个隐藏覆盖钮）走 mainform 分支本规则不覆盖。
        #    故当前口径零误报，直接落为硬门：任何对话框新增 TabStop=false 的交互控件即 CI 红。
        #    例外通道：确需不可 Tab（如仅鼠标/快捷键可达的覆盖钮）时在此前缀豁免表登记原因。
        INTER_TYPES = ("Button", "Input", "Select", "Combo", "Checkbox", "Radio", "Table", "Tabs")
        # 类型必须精确比对短名：Panel/TableLayoutPanel 等不可聚焦容器 TabStop 恒 false，
        # 子串匹配（"Table" 命中 TableLayoutPanel）会造出一堆假告警（首版实测 connection-rdp 9 处空名容器）。
        unreachable = [n for (n, _a, _b, _c, _d) in all_nodes
                       if sbool(n.get("visible")) and sbool(n.get("enabled"), True)
                       and (n.get("type") or "").split(".")[-1] in INTER_TYPES
                       and not sbool(n.get("tabStop"))]
        check(len(unreachable) == 0, "键盘可达性: 交互叶 TabStop=false 数=%d" % len(unreachable))
        for n in unreachable[:5]:
            check(False, "键盘不可达 %s[%s]" % (n.get("name") or n.get("type", "?").split(".")[-1],
                                                n.get("text") or ""))

        # R5 停靠遮挡（change 2026-09-25 D5 新增）：同父可见兄弟中 Dock=Fill 与非 Fill 边停靠
        # （Top/Bottom/Left/Right）不得相交 —— 这是“工具栏/表头盖住表体”类布局 bug 的可测签名，
        # 原重叠规则因“Dock!=None 一律跳过”漏判（CI 321 scanner-center 工具栏盖住两表表头 56px 即此）。
        # 口径来自 CI 321/323 全量实测：除 scanner-center（2026-09-25-ux-empty-focus 已修）与
        # dangerous-cmd 外，其余 dump 零命中。dangerous-cmd 的 3 处已由
        # 2026-09-25-dangerous-cmd-dock-overlap 修复并撤销前缀豁免——规则对已知违例不再失灵。
        # 注：C# 侧 UiSmokeRunner.AssertNoDockOverlap 同口径且真在 CI 里跑（DialogsSmoke 全量对话框），
        # 本规则用于事后审查产物。
        EDGE = ("Top", "Bottom", "Left", "Right")
        dock_ov = 0
        # tree 自身（窗体根）也要比：scanner-center 的工具栏 vs SplitContainer 就是根级兄弟
        for parent, _pa, _pn, _pnode, _pkey in [(tree, None, "", None, "ROOT")] + walk(tree):
            vis = [c for c in _kids(parent) if sbool(c.get("visible"))]
            for i in range(len(vis)):
                for j in range(i + 1, len(vis)):
                    a, b = vis[i], vis[j]
                    da, db = a.get("dock"), b.get("dock")
                    if not ((da == "Fill" and db in EDGE) or (db == "Fill" and da in EDGE)):
                        continue
                    ov = intersect(a.get("abs", {}), b.get("abs", {}))
                    if ov > 0:
                        dock_ov += 1
                        check(False, "停靠遮挡 %s[%s](%s) vs %s[%s](%s) 交叠%dpx2" % (
                            _nm(a), a.get("type", "").split(".")[-1], da,
                            _nm(b), b.get("type", "").split(".")[-1], db, ov))
        if dock_ov == 0:
            check(True, "停靠无遮挡=0")

    print()
    if WARN:
        print("UI-TREE-CHECK WARN: %d（R6 文本适配候选，已计为失败；--textfit-lenient 可退回告警）" % len(WARN))
    if FAIL:
        print("UI-TREE-CHECK FAIL: %d" % len(FAIL))
        return 1
    print("UI-TREE-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
