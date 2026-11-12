#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""零依赖 PNG 读取（只做检查器需要的部分）。

为什么不用 Pillow：CI 镜像里没有 Pillow，加一个 pip 依赖只为读几张 PNG 不值当，
而且门禁脚本一律「系统 python3 直接跑」是现有约定（见 tools/README）。
PNG 本体只需 zlib（stdlib 自带），非隔行、8bit、RGB/RGBA 是 Bitmap.Save 的唯一输出。

用法：
    img = pnglite.load("x.png")      # → PngImage(w, h, pixels)  pixels 是 bytearray RGBA
    r, g, b, a = img.rgba(x, y)
"""

import struct
import zlib


class PngImage(object):
    __slots__ = ("w", "h", "px")

    def __init__(self, w, h, px):
        self.w = w
        self.h = h
        self.px = px          # bytearray，每像素 4 字节 RGBA

    def rgba(self, x, y):
        i = (y * self.w + x) * 4
        p = self.px
        return p[i], p[i + 1], p[i + 2], p[i + 3]


def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def load(path):
    """读 8bit 非隔行 RGB/RGBA PNG → PngImage；palette/16bit/隔行一律抛 ValueError。"""
    with open(path, "rb") as fp:
        data = fp.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG: " + path)

    pos = 8
    width = height = depth = ctype = None
    idat = []
    while pos + 8 <= len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctag == b"IHDR":
            width, height, depth, ctype, _comp, _filt, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or ctype not in (2, 6) or interlace != 0:
                raise ValueError("unsupported PNG (depth=%s ctype=%s interlace=%s)" % (depth, ctype, interlace))
        elif ctag == b"IDAT":
            idat.append(body)
        elif ctag == b"IEND":
            break

    if width is None:
        raise ValueError("no IHDR: " + path)

    raw = zlib.decompress(b"".join(idat))
    bpp = 4 if ctype == 6 else 3
    stride = width * bpp
    out = bytearray(width * height * 4)
    prev = bytearray(stride)
    off = 0
    for y in range(height):
        ftype = raw[off]
        off += 1
        line = bytearray(raw[off:off + stride])
        off += stride
        if ftype == 1:                      # Sub
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif ftype == 2:                    # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:                    # Average
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:                    # Paeth
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                c = prev[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + _paeth(a, prev[i], c)) & 0xFF
        elif ftype != 0:
            raise ValueError("bad filter %d at row %d" % (ftype, y))

        base = y * width * 4
        if bpp == 4:
            out[base:base + width * 4] = line
        else:
            for x in range(width):
                s = x * 3
                d = base + x * 4
                out[d] = line[s]
                out[d + 1] = line[s + 1]
                out[d + 2] = line[s + 2]
                out[d + 3] = 255
        prev = line

    return PngImage(width, height, out)
