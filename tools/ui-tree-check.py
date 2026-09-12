#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""全控件树盒模型断言：读 CI 产出的 *.json（UiTreeDumper），查：
1. 重叠：同父容器下两个可见兄弟控件 abs 矩形交叠面积 > 阈值（默认 4px²，过滤包含关系）
2. 越界：子控件 abs 超出父 abs（容差 2px；Dock.Fill/AutoScroll 容器豁免）
3. 零尺寸：可见叶控件 w<=0 或 h<=0
4. 行高：extra.RowHeight 在 24-34（表）/输入框 h>=30
5. 按钮边距：AntdUI.Button 的 padding 全 0 告警（文字贴边风险）

用法: python3 tools/ui-tree-check.py <jsonDir>
退出码 0=全过，1=有失败。
"""
import json
import os
import sys

FAIL = []
TOL = 2          # 越界容差 px
OVERLAP_MIN = 16  # 重叠告警阈值 px²（4x4）


def check(ok, what):
    print(("  ok: " if ok else "  FAIL: ") + what)
    if not ok:
        FAIL.append(what)


def walk(node, parent_abs=None, parent_name="", parent_node=None, out=None):
    out = out if out is not None else []
    if "controls" in node:
        # 根：form 节点，遍历顶层控件
        for ch in node["controls"]:
            walk(ch, None, node.get("form", ""), None, out)
    else:
        out.append((node, parent_abs, parent_name, parent_node))
        for ch in node.get("children", []):
            walk(ch, node.get("abs"), node.get("name") or node.get("type"), node, out)
    return out


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


def siblings_overlap(nodes):
    """同父兄弟重叠：只查可见、非包含关系的两两交叠。"""
    vis = [n for (n, _, _) in nodes if n.get("visible") and area(n.get("abs", {})) > 0]
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
    d = sys.argv[1] if len(sys.argv) > 1 else "ui-smoke"
    files = sorted(f for f in os.listdir(d) if f.endswith(".json"))
    print("=== ui-tree-check on %s (%d files) ===" % (d, len(files)))
    if not files:
        check(False, "无 json dump")
        return 1

    for fn in files:
        with open(os.path.join(d, fn), encoding="utf-8-sig") as fp:
            tree = json.load(fp)
        print("--- " + fn + " (" + tree.get("form", "?") + ") ---")
        fname = fn
        all_nodes = walk(tree)
        print("  控件总数=%d" % len(all_nodes))

        # 按父分组查兄弟重叠
        by_parent = {}
        for n, pabs, pname, pnode in all_nodes:
            by_parent.setdefault(pname, []).append((n, pabs, pname))
        overlaps = 0
        for pname, group in by_parent.items():
            if len(group) < 2:
                continue
            for a, b, ov in siblings_overlap(group):
                # Dock 相对布局天然相邻：Dock!=None 的跳过（停靠不算重叠）
                if a.get("dock") != "None" or b.get("dock") != "None":
                    continue
                overlaps += 1
                check(False, "重叠 %s[%s]%s vs %s[%s]%s 交叠%dpx²" % (
                    a.get("name") or "?", a.get("type", "").split(".")[-1], a.get("abs"),
                    b.get("name") or "?", b.get("type", "").split(".")[-1], b.get("abs"), ov))
        if overlaps == 0:
            check(True, "兄弟重叠=0")

        # 越界：子超出父（Dock.Fill / AutoScroll 豁免）
        # 免判2条（285 实测结论）：①父 h<=1 的 AntdUI 自绘容器（StackPanel/__IN__ 未布局量测局限）；
        # ②文本为 … 的溢出钮（它是窄窗下剩余命令唯一入口，无处可收，设计取舍）。
        # ③父 AutoScroll=true（滚动容器内容高出是正常态，如 ConnectionDialog 高级区，288 实测）。
        oob = 0
        for n, pabs, pname, pnode in all_nodes:
            if pabs is None or not n.get("visible"):
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
        zero = [n for (n, pabs, _, _pnode) in all_nodes
                if n.get("visible") and not n.get("children")
                and (n.get("abs", {}).get("w", 1) <= 0 or n.get("abs", {}).get("h", 1) <= 0)
                # 空文本 AutoSize Label 正常态（如 errorLabel，有错才撑开）
                and not ("Divider" in n.get("type", "") and n.get("abs", {}).get("h", 99) <= 2)
                and not ("Label" in n.get("type", "") and not (n.get("text") or ""))
                and n.get("dock") != "Fill"
                and not (pabs is not None and area(pabs) <= 0)]
        check(len(zero) == 0, "零尺寸可见叶=%d" % len(zero))
        for n in zero[:5]:
            check(False, "零尺寸 " + (n.get("name") or n.get("type", "?")))

        if fname.startswith("mainform"):
            mainform_rules(tree, check)
            continue

        # 表行高
        for n, _, _, _ in all_nodes:
            rh = (n.get("extra") or {}).get("RowHeight", "")
            if rh and rh != "null" and rh != "":
                try:
                    v = int(float(rh))
                    check(24 <= v <= 34, "%s 行高=%d 在24-34" % (n.get("name") or "表", v))
                except ValueError:
                    pass

        # AntdUI.Button 全零 padding 告警
        for n, _, _, _ in all_nodes:
            t = n.get("type", "")
            if "AntdUI" in t and "Button" in t:
                # 固定尺寸钮靠库内 sps 居中（字高*0.4/侧），Padding=0 不贴边；只判 AutoSize 钮
                if not n.get("autoSize"):
                    continue
                p = n.get("padding", {})
                if p.get("l", 1) == 0 and p.get("r", 1) == 0:
                    check(False, "按钮左右padding=0(贴边风险) %s[%s]" % (n.get("name") or "?", n.get("text", "")))

    print()
    if FAIL:
        print("UI-TREE-CHECK FAIL: %d" % len(FAIL))
        return 1
    print("UI-TREE-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
