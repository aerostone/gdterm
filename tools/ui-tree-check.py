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
   96/120dpi 度量差异都会造成个位数像素级告警，先积累基线再翻转成硬门（--textfit-strict）。
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

用法: python3 tools/ui-tree-check.py <jsonDir>
退出码 0=全过，1=有失败。
"""
import json
import os
import sys

FAIL = []
WARN = []
TOL = 2          # 越界容差 px
OVERLAP_MIN = 16  # 重叠告警阈值 px2（4x4）
HIT_MIN = 32      # 最小触击目标 px（R1；change 2026-09-24 定稿，44 噪音过大否决）
TEXTFIT_TOL = 4   # 文本适配容差 px（R6；96/120dpi 度量差异的经验上限）
TEXTFIT_STRICT = "--textfit-strict" in sys.argv


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
    # flag（--textfit-strict 等）与位置参数分离：此前 argv[1] 无条件当目录，
    # flag 放第一位会 FileNotFoundError 崩溃（2026-11-01 复核发现）。
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    d = args[0] if args else "ui-smoke"
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
        scale = dpi / 96.0
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

        # R6 文本适配（审计 C7-1；warn-only = 告警不是阻断门，--textfit-strict 才转硬门）：
        # 历史最高频 bug 是“文字被裁/贴顶/挤成两行”，dump 带真实 GDI 度量后按“所需 > 可用”判。
        # 口径：
        #   - 只判承载标签文本的控件（Button/Label/CheckBox/RadioButton 等），
        #     输入框/表格/列表本身可滚动或自带省略号，纳入会产生大量噪音；
        #   - AutoSize=true 的控件由 WinForms 自己撑开，跳过；
        #   - 老 dump 没有 textSingleW 字段 → 跳过（向后兼容），受检数照实记录。
        TEXTFIT_TYPES = ("Button", "Label", "Checkbox", "CheckBox", "Radio", "RadioButton")
        overflow = 0
        measured = 0
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
                if need_h > avail_h + TEXTFIT_TOL:
                    overflow += 1
                    WARN.append("文字裁切 %s[%s] 需%dpx高 可用%dpx「%s」" % (
                        _nm(n), short, need_h, avail_h, text[:20]))
            else:
                need_w = n.get("textSingleW", 0)
                if need_w > avail_w + TEXTFIT_TOL:
                    overflow += 1
                    WARN.append("文字溢出 %s[%s] 需%dpx宽 可用%dpx「%s」" % (
                        _nm(n), short, need_w, avail_w, text[:20]))
        if measured == 0:
            print("  info: 文本适配受检=0（无 textSingleW 字段或无文本承载控件）")
        elif overflow == 0:
            check(True, "文本适配=0 (受检=%d)" % measured)
        elif TEXTFIT_STRICT:
            for w in WARN[-overflow:]:
                check(False, w)
        else:
            print("  warn: 文本适配候选=%d (受检=%d；warn-only，加 --textfit-strict 转硬门)" % (overflow, measured))
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
                    check(24 <= v <= 34, "%s 行高=%d 在24-34" % (n.get("name") or "表", v))
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
                              "batch-command", "health-monitor", "snippet-search")):
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
        print("UI-TREE-CHECK WARN: %d（文本适配候选，--textfit-strict 时计为失败）" % len(WARN))
    if FAIL:
        print("UI-TREE-CHECK FAIL: %d" % len(FAIL))
        return 1
    print("UI-TREE-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
