#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
解析 RdpTcpProxy hex dump：按方向分块，解 TPKT/X.224/加密流。

使用示例：
  python3 parse_rdpdump.py tmp/rdp-dump/ver166/rdp-dump-20260831-135754-c61442.hex
"""
import re, sys

def load_blocks(path):
    """返回 [(dir, offset, bytes)]，dir in C2S/S2C。"""
    blocks = []
    cur_dir = None
    cur = None
    offset = None
    hexre = re.compile(r'^([0-9A-F]{2}( [0-9A-F]{2})*)\s*\|')
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            m = re.match(r'^--- (C2S|S2C) offset=(\d+) len=(\d+) ---', line)
            if m:
                if cur is not None:
                    blocks.append((cur_dir, offset, bytes(cur)))
                cur_dir = m.group(1); offset = int(m.group(2)); cur = []
                continue
            m2 = hexre.match(line)
            if m2 and cur is not None:
                cur.extend(int(x, 16) for x in m2.group(1).split(' '))
    if cur is not None:
        blocks.append((cur_dir, offset, bytes(cur)))
    return blocks

def describe_pdu2(data):
    """按 TPKT header 分段描述每个 PDU。"""
    out = []
    pos = 0
    while pos + 4 <= len(data):
        ver = data[pos]; res = data[pos+1]
        if ver != 3:
            out.append(f"pos={pos} 非TPKT(ver={ver}) 剩余{len(data)-pos}字节")
            break
        ln = (data[pos+2] << 8) | data[pos+3]
        if ln < 4 or pos + ln > len(data):
            out.append(f"pos={pos} 长度异常 ln={ln} 剩余{len(data)-pos}")
            break
        seg = data[pos:pos+ln]
        p = pos; pos += ln
        if ln == 11 and seg[5] == 0xD0:
            out.append(f"[{p}] X.224 CC 11B: {seg.hex()}")
        elif seg[4] >= 6 and (seg[5] & 0xF0) == 0xE0:
            tok = seg[11:]
            txt = tok.decode('latin1')
            # 解析 RDP Negotiation Request (8B 在 cookie 行之后)
            end = tok.find(b'\r\n')
            proto = None
            if end >= 0 and len(tok) >= end + 10:
                nego = tok[end+2:end+10]
                if len(nego) >= 5:
                    proto = nego[4]
            out.append(f"[{p}] X.224 CR {ln}B proto=0x{proto:x} token={txt[:60]!r}")
        else:
            out.append(f"[{p}] TPKT ln={ln} (data/encrypt) head={seg[:12].hex()}")
    return out

def main(path):
    blocks = load_blocks(path)
    print(f"# {path}: {len(blocks)} blocks")
    for d, off, data in blocks[:40]:
        tag = 'C→S' if d == 'C2S' else 'S→C'
        print(f"== {tag} off={off} len={len(data)}")
        for l in describe_pdu2(data)[:12]:
            print("   ", l)
    if len(blocks) > 40:
        print(f"   ... 其余 {len(blocks)-40} blocks 省略")

if __name__ == '__main__':
    main(sys.argv[1])