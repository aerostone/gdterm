#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TS_UD 块遍历 v2（依赖 ud_parse.py，等价输出；保留历史命名避免破坏引用）。

用法：
  python3 ud_parse2.py [hex文件...]
"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from ud_parse import parse, default_files


if __name__ == '__main__':
    if len(sys.argv) > 1:
        files = [(os.path.basename(p), p) for p in sys.argv[1:]]
    else:
        files = default_files()
    for label, p in files:
        blocks = parse(p)
        print('== %s: %s' % (label, ' '.join('%s(%d)' % (n, len(b))
                                             for t, n, b in blocks)))
        d = dict((t, b) for t, n, b in blocks)
        cl = d.get(0xC004)
        net = d.get(0xC003)
        if cl:
            from cs_core import parse_cluster
            c = parse_cluster(cl)
            print('   cluster flags=0x%08x sessionId=%d' %
                  (c.get('flags', 0), c.get('sessionId', 0)))
        if net:
            from cs_core import parse_net
            n = parse_net(net)
            print('   net chanCount=%d' % n.get('chanCount', 0))
            for name, fl in n.get('chans', []):
                print('      %s: 0x%08x' % (name, fl))
