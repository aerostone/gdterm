#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
CS_CORE 修正偏移解析（依赖 core_diff.py 的权威 dump()，等价于 core_diff.py 默认模式）。

用法：
  python3 core_diff2.py
"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from core_diff import dump, default_files, KEYS, render_cell

if __name__ == '__main__':
    res = {}
    for label, p in default_files().items():
        res[label] = dump(p)

    print('field'.ljust(12), ' '.join('"%s"' % l for l in res))
    for k in KEYS:
        print(k.ljust(12), ' '.join(render_cell(res[l].get(k, '-')).ljust(20)
                                    for l in res))
    print()
    print('digProduct:')
    for l in res:
        print(' ', l, repr(res[l].get('digProduct', '')))
