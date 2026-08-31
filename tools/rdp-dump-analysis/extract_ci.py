#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 rdp-dump hex 文件提取 MCS Connect Initial TPKT 帧，解析 TS_UD 块。

用法：
  python3 extract_ci.py <hex文件>...
"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from analyze_dump import load_blocks, split_tpkt
from parse_ci import ci_user_data, get_connect_initial
from cs_core import walk_ud, TYPES, parse_core

if __name__ == '__main__':
    for path in sys.argv[1:]:
        seg = get_connect_initial(path)
        if seg is None:
            print(os.path.basename(path), 'NO CI')
            continue
        ud, calling, called, up = ci_user_data(seg)
        print('=' * 80)
        print(os.path.basename(path), 'CI=%dB userData=%dB' % (len(seg), len(ud)))
        for t, body in walk_ud(ud):
            print('  UD 0x%04X %s body=%dB' % (t, TYPES.get(t, '?'), len(body)))
            if t == 0xC001:
                f = parse_core(body)
                print('    ver=0x%08x w=%d h=%d build=%d earlyCaps=0x%04x '
                      'conn=%d dig=%r' %
                      (f.get('version', 0), f.get('w', 0), f.get('h', 0),
                       f.get('build', 0), f.get('earlyCaps', 0),
                       f.get('connType', 0), f.get('digProduct', '')))
