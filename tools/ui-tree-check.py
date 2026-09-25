#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""全控件树盒模型断言：读 CI 产出的 *.json（UiTreeDumper），查：

R1 触击目标：可见且 enabled 的交互叶，abs 宽高二者同时小于 HIT_MIN(32) 才算失败
   （曾评估 either-dim，会立刻命中主窗 10 个 109-118x25 合法紧凑控件，故否决）；
   Label/Divider/Panel/Splitter 等展示型控件不参与，省略号溢出钮豁免。
R2 焦点链：同一父容器实例内 tabIndex 非零值不得重复（0=WinForms 默认无序，跳过；
   缺 tabIndex 字段的旧 dump 跳过）。
R3 关闭绑定：每窗至少一个名含 Close/Cancel/OK/确定/取消/关闭 的控件；无关闭语义的窗体走前缀豁免。
R4 键盘可达性：交互叶 TabStop=false 数必须为 0（精确短名匹配，见该段注释）；mainform 走独立分支。
R5 停靠遮挡：同父可见兄弟中 Dock=Fill 与边停靠(Top/Bottom/Left/Right)不得相交 ——
   "工具栏/表头盖住表体"类布局 bug 的可测签名；Fill vs Fill 豁免（同格覆盖层靠可见性互斥）。

另含：兄弟重叠（交叠面积 > OVERLAP_MIN(16px²)，过滤包含关系）、子越界（容差 TOL(2px)）、
零尺寸可见叶、表行高 24-34、AntdUI.Button 左右 padding 全 0。

用法: python3 tools/ui-tree-check.py <jsonDir>
退出码 0=全过，1=有失败。
"""
import json
import os
import sys

FAIL = []
TOL = 2          # 越界容差 px
OVERLAP_MIN = 16  # 重叠告警阈值 px²（4x4）
HIT_MIN = 32      # 最小触击目标 px（R1；change 2026-09-24 定稿，44 噪音过大否决）


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


def siblings_overlap(nodes):
    """同父兄弟重叠：只查可见、非包含关系的两两交叠。"""
    vis = [n for (n, _, _) in [(t[0], t[1], t[2]) for t in nodes] if n.get("visible") and area(n.get("abs", {})) > 0]
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
        for n, pabs, pname, pnode, pkey in all_nodes:
            by_parent.setdefault(pkey, []).append((n, pabs, pname))
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
        for n, pabs, pname, pnode, _pkey in all_nodes:
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
        zero = [n for (n, pabs, _, _pnode, _pkey) in all_nodes
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
        for n, _, _, _, _ in all_nodes:
            rh = (n.get("extra") or {}).get("RowHeight", "")
            if rh and rh != "null" and rh != "":
                try:
                    v = int(float(rh))
                    check(24 <= v <= 34, "%s 行高=%d 在24-34" % (n.get("name") or "表", v))
                except ValueError:
                    pass

        # AntdUI.Button 全零 padding 告警
        for n, _, _, _, _ in all_nodes:
            t = n.get("type", "")
            if "AntdUI" in t and "Button" in t:
                # 固定尺寸钮靠库内 sps 居中（字高*0.4/侧），Padding=0 不贴边；只判 AutoSize 钮
                if not n.get("autoSize"):
                    continue
                p = n.get("padding", {})
                if p.get("l", 1) == 0 and p.get("r", 1) == 0:
                    check(False, "按钮左右padding=0(贴边风险) %s[%s]" % (n.get("name") or "?", n.get("text", "")))

        # R1 最小触击目标：可见可交互控件 abs 宽高至少一维 >= HIT_MIN。
        # 口径：visible && enabled && 子节点为空（叶）&& 非线型（Divider h<=2 豁免沿用零尺寸口径）。
        # 免判 R1-1：文本为 … 的溢出钮（窄窗唯一入口，设计取舍，沿用越界免判②）。
        NON_HIT = ("Label", "Divider", "Panel", "Splitter")
        small = 0
        for n, _, _, _, _ in all_nodes:
            if not n.get("visible") or not n.get("enabled", True):
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
            if a.get("w", 0) < HIT_MIN and a.get("h", 0) < HIT_MIN:
                small += 1
                check(False, "触击过小 %s[%s]%dx%d<%d" % (
                    n.get("name") or "?", n.get("type", "").split(".")[-1],
                    a.get("w", 0), a.get("h", 0), HIT_MIN))
        if small == 0:
            check(True, "触击目标=0")

        # R2 焦点链：同父容器 TabStop=true 控件 TabIndex 无重复（缺字段的老 dump 跳过）。
        dup = 0
        by_parent_tab = {}
        for n, _pabs, _pname, _pnode, pkey in all_nodes:
            if not n.get("visible"):
                continue
            if not n.get("tabStop"):
                continue
            if "tabIndex" not in n:
                continue  # 老 dump 无字段：跳过，不断言
            if n.get("tabIndex", 0) == 0:
                continue  # 0=WinForms 默认未排（CI 321 实测：工具栏钮/动态行皆 0，设计即此）
            by_parent_tab.setdefault(pkey, {}).setdefault(n.get("tabIndex"), []).append(n)
        for pname, idxmap in by_parent_tab.items():
            for idx, group in idxmap.items():
                if len(group) > 1:
                    dup += 1
                    check(False, "TabIndex重复 %s 在容器 %s (%d个)" % (
                        idx, pname, len(group)))
        if dup == 0:
            check(True, "焦点链无重复TabIndex")

        # R3 关闭绑定：每窗至少一个关闭路径（名含 Close/Cancel/OK/确定/取消/关闭其一）。
        # 免判 R3-1（2026-09-24 定稿 + CI 320 补 dangerous-cmd 配置页 + CI 321 补 scanner-center 工具窗）。
        if fname.startswith(("transfer-progress", "pwd-generator", "setup-wizard", "dangerous-cmd",
                              "scanner-center")):
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
                       if n.get("visible") and n.get("enabled", True)
                       and (n.get("type") or "").split(".")[-1] in INTER_TYPES
                       and n.get("tabStop") is False]
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
            vis = [c for c in _kids(parent) if c.get("visible")]
            for i in range(len(vis)):
                for j in range(i + 1, len(vis)):
                    a, b = vis[i], vis[j]
                    da, db = a.get("dock"), b.get("dock")
                    if not ((da == "Fill" and db in EDGE) or (db == "Fill" and da in EDGE)):
                        continue
                    ov = intersect(a.get("abs", {}), b.get("abs", {}))
                    if ov > 0:
                        dock_ov += 1
                        check(False, "停靠遮挡 %s[%s](%s) vs %s[%s](%s) 交叠%dpx²" % (
                            _nm(a), a.get("type", "").split(".")[-1], da,
                            _nm(b), b.get("type", "").split(".")[-1], db, ov))
        if dock_ov == 0:
            check(True, "停靠无遮挡=0")

    print()
    if FAIL:
        print("UI-TREE-CHECK FAIL: %d" % len(FAIL))
        return 1
    print("UI-TREE-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
