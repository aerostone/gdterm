# -*- coding: utf-8 -*-
"""WinForms 停靠顺序模拟器——用于在无法编译的环境下把"改装配顺序"变成可证伪的预测。

反推出的算法（由三份真实 CI dump 几何反推，见下 self-check）：

    remaining = 客户区
    按 Controls 索引 **从大到小** 迭代（索引越大越先分配）：
        不可见                -> 跳过（不消耗 remaining）
        边缘停靠 Top          -> bounds=(remaining.x, remaining.y, remaining.w, 自身 h)
                                 remaining.y += h; remaining.h -= h
        边缘停靠 Bottom       -> bounds=(remaining.x, remaining.y+remaining.h-自身 h, remaining.w, 自身 h)
                                 remaining.h -= h
        Dock=Fill             -> bounds = remaining，且 **不消耗 remaining**

最后一条是这个缺陷类的根因：Fill 拿的是"此刻的 remaining"，而它不缩小 remaining，
所以**任何在它之后才被处理的边缘兄弟都还能拿到正确位置，只是与它重叠**。
因此边缘兄弟必须比 Fill 先被处理 = Fill 必须是**最低索引**（最先 Add）。

本脚本自证：对三份真实 dump 反推出的几何必须与实测逐值相等，否则拒绝给出预测。

用法：python3 dock-sim.py [dumpDir]
"""
import json
import os
import sys


def sim(client_w, client_h, children):
    """children: [(label, dock, own_h, own_w, visible)]，按 Controls 索引升序。

    own_w 为 None 表示宽度吃满 remaining（普通边缘停靠控件）；
    给定则用自身固有宽度（AutoSize 标签实测如此：状态条 w=262 而非 800）。
    """
    rx, ry, rw, rh = 0, 0, client_w, client_h
    out = {}
    for label, dock, own_h, own_w, visible in reversed(children):
        if not visible:
            continue
        if dock == "Fill":
            out[label] = (rx, ry, rw, rh)      # 注意：不消耗 remaining
        elif dock in ("Top", "Bottom"):
            w = rw if own_w is None else own_w
            y = ry if dock == "Top" else ry + rh - own_h
            out[label] = (rx, y, w, own_h)
            if dock == "Top":
                ry += own_h
            rh -= own_h
        else:
            raise ValueError("未建模的停靠：" + str(dock))
    return out


# ---- 自证：模型必须复现三份真实 CI dump 的实测 bounds --------------------------
# 每份 case：(文件名, 客户区, 按 Add 顺序的 [(标签, dock, 自身高, 是否可见)])，
# 期望值从 dump 的 bounds 字段抄录（非 abs，即客户区相对坐标）。
KNOWN = [
    ("dangerous-cmd @CI323（修复前，Fill 在索引 3）",
     800, 520,
     [("status", "Bottom", 26, 262, True),
      ("wlPanel", "Bottom", 162, None, True),
      ("toolbar", "Top", 46, None, True),
      ("table", "Fill", 0, None, True)],
     {"table": (0, 0, 800, 520), "toolbar": (0, 0, 800, 46),
      "wlPanel": (0, 358, 800, 162), "status": (0, 332, 262, 26)}),

    ("scanner-center @CI321（修复前，Fill 在索引 1）",
     960, 640,
     [("toolbar", "Top", 56, None, True),
      ("split", "Fill", 0, None, True),
      ("wmi", "Top", 100, None, False)],
     {"split": (0, 0, 960, 640), "toolbar": (0, 0, 960, 56)}),

    ("scanner-center @CI323（修复后，Fill 在索引 0）",
     960, 640,
     [("split", "Fill", 0, None, True),
      ("wmi", "Top", 100, None, False),
      ("toolbar", "Top", 56, None, True)],
     {"split": (0, 56, 960, 584), "toolbar": (0, 0, 960, 56)}),
]


def self_check():
    bad = 0
    for title, w, h, children, expect in KNOWN:
        got = sim(w, h, children)
        ok = all(tuple(got[k][:len(v)]) == tuple(v) for k, v in expect.items())
        print("  %s  %s" % ("ok  " if ok else "FAIL", title))
        for k, v in expect.items():
            g = got[k]
            if tuple(g[:len(v)]) == tuple(v):
                print("        %-9s 期望(客户区相对)=%-22s 模拟=%s" % (k, v, g))
            else:
                print("        %-9s 期望=%-22s 模拟=%-22s  <-- 模型不成立" % (k, v, g))
                bad += 1
    return bad


# ---- 预测：修复后的 dangerous-cmd --------------------------------------------
PREDICT = [
    ("table", "Fill", 0, None, True),        # 原索引 3 -> 0
    ("toolbar", "Top", 46, None, True),      # 原索引 2 -> 1
    ("wlPanel", "Bottom", 162, None, True),  # 原索引 1 -> 2
    ("status", "Bottom", 26, 262, True),     # 原索引 0 -> 3
]


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "."
    print("=== 停靠模型自证（必须全部 ok，否则预测不可信）===")
    bad = self_check()
    if bad:
        print("\n模型自证失败 %d 处，拒绝输出预测。" % bad)
        return 1

    print("\n=== 预测：DangerousCommandConfigForm 反转 Add 顺序后（客户区 800x520）===")
    got = sim(800, 520, PREDICT)
    for k, v in got.items():
        print("  %-9s 预测 bounds=%s" % (k, v))
    t = got["table"]
    assert t[1] == 46, "规则表顶边应等于工具栏底边"
    assert t[1] + t[3] == 332, "规则表底边应等于白名单面板顶边"
    print("  断言：规则表顶边=工具栏底边=46、底边=白名单顶边=332、高度=286 —— 与三条边停靠均不相交")

    # 把预测几何回写成一份"修复后"合成 dump，交给 ui-tree-check 判定 R5。
    # 说明：合成 dump 只证明"该口径下修复后几何可满足"，不证明 C# 一定对——CI 才是裁判。
    src = None
    for cand in ("dangerous-cmd-prefix.json", "dangerous-cmd.json"):
        if os.path.exists(os.path.join(root, cand)):
            src = os.path.join(root, cand)
            break
    if src is None:
        print("\n  未找到 dangerous-cmd 夹具，跳过合成 dump")
        return 0

    with open(src, encoding="utf-8-sig") as fh:
        tree = json.load(fh)
    kids = tree.get("controls") or tree.get("children") or []

    def pick(c):
        d = c.get("dock")
        h = c.get("bounds", {}).get("h", 0)
        if d == "Fill":
            return "table"
        if d == "Top":
            return "toolbar"
        if d == "Bottom":
            return "wlPanel" if h > 50 else "status"
        raise ValueError("未预期的停靠：" + str(d))

    for c in kids:
        x, y, w, h = got[pick(c)]
        c["bounds"] = {"x": x, "y": y, "w": w, "h": h}
        c["abs"] = {"x": x + 26, "y": y + 26, "w": w, "h": h}
    # 同步成修复后的 Add 顺序（Fill 最低索引），让合成 dump 与真实控件集合顺序一致
    order = ["table", "toolbar", "wlPanel", "status"]
    kids.sort(key=lambda c: order.index(pick(c)))
    tree["controls"] = kids
    out = os.path.join(root, "dangerous-cmd-postfix.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(tree, fh, ensure_ascii=False, indent=1)
    print("\n  已写出合成修复后 dump：%s" % out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
