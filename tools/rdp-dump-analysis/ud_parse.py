#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TS_UD 块遍历：输出每个连接的 TS_UD 块类型/长度及 CS_SEC / CS_CLUSTER /
CS_NETWORK 关键字段。用于验证 redirect 重连时 CS_CLUSTER flags/sessionId
变化是否与 mstsc 一致。

用法：
  python3 ud_parse.py                    # 默认对比 ver166 + mstsc 黄金样本
  python3 ud_parse.py <hex文件>...       # 指定 dump

依赖 cs_core.py 的权威解析。
"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from analyze_dump import load_blocks, split_tpkt
from parse_ci import read_len, get_connect_initial, ci_user_data
from cs_core import walk_ud, parse_sec, parse_cluster, parse_net, TYPES


def parse(path):
    """返回 [(type, name, body)]，body 不含 4B 头。"""
    seg = get_connect_initial(path)
    if seg is None:
        return []
    ud, _c, _d, _u = ci_user_data(seg)
    return [(t, TYPES.get(t, '%04X' % t), body) for t, body in walk_ud(ud)]


def default_files():
    base = '/data/develop/dotnet/gdterm/tmp/rdp-dump/'
    return [
        ('first ACC',       base + 'ver166/rdp-dump-20260831-135754-c61442.hex'),
        ('token KICKED',    base + 'ver166/rdp-dump-20260831-135811-c61501.hex'),
        ('tokenless retry', base + 'ver166/rdp-dump-20260831-135812-c61505.hex'),
        ('mstsc token ACC', base + 'rdp-dump-20260831-080357-c57179.hex'),
        ('mstsc first',     base + 'rdp-dump-20260831-080345-c57136.hex'),
    ]


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
        sec = d.get(0xC002)
        cl = d.get(0xC004)
        net = d.get(0xC003)
        if sec:
            s = parse_sec(sec)
            print('   sec: encMethods=0x%08x ext=0x%x' %
                  (s.get('encMethods', 0), s.get('extEnc') or 0))
        if cl:
            c = parse_cluster(cl)
            print('   cluster: flags=0x%08x sessionId=%d' %
                  (c.get('flags', 0), c.get('sessionId', 0)))
        if net:
            n = parse_net(net)
            print('   net: chanCount=%d' % n.get('chanCount', 0))
            for name, fl in n.get('chans', []):
                print('      %s: 0x%08x' % (name, fl))
