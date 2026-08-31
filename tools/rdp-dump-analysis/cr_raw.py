#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
打印每个 dump 中 X.224 Connection Request 的完整原始字节，
含 token/cookie 文本（NSFVERIFYHASH 等），用于字节级比对。

用法：
  python3 cr_raw.py <hex文件>...
"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from analyze_dump import load_blocks

for p in sys.argv[1:]:
    for d, off, data in load_blocks(p):
        if d == 'C2S' and len(data) >= 11 and data[5] & 0xF0 == 0xE0:
            print(p.split('/')[-1], 'CR len', len(data))
            print(' full:', data.hex())
            print(' cookie+tail:', data[11:].hex())
            print(' as text:', data[11:].decode('latin1').replace('\r', '<CR>')
                  .replace('\n', '<LF>'))
            break
