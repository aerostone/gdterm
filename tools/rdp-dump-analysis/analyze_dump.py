#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全量分析 RdpTcpProxy hex dump：按方向分块，解 TPKT/X.224/MCS/DPU。

RdpTcpProxy 生成的 hex dump 格式：
  --- C2S|S2C offset=<num> len=<num> ---
  <hex bytes with ASCII gutter>

本模块提供基础解析函数，被其他分析脚本导入。
"""

import re, sys, os


def load_blocks(path):
    """返回 [(dir, offset, bytes)]，dir in ('C2S','S2C')。"""
    blocks = []
    cur = None
    cur_dir = None
    offset = None
    hexre = re.compile(r'^([0-9A-F]{2}( [0-9A-F]{2})*)\s*\|')
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            # 块分隔行
            m = re.match(r'^--- (C2S|S2C) offset=(\d+) len=(\d+) ---', line)
            if m:
                if cur is not None:
                    blocks.append([cur_dir, offset, bytes(cur)])
                cur_dir = m.group(1)
                offset = int(m.group(2))
                cur = []
                continue
            m2 = hexre.match(line)
            if m2 and cur is not None:
                cur.extend(int(x, 16) for x in m2.group(1).split(' '))
    if cur is not None:
        blocks.append([cur_dir, offset, bytes(cur)])
    return blocks


def split_tpkt(data):
    """按 TPKT 帧拆分，返回 [(pos, seg)]，坏段以 (pos, None) 结尾。"""
    out = []
    pos = 0
    while pos + 4 <= len(data):
        if data[pos] != 3:
            out.append((pos, None))
            break
        ln = (data[pos+2] << 8) | data[pos+3]
        if ln < 4 or pos + ln > len(data):
            out.append((pos, None))
            break
        out.append((pos, data[pos:pos+ln]))
        pos += ln
    return out


def parse_cr(seg):
    """
    X.224 Connection Request: 返回 (token_text, requested_protocol)。
    
    TPKT 帧结构：
      TPKT header (4): 版本, 保留, 长度
      X.224 LI (1): 长度指示
      X.224 CR (1): 0xE0
      dst-ref (2), src-ref (2)
      class/options (1): 0x00
      变长: cookie 行 + CRLF + RDP Negotiation Request (8B)
    
    RDP Negotiation Request:
      type=0x01 (1), flags=0x00 (1), length=0x0008 (2), requestedProtocols=0x00000003 (4)
    """
    li = seg[4]
    var = seg[11:4+li]
    token = ''
    proto = None
    end = var.find(b'\r\n')
    if end >= 0:
        token = var[:end].decode('latin1')
        nego = var[end+2:]
    else:
        nego = var
    if len(nego) >= 5:
        proto = nego[4]
    return token, proto


def parse_mcs(seg):
    """X.224 data PDU → MCS PDU 概要。seg 是完整 TPKT 帧。"""
    if len(seg) < 7:
        return None
    body = seg[7:]  # 跳过 TPKT(4) + X.224 data header(3: 02 f0 80)
    if not body:
        return ('empty', 0, b'')
    tag = body[0]
    if tag == 0x04:
        return ('erectDomain', len(body), b'')
    if tag == 0x28:
        return ('attachUser', len(body), b'')
    if tag == 0x2e:
        return ('attachUserConfirm', len(body), body)
    if tag == 0x38 and len(body) >= 4:
        return ('channelJoin ch=%d' % int.from_bytes(body[1:3], 'big'), len(body), body[1:5])
    if tag == 0x3e:
        return ('channelJoinConfirm', len(body), body[1:7])
    if tag == 0x64 and len(body) >= 6:
        chan = int.from_bytes(body[4:6], 'big')
        return ('sendData ch=%d len=%d' % (chan, len(body)-6), len(body), body[:10])
    if tag == 0x74 and len(body) >= 6:
        chan = int.from_bytes(body[4:6], 'big')
        return ('recvData ch=%d len=%d' % (chan, len(body)-6), len(body), body[:10])
    if tag == 0x21:
        return ('DISCONNECT-PROVIDER-ULTIMATUM reason=0x%02x' %
                (body[1] if len(body) > 1 else 0), len(body), body)
    return ('mcs?tag=0x%02x' % tag, len(body), body[:10])


def describe_pdu(data):
    """按 TPKT header 分段描述每个 PDU，返回字符串列表。"""
    out = []
    pos = 0
    while pos + 4 <= len(data):
        ver = data[pos]
        res = data[pos+1]
        if ver != 3:
            out.append("pos=%d 非TPKT(ver=%d) 剩余%d字节" % (pos, ver, len(data)-pos))
            break
        ln = (data[pos+2] << 8) | data[pos+3]
        if ln < 4 or pos + ln > len(data):
            out.append("pos=%d 长度异常 ln=%d 剩余%d" % (pos, ln, len(data)-pos))
            break
        seg = data[pos:pos+ln]
        p = pos
        pos += ln
        if ln == 11 and seg[5] == 0xD0:
            out.append("[%d] X.224 CC 11B: %s" % (p, seg.hex()))
        elif seg[4] >= 6 and (seg[5] & 0xF0) == 0xE0:
            tok, proto = parse_cr(seg)
            txt = tok.decode('latin1') if isinstance(tok, bytes) else tok
            out.append("[%d] X.224 CR %dB proto=0x%x token=%s" %
                       (p, ln, proto, txt[:60]))
        else:
            out.append("[%d] TPKT ln=%d (data/encrypt) head=%s" %
                       (p, ln, seg[:12].hex()))
    return out


def main(path):
    """命令行入口：打印每个 dump 的 PDU 概要。"""
    blocks = load_blocks(path)
    print("# %s: %d blocks" % (os.path.basename(path), len(blocks)))
    for d, off, data in blocks[:40]:
        tag = 'C→S' if d == 'C2S' else 'S→C'
        print("== %s off=%d len=%d" % (tag, off, len(data)))
        for l in describe_pdu(data)[:12]:
            print("   ", l)
    if len(blocks) > 40:
        print("   ... 其余 %d blocks 省略" % (len(blocks) - 40))


if __name__ == '__main__':
    for p in sys.argv[1:]:
        main(p)