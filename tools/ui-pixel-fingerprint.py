#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""L3 像素指纹：把「画面长什么样」压成可比的数字，进 CI 挡外观回归。

为什么是"指纹"而不是逐像素 diff：逐像素基线要往仓库里塞几十张 PNG（几十 MB），
而且抗不住一像素的渲染差异。这里只记三样东西，都是"变了就说明外观真的变了"的量：
  · 平均亮度与近白/近黑占比  → 主题被换回浅色、整块变白/变黑
  · 主导色（量化到 16 级/通道）及其占比 → 色板 token 被改、某控件换了底色
  · 图像尺寸                 → DPI/布局整体跑偏
基线是几十行 JSON，人可读、可 review、可解释。

用法：
    python3 tools/ui-pixel-fingerprint.py ui-smoke              # 检查（CI 用）
    python3 tools/ui-pixel-fingerprint.py ui-smoke --write      # 重写基线（人工确认后）

基线位置：tools/ui-baseline/<dir>.json
退出码：0 通过，1 有偏差（新增/消失/超阈）。
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import pnglite
except Exception:
    print("ui-pixel-fingerprint: 需要 tools/pnglite.py")
    sys.exit(2)

# 阈值不是拍脑袋定的，是量出来的（2026-11-07）：
#   同一台 CI 镜像上，2 次不同提交之间比较 50 张截图，其中 37 张**字节完全相同**
#   （代码没动过的页面渲染是确定性的），13 张有差异的页面里噪声上限是：
#     平均亮度 0.0020 / 近白 0.0003 / 近黑 0.0018 / 主导色 0.0140
#   而真实的外观变更（进度条从白块改成主题色）是 0.1345 / 0.1688 / 0.1690。
#   两档差了一个数量级，所以阈值取在中间并且更靠近噪声侧：
TOL_MEAN_LUM = 0.008         # 平均亮度（0..1）
TOL_WHITE = 0.003            # 近白占比
TOL_BLACK = 0.004            # 近黑占比
TOL_DOMINANT = 0.03          # 单个主导色占比
NEW_COLOR_MIN = 0.03         # 新出现的颜色占比超过这个就判红（零星抗锯齿不算）


def quant(c):
    """颜色量化到 16 级/通道：抵消抗锯齿与亚像素渲染的细微差别。"""
    return (c >> 4) << 4


def fingerprint(path):
    img = pnglite.load(path)
    n = img.w * img.h
    # 大图隔点采样：指纹是统计量，采样 1/4 像素不影响结论，速度差 4 倍
    step = 1 if n <= 400000 else 2
    hist = {}
    lum_sum = 0.0
    white = black = tot = 0
    px = img.px
    W = img.w
    for y in range(0, img.h, step):
        base = y * W * 4
        for x in range(0, W, step):
            i = base + x * 4
            r, g, b = px[i], px[i + 1], px[i + 2]
            tot += 1
            lum_sum += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0
            if r > 200 and g > 200 and b > 200:
                white += 1
            elif r < 40 and g < 40 and b < 40:
                black += 1
            key = (quant(r), quant(g), quant(b))
            hist[key] = hist.get(key, 0) + 1
    dominant = {}
    for k, v in sorted(hist.items(), key=lambda kv: -kv[1])[:12]:
        share = v / float(tot)
        if share >= 0.005:
            dominant["#%02X%02X%02X" % k] = round(share, 3)
    return {
        "w": img.w,
        "h": img.h,
        "meanLum": round(lum_sum / max(tot, 1), 4),
        "whiteShare": round(white / float(max(tot, 1)), 4),
        "blackShare": round(black / float(max(tot, 1)), 4),
        "dominant": dominant,
    }


def build(d):
    out = {}
    for fn in sorted(os.listdir(d)):
        if not fn.endswith(".png"):
            continue
        try:
            out[fn[:-4]] = fingerprint(os.path.join(d, fn))
        except Exception as ex:
            print("  ! %s 读取失败：%s" % (fn, ex))
    return out


def compare(name, cur, base):
    bad = []
    if cur["w"] != base["w"] or cur["h"] != base["h"]:
        bad.append("尺寸 %dx%d → %dx%d" % (base["w"], base["h"], cur["w"], cur["h"]))
    if abs(cur["meanLum"] - base["meanLum"]) > TOL_MEAN_LUM:
        bad.append("平均亮度 %.3f → %.3f" % (base["meanLum"], cur["meanLum"]))
    if abs(cur["whiteShare"] - base["whiteShare"]) > TOL_WHITE:
        bad.append("近白占比 %.3f → %.3f" % (base["whiteShare"], cur["whiteShare"]))
    if abs(cur["blackShare"] - base["blackShare"]) > TOL_BLACK:
        bad.append("近黑占比 %.3f → %.3f" % (base["blackShare"], cur["blackShare"]))
    for col, share in base["dominant"].items():
        now = cur["dominant"].get(col, 0.0)
        if abs(now - share) > TOL_DOMINANT:
            bad.append("主导色 %s 占比 %.2f → %.2f" % (col, share, now))
    for col, share in cur["dominant"].items():
        if col not in base["dominant"] and share > NEW_COLOR_MIN:
            bad.append("新主导色 %s 占 %.2f" % (col, share))
    return bad


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    d = args[0] if args else "ui-smoke"
    write = "--write" in sys.argv
    if not os.path.isdir(d):
        print("ui-pixel-fingerprint: 目录不存在 " + d)
        return 2

    cur = build(d)
    if not cur:
        print("ui-pixel-fingerprint: 目录里没有 png — 没有画面证据，不能算通过")
        return 1

    here = os.path.dirname(os.path.abspath(__file__))
    base_path = os.path.join(here, "ui-baseline", os.path.basename(os.path.abspath(d)) + ".json")

    if write:
        os.makedirs(os.path.dirname(base_path), exist_ok=True)
        with open(base_path, "w", encoding="utf-8") as fp:
            json.dump(cur, fp, ensure_ascii=False, indent=1, sort_keys=True)
        print("ui-pixel-fingerprint: 已写基线 %s（%d 张）" % (base_path, len(cur)))
        return 0

    if not os.path.exists(base_path):
        print("ui-pixel-fingerprint: 缺基线 %s —— 用 --write 生成后提交" % base_path)
        return 1

    with open(base_path, encoding="utf-8") as fp:
        base = json.load(fp)

    fails = 0
    for name in sorted(base):
        if name not in cur:
            print("  FAIL: %s 的截图消失了（基线有、这次没有）" % name)
            fails += 1
            continue
        bad = compare(name, cur[name], base[name])
        if bad:
            fails += 1
            print("  FAIL: " + name)
            for b in bad[:5]:
                print("        " + b)
    for name in sorted(set(cur) - set(base)):
        print("  info: 新页面 %s（基线里没有，--write 后纳入）" % name)

    print()
    if fails:
        print("UI-PIXEL-FINGERPRINT: FAIL (%d/%d 张偏离基线)" % (fails, len(base)))
        return 1
    print("UI-PIXEL-FINGERPRINT: ALL OK (%d 张，阈值 亮度%.2f 白%.2f 黑%.2f 色%.2f)" % (
        len(base), TOL_MEAN_LUM, TOL_WHITE, TOL_BLACK, TOL_DOMINANT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
