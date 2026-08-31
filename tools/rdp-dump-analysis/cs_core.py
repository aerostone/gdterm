#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
CS_CORE / CS_SEC 权威解析（经 wire dump 实测验证）。

TS_UD 块遍历（已验证）：
  'Duca' H.221 key + 2 字节 PER 长度(81 3a) 之后是 TS_UD 块序列。
  块头 = LE type(2) + LE len(2)，len 含 4 字节头本身
  => body = ud[p+4 : p+bl]，下一个块 p += bl

CS_CORE body 布局（gcc.c gcc_write_client_core_data，body=230B，不含 4B 头）：
  version(4)@0  desktopWidth(2)@4  desktopHeight(2)@6  colorDepth(2)@8
  sas(2)@10  keyboardLayout(4)@12  clientBuild(4)@16  clientName(32)@20
  kbdType(4)@52  kbdSub(4)@56  kbdFn(4)@60  imeFile(64)@64
  postBeta2ColorDepth(2)@128  clientProductID(2)@130  serialNumber(4)@132
  highColorDepth(2)@136  supportedColorDepths(2)@138  earlyCapabilityFlags(2)@140
  clientDigProductId(64)@142  connectionType(1)@206  pad(1)@207
  serverSelectedProtocol(4)@208  desktopPhysicalWidth(4)@212
  desktopPhysicalHeight(4)@216  desktopOrientation(2)@220
  desktopScaleFactor(4)@222  deviceScaleFactor(4)@226
"""
import struct

TYPES = {0xC001: 'core', 0xC002: 'sec', 0xC003: 'net', 0xC004: 'cluster',
         0xC005: 'monitor', 0xC006: 'monExt', 0xC007: 'msgChan',
         0xC008: 'monCount', 0xC009: 'multitr', 0xC00A: 'monV2',
         0xC00C: 'keyboard'}


def walk_ud(ud):
    """userData（Duca 之后含块序列）-> [(type, body)]，body 不含 4B 头。"""
    dpos = ud.find(b'Duca')
    if dpos < 0:
        return []
    p = dpos + 4 + 2  # Duca + PER length(81 3a)
    out = []
    while p + 4 <= len(ud):
        t, bl = struct.unpack('<HH', ud[p:p + 4])
        if bl < 4 or p + bl > len(ud):
            break
        out.append((t, ud[p + 4:p + bl]))  # bl 含 4B 头
        p += bl
    return out


def parse_core(b):
    """CS_CORE body（230B，不含 4B 头）-> 字段 dict。"""
    if len(b) < 212:
        return {}
    LE = lambda o, n: int.from_bytes(b[o:o + n], 'little')
    f = {}
    f['version'] = LE(0, 4)
    f['w'], f['h'], f['depth8bpp'], f['sas'] = LE(4, 2), LE(6, 2), LE(8, 2), LE(10, 2)
    f['kbdLayout'], f['build'] = LE(12, 4), LE(16, 4)
    f['name'] = b[20:52].decode('utf-16-le', errors='replace').rstrip('\x00')
    f['kbdType'], f['kbdSub'], f['kbdFn'] = LE(52, 4), LE(56, 4), LE(60, 4)
    f['postBeta2'], f['prodID'], f['serial'] = LE(128, 2), LE(130, 2), LE(132, 4)
    f['highDepth'], f['supDepth'], f['earlyCaps'] = LE(136, 2), LE(138, 2), LE(140, 2)
    f['digProduct'] = b[142:206].decode('utf-16-le', errors='replace').rstrip('\x00')
    f['connType'] = b[206]
    f['selProto'] = LE(208, 4)
    return f


def parse_sec(b):
    """TS_UD_CS_SEC：encryptionMethods(4)@[0:4] encryptionLevel(4)@[4:8]。
    实测 body=8B，如 '1b00000000000000' => encMethods=0x1b，无 pad 前缀。"""
    if len(b) < 4:
        return {}
    return {'encMethods': int.from_bytes(b[0:4], 'little'),
            'extEnc': int.from_bytes(b[4:8], 'little') if len(b) >= 8 else None}


def parse_cluster(b):
    """TS_UD_CS_CLUSTER：flags(4) sessionId(4)。"""
    if len(b) < 8:
        return {}
    return {'flags': int.from_bytes(b[0:4], 'little'),
            'sessionId': int.from_bytes(b[4:8], 'little')}


def parse_net(b):
    """TS_UD_CS_NETWORK：channelCount(2) pad(2) + channelDef 数组。"""
    if len(b) < 4:
        return {}
    n = int.from_bytes(b[0:2], 'little')
    chans = []
    for c in range(min(n, (len(b) - 4) // 12)):
        o = 4 + c * 12
        name = b[o:o + 8].decode('latin1').rstrip('\x00')
        flags = int.from_bytes(b[o + 8:o + 12], 'little')
        chans.append((name, flags))
    return {'chanCount': n, 'chans': chans}
