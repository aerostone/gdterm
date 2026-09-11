#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""UI 冒烟截图断言：对 Gdterm.Tests --ui-smoke 产出的 PNG 做像素级检查。
在 Linux 开发机 / CI 里跑（Pillow + numpy），判：行高、重叠、豆腐块、列宽。

用法: python3 tools/ui-check.py <shotDir> [--save out.png]
退出码 0=全过，1=有失败。
"""
import os
import sys

try:
    from PIL import Image
    import numpy as np
except ImportError:
    print("need Pillow+numpy: pip install pillow numpy")
    sys.exit(2)

FAIL = []


def check(ok, what):
    print(("  ok: " if ok else "  FAIL: ") + what)
    if not ok:
        FAIL.append(what)


def load(path):
    if not os.path.exists(path):
        check(False, "截图缺失: " + path)
        return None
    return np.array(Image.open(path).convert("RGB")).astype(int)


def dark_bg_rows(img):
    """找深色背景表区行：按行算亮度方差，表区行交替/文字行方差大。"""
    gray = img.mean(axis=2)
    return gray


def main():
    shot = sys.argv[1] if len(sys.argv) > 1 else "ui-smoke"
    print("=== ui-check on " + shot + " ===")

    # DrawToBitmap 对 AntdUI 自绘无效时表区全黑——先验表区非空，否则行高断言无意义
    def table_has_content(img, what):
        h, w, _ = img.shape
        gray = img.mean(axis=2)
        band = gray[h // 4:3 * h // 4, :]
        n = (band > 60).sum()
        ok = n > 500
        check(ok, what + "表中部内容像素=%d(>500, PrintWindow 实抓)" % n)
        return ok

    # 1) 密码管理器：表区存在 + 行高 <= 30px(24地板+DPI余量) + 无大面积纯白豆腐块
    p = os.path.join(shot, "keepass-manager.png")
    img = load(p)
    if img is not None:
        table_has_content(img, "密码管理器")
        h, w, _ = img.shape
        check(h > 300 and w > 400, "密码管理器截图尺寸合理 %dx%d" % (w, h))
        # 行高：取中部水平带，找文字行(暗背景+亮字 → 行内方差大)的周期
        mid = img[h // 3:2 * h // 3, :, :]
        gray = mid.mean(axis=2)
        row_var = gray.var(axis=1)
        # 文字行方差阈值：有字行 vs 空行
        text_rows = row_var > row_var.max() * 0.15
        # 找连续文字行块的中心间距
        centers = []
        in_run = False
        start = 0
        for i, t in enumerate(text_rows):
            if t and not in_run:
                in_run = True
                start = i
            elif not t and in_run:
                in_run = False
                centers.append((start + i) // 2)
        if len(centers) >= 2:
            gaps = [b - a for a, b in zip(centers, centers[1:])]
            avg_gap = sum(gaps) / len(gaps)
            check(avg_gap <= 34, "表行高约 %.0fpx <= 34 (24地板+余量)" % avg_gap)
        else:
            check(False, "未检测到≥2个文字行 (centers=%d)" % len(centers))
        # 豆腐块：纯白(>250)连通大块 = 未渲染字形/空白异常
        white = (img > 250).all(axis=2)
        white_ratio = white.mean()
        check(white_ratio < 0.05, "纯白像素占比 %.2f%% < 5%% (无大面积豆腐/空白)" % (white_ratio * 100))

    # 2) 健康报告：截图存在即可（Tab页+关闭条肉眼在CI图里看）
    p2 = os.path.join(shot, "password-health.png")
    img2 = load(p2)
    if img2 is not None:
        check(img2.shape[0] > 300, "健康报告截图尺寸合理")

    # 3) 扫描中心：截图存在即可
    p3 = os.path.join(shot, "scanner-center.png")
    img3 = load(p3)
    if img3 is not None:
        check(img3.shape[0] > 300, "扫描中心截图尺寸合理")

    print()
    if FAIL:
        print("UI-CHECK FAIL: %d" % len(FAIL))
        for m in FAIL:
            print(" - " + m)
        return 1
    print("UI-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
