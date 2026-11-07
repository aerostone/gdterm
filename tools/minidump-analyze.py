#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""perf-crash.dmp（dbghelp minidump）离线解析——gdterm perf 基准 SEH 取证用。

从 minidump 提取：
  - 异常记录（EXCEPTION_RECORD：code/address/flags）
  - 模块表（把故障地址映射到 模块名 + 模块内 RVA）
  - 故障线程 CONTEXT（rip/rsp/rbp）及 rip 所属模块

只依赖 stdlib。x64 布局（AppVeyor 构建机）。

用法：python3 tools/minidump-analyze.py perf-crash.dmp

x64 结构偏移（微软文档）：
  EXCEPTION_RECORD: code@0 flags@4 rec@8 addr@16 nparams@24 info[15]@36
  MD_MINIDUMP_EXCEPTION_RECORD: size@0 thread@8 然后 EXCEPTION_RECORD
  MD_MINIDUMP_THREAD_INFO: size@0 tid@4 suspend@8 prio@12 cli@16 ctx@28
  CONTEXT(x64): Rbp@0xA0 Rip@0xF8 Rsp@0x100
"""
import struct
import sys

ST_MODULE_LIST = 0x2
ST_THREAD_INFO_LIST = 0x10
ST_EXCEPTION = 0x8


def die(msg):
    sys.exit("minidump-analyze: " + msg)


def main():
    if len(sys.argv) < 2:
        die("用法: minidump-analyze.py <dump>")
    data = open(sys.argv[1], "rb").read()
    if len(data) < 32 or data[:4] != b"MDMP":
        die("不是 minidump（签名非 MDMP）")
    # MINIDUMP_HEADER: sig(4) version(4) nstreams(4) dirRva(USHORT@12) cks(4) ts(4) flags(4) reqVer(8)
    (version, nstreams) = struct.unpack_from("<I I", data, 4)
    (dir_rva,) = struct.unpack_from("<H", data, 12)
    print("version=0x%x streams=%d" % (version, nstreams))

    streams = {}
    for i in range(nstreams):
        off = dir_rva + i * 12   # MINIDUMP_DIRECTORY = 12B: type(4) size(4) rva(4)
        if off + 12 > len(data):
            break
        stype, size, rva = struct.unpack_from("<I I I", data, off)
        streams[stype] = (size, rva)

    def rd(rva, size):
        if rva + size > len(data):
            return None
        return data[rva:rva + size]

    exc_addr = None
    if ST_EXCEPTION in streams:
        size, rva = streams[ST_EXCEPTION]
        raw = rd(rva, size)
        if raw:
            _s, _tid = struct.unpack_from("<I I", raw, 0)
            code, flags, _rec, addr, nparams = struct.unpack_from(
                "<I I Q Q I", raw, 8)
            info = struct.unpack_from("<15I", raw, 36) if size >= 36 + 60 else (0,) * 15
            print("== exception ==")
            print("  code=0x%08x  flags=0x%x" % (code, flags))
            print("  address=0x%x  nparams=%d" % (addr, nparams))
            if nparams:
                print("  info[0]=0x%x  info[1]=0x%x" % (info[0], info[1]))
            exc_addr = addr
        else:
            print("== exception == (流越界)")
    else:
        print("== exception == (无异常流)")

    modules = []
    if ST_MODULE_LIST in streams:
        size, rva = streams[ST_MODULE_LIST]
        raw = rd(rva, size)
        if raw:
            _total, count = struct.unpack_from("<I I", raw, 0)
            for i in range(count):
                e = 8 + i * 24   # MD_MODULE_ENTRY = 24B
                if e + 24 > len(raw):
                    break
                base, img, _cks2, _tds, name_rva = struct.unpack_from(
                    "<Q I I I I", raw, e)
                name = ""
                if name_rva and name_rva < len(raw):
                    np = name_rva
                    end = np
                    while end + 2 <= len(raw) and raw[end:end + 2] != b"\x00\x00":
                        end += 2
                    name = raw[np:min(end, np + 256)].decode("utf-16-le", "replace")
                modules.append((base, img, name))
    modules.sort()
    print("== modules (%d) ==" % len(modules))
    for base, img, name in modules:
        mark = ""
        if exc_addr and base <= exc_addr < base + img:
            mark = "   <<< FAULTING (rva=0x%x)" % (exc_addr - base)
        print("  0x%016x +0x%08x  %s%s" % (base, img, name, mark))

    if ST_THREAD_INFO_LIST in streams:
        size, rva = streams[ST_THREAD_INFO_LIST]
        raw = rd(rva, size)
        if raw:
            _total, count = struct.unpack_from("<I I", raw, 0)
            off = 8
            for i in range(count):
                if off + 28 > len(raw):
                    break
                tsize, tid = struct.unpack_from("<I I", raw, off)
                ctx = off + 28
                if ctx + 0x108 > len(raw):
                    break
                rip = struct.unpack_from("<Q", raw, ctx + 0xF8)[0]
                rsp = struct.unpack_from("<Q", raw, ctx + 0x100)[0]
                rbp = struct.unpack_from("<Q", raw, ctx + 0xA0)[0]
                owner = ""
                for base, img, name in modules:
                    if base <= rip < base + img:
                        owner = "  rip in %s +0x%x" % (name, rip - base)
                        break
                star = "   <<< FAULT RIP" if exc_addr and rip == exc_addr else ""
                print("  thread[%d] tid=%d rip=0x%x rsp=0x%x rbp=0x%x%s%s" % (
                    i, tid, rip, rsp, rbp, owner, star))
                if tsize >= 28:
                    off += tsize
                else:
                    off += 28
    else:
        print("== threads == (无 ThreadInfo 流)")


if __name__ == "__main__":
    main()
