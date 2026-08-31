#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
解析 MCS Connect Initial：BER domain parameters + GCC user data (TS_UD 块)。

MCS Connect Initial 是 X.224 CR 之后客户端发送的第一个 MCS PDU（[APP 101]，
tag 0x7f），内部含 callingDomainSelector、upwardFlag、target/min/max
三组 DomainParameters（BER SEQUENCE OF INTEGER），最后是 GCC userData
(OCTET STRING)。userData 以 T.124 GCC ConferenceCreateRequest 开头，
H.221 key 'Duca'（客户端）或 'McDn'（服务端）之后是 TS_UD 块序列。

TS_UD 块头：LE type(2) + LE len(2)，len 含 4 字节头本身
=> body = ud[p+4 : p+bl]，下一个块 p += bl

CS_CORE body 布局（230B）：参见 cs_core.py 中的 parse_core()。

用法：
  python3 parse_ci.py            # 打印默认 2 个连接的 CI 结构
  python3 parse_ci.py <file>...  # 打印指定 dump 的 CI 结构
"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from analyze_dump import load_blocks, split_tpkt
from cs_core import TYPES, parse_core


def read_len(data, i):
    """PER length 解码（长形式 0x80-0xff 前导字节数）。"""
    b = data[i]; i += 1
    if b < 0x80:
        return b, i
    n = b & 0x7f
    return int.from_bytes(data[i:i + n], 'big'), i + n


def parse_domain_params(data, name):
    """DomainParameters: SEQUENCE of INTEGERs（BER-ish）。"""
    i = 0
    assert data[i] == 0x30, hex(data[i])
    i += 1
    ln, i = read_len(data, i)
    end = i + ln
    vals = []
    while i < end:
        assert data[i] == 0x02
        i += 1
        iln, i = read_len(data, i)
        v = int.from_bytes(data[i:i + iln], 'big')
        vals.append((iln, v))
        i += iln
    names = ['maxChannelIds', 'maxUserIds', 'tokenIds', 'numPriorities',
             'minThroughput', 'maxHeight', 'maxMCSPDUsize', 'maxDomainIds']
    print('  %s len=%d: ' % (name, ln) + ', '.join(
        '%s=%d(enc %dB)' % (n, v, l) for (l, v), n in zip(vals, names)))
    return end


def ci_user_data(seg):
    """Connect Initial TPKT 帧 -> (userData, calling, called, upward)。"""
    i = 7
    assert seg[i] == 0x7f; i += 1
    assert seg[i] == 0x65; i += 1
    ln, i = read_len(seg, i)
    end = i + ln
    assert seg[i] == 0x04; i += 1
    l, i = read_len(seg, i); calling = seg[i:i + l]; i += l
    assert seg[i] == 0x04; i += 1
    l, i = read_len(seg, i); called = seg[i:i + l]; i += l
    assert seg[i] == 0x01; i += 1
    l, i = read_len(seg, i); up = seg[i]; i += l
    for _ in range(3):
        assert seg[i] == 0x30
        ln2, j = read_len(seg, i + 1)
        i = j + ln2
    assert seg[i] == 0x04
    l, i = read_len(seg, i + 1)
    return seg[i:i + l], calling, called, up


def get_connect_initial(path):
    """从 dump 提取第一个 C2S Connect Initial TPKT 帧。"""
    blocks = load_blocks(path)
    for d, off, data in blocks:
        if d != 'C2S':
            continue
        for pos, seg in split_tpkt(data):
            if seg is None:
                break
            if len(seg) > 30 and seg[7] == 0x7f and seg[8] == 0x65:
                return seg
    return None


if __name__ == '__main__':
    if len(sys.argv) > 1:
        paths = sys.argv[1:]
    else:
        base = '/data/develop/dotnet/gdterm/tmp/rdp-dump/'
        paths = [
            base + 'ver166/rdp-dump-20260831-135754-c61442.hex',
            base + 'rdp-dump-20260831-080357-c57179.hex',
        ]
    for path in paths:
        seg = get_connect_initial(path)
        if seg is None:
            print(os.path.basename(path), 'NO CI')
            continue
        print('== %s Connect Initial (%dB) ==' % (os.path.basename(path), len(seg)))
        ud, calling, called, up = ci_user_data(seg)
        print('  callingDomain=%s calledDomain=%s upward=%d userData=%dB' %
              (calling.hex(), called.hex(), up, len(ud)))
        dpos = ud.find(b'Duca')
        if dpos < 0:
            print('  (no Duca key)')
            continue
        print('  gccKey=%r' % ud[dpos:dpos + 4])
        p = dpos + 4 + 2  # Duca + PER length(81 3a)
        while p + 4 <= len(ud):
            t = int.from_bytes(ud[p:p + 2], 'little')
            bl = int.from_bytes(ud[p + 2:p + 4], 'little')
            if bl < 4 or p + bl > len(ud):
                break
            body = ud[p + 4:p + bl]
            print('  UD 0x%04X (%s) bl=%d body=%dB' % (t, TYPES.get(t, '?'), bl, len(body)))
            if t == 0xC001:
                f = parse_core(body)
                print('    ver=0x%08x w=%d h=%d build=%d earlyCaps=0x%04x '
                      'conn=%d dig=%r' %
                      (f.get('version', 0), f.get('w', 0), f.get('h', 0),
                       f.get('build', 0), f.get('earlyCaps', 0),
                       f.get('connType', 0), f.get('digProduct', '')))
            p += bl
