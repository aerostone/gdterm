#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""编码守卫：全仓文本文件必须是合法 UTF-8，且 BOM 只允许出现在有技术依据的位置。

规则
  R1 文本文件必须能按 UTF-8 严格解码（二进制扩展名除外）。
  R2 BOM 只允许出现在：第三方/内置目录（third_party/、lib/、vendor/），
     从控件树 dumper 逐字节冻结的夹具（.codestable/changes/*/fixtures/*），
     以及 .ps1（见 R3）。
  R3 含非 ASCII 的 .ps1 必须带 BOM：Windows PowerShell 5.1 对无 BOM 脚本
     按系统 ANSI 码页解释，会把中文读成乱码（与 Roslyn 相反，后者对无 BOM
     源码先按 UTF-8 严格解码）。纯 ASCII 的 .ps1 在 ANSI 读法下字节相同，
     故不强制，但带 BOM 也不判错。

用法
  python3 tools/check-encoding.py [root]      # root 默认当前目录
退出码 0 = 通过，1 = 有 FAIL。
仅依赖 stdlib；输出为可 grep 的 ASCII 结构（FAIL <path> : <reason>）。
"""
import os
import subprocess
import sys

BOM = b"\xef\xbb\xbf"

# 二进制/资源扩展名：不参与文本编码检查
BINARY_EXT = {
    ".dll", ".exe", ".pdb", ".lib", ".obj", ".so", ".dylib", ".a",
    ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
    ".zip", ".nupkg", ".jar", ".7z", ".gz", ".tar", ".rar",
    ".pdf", ".woff", ".woff2", ".ttf", ".otf", ".eot",
    ".snk", ".pfx", ".cer", ".der", ".res", ".bin", ".dat", ".class",
}

# BOM 允许的前缀：第三方源码与随包二进制目录
ALLOW_BOM_PREFIX = ("third_party/", "lib/", "vendor/")


def bom_allowed(rel):
    """返回允许带 BOM 的理由，None 表示不允许。"""
    if rel.startswith(ALLOW_BOM_PREFIX):
        return "vendored/bundled"
    # 控件树 dumper 用 Encoding.UTF8 写出，产物天然带 BOM；
    # 这些夹具是逐字节冻结的真实 CI 产物，故保留原样（消费者按 utf-8-sig 读）。
    if rel.startswith(".codestable/changes/") and "/fixtures/" in rel:
        return "CI dump fixture (utf-8-sig 消费)"
    if rel.endswith(".ps1"):
        return "PowerShell 5.1 需声明 UTF-8（.editorconfig 同口径）"
    return None


def list_files(root):
    """优先用 git 跟踪清单；非 git 目录则回退为文件系统遍历。"""
    try:
        out = subprocess.run(
            ["git", "-C", root, "ls-files", "-z"],
            capture_output=True, check=True,
        ).stdout
        files = [p for p in out.decode("utf-8", "surrogateescape").split("\0") if p]
        if files:
            return files, "git"
    except (OSError, subprocess.CalledProcessError):
        pass

    files = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d != ".git"]
        for name in filenames:
            full = os.path.join(dirpath, name)
            files.append(os.path.relpath(full, root).replace(os.sep, "/"))
    return sorted(files), "walk"


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.getcwd()
    files, source = list_files(root)

    fails = []
    checked = skipped = 0

    for rel in files:
        ext = os.path.splitext(rel)[1].lower()
        if ext in BINARY_EXT:
            skipped += 1
            continue

        full = os.path.join(root, rel)
        try:
            raw = open(full, "rb").read()
        except OSError as exc:
            fails.append((rel, "unreadable: %s" % exc.strerror))
            continue

        has_bom = raw.startswith(BOM)
        body = raw[3:] if has_bom else raw
        try:
            text = body.decode("utf-8")
        except UnicodeDecodeError as exc:
            fails.append((rel, "not valid UTF-8: byte 0x%02x at offset %d"
                          % (body[exc.start], exc.start)))
            continue

        checked += 1
        nonascii = any(ord(ch) > 127 for ch in text)

        # R3：含非 ASCII 的 PowerShell 脚本必须声明为 UTF-8（带 BOM）
        if ext == ".ps1" and nonascii:
            if not has_bom:
                fails.append((rel, "PowerShell 5.1 would read this as ANSI: "
                                   "non-ASCII .ps1 without BOM"))
            continue

        # R2：其他文件一律不许带 BOM（白名单除外）
        if has_bom and bom_allowed(rel) is None:
            fails.append((rel, "unexpected UTF-8 BOM (should be plain UTF-8)"))

    print("encoding guard: %d text files checked, %d binary skipped (via %s)"
          % (checked, skipped, source))
    for rel, why in fails:
        print("  FAIL %s : %s" % (rel, why))

    if fails:
        print("ENCODING-CHECK FAIL: %d" % len(fails))
        return 1
    print("ENCODING-CHECK ALL OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
