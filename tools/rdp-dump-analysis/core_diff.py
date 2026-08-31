#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
CS_CORE 全字段对比表（跨连接）。

用法：
  python3 core_diff.py                     # 默认对比 ver166 + mstsc 黄金样本
  python3 core_diff.py <hex文件>...        # 输出指定 dump 的核心字段

依赖 cs_core.py 的权威解析（TS_UD 块遍历 + CS_CORE 字段偏移均经 wire dump 实测验证）。
"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from analyze_dump import load_blocks, split_tpkt
from parse_ci import read_len, get_connect_initial
from cs_core import walk_ud, parse_core, parse_sec, parse_cluster, parse_net, TYPES


def get_ud_blocks(path):
    """从 Connect Initial 提取 TS_UD 块 dict {type: body}（body 不含 4B 头）。"""
    seg = get_connect_initial(path)
    if seg is None:
        return []
    from parse_ci import ci_user_data
    ud, _c, _d, _u = ci_user_data(seg)
    return walk_ud(ud)


def dump(path):
    """返回字段 dict。"""
    uds = get_ud_blocks(path)
    d = {}
    for t, body in uds:
        if t == 0xC001:
            d.update(parse_core(body))
        if t == 0xC002:
            d.update(parse_sec(body))
        if t == 0xC004:
            d.update(parse_cluster(body))
        if t == 0xC003:
            d.update(parse_net(body))
    d['udTypes'] = ','.join(TYPES.get(t, '%04X' % t) for t, _ in uds)
    return d


_DEFAULT_BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                             '..', '..', 'tmp', 'rdp-dump')


def default_files():
    base = _DEFAULT_BASE
    return {
        'v166-first(c61442)':       os.path.join(base, 'ver166', 'rdp-dump-20260831-135754-c61442.hex'),
        'v166-tokenKICKED(c61501)': os.path.join(base, 'ver166', 'rdp-dump-20260831-135811-c61501.hex'),
        'v166-tokenless(c61505)':   os.path.join(base, 'ver166', 'rdp-dump-20260831-135812-c61505.hex'),
        'mstsc-GOLD-first(c57136)': os.path.join(base, 'rdp-dump-20260831-080345-c57136.hex'),
        'mstsc-GOLD-token(c57179)': os.path.join(base, 'rdp-dump-20260831-080357-c57179.hex'),
    }


KEYS = ['version', 'w', 'h', 'build', 'name', 'kbdLayout', 'kbdType',
        'postBeta2', 'prodID', 'serial', 'highDepth', 'supDepth',
        'earlyCaps', 'digProduct', 'connType', 'selProto',
        'encMethods', 'flags', 'sessionId', 'chanCount', 'udTypes']


def render_cell(v):
    if isinstance(v, list):
        return ' '.join('%s:0x%08x' % (n, f) for n, f in v)
    if isinstance(v, str):
        return v if len(v) < 20 else v[:18] + '..'
    return str(v) if v is not None else '-'


if __name__ == '__main__':
    if len(sys.argv) > 1:
        # 指定文件模式：单文件详细输出
        for p in sys.argv[1:]:
            d = dump(p)
            print(os.path.basename(p))
            for k in KEYS:
                v = d.get(k)
                if v is not None:
                    print('  %-12s %s' % (k, render_cell(v)))
        sys.exit(0)

    allf = {}
    for label, p in default_files().items():
        allf[label] = dump(p)

    hdr = 'field'.ljust(16) + ''.join(l[:22].ljust(24) for l in allf)
    print(hdr)
    for k in KEYS:
        row = k.ljust(16)
        for l in allf:
            row += render_cell(allf[l].get(k)).ljust(24)
        print(row)
