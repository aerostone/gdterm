#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""csproj 文件清单核对 —— 拦住"本地全绿、CI 直接 CS2001/CS0246"的那一类错。

背景（2026-11-06 CI 0.1.363 复盘）：
  tools/csc-check.sh 按目录枚举 .cs，与 csproj 无关，所以两件事它永远看不见：
    1) 新增文件没登记 → Windows 真机 msbuild 报 CS0246（经典 csproj 无 glob）
    2) 删/改名文件没清登记 → msbuild 报 CS2001 Source file could not be found
  0.1.363 就是第 2 种：SgrNormalizer.cs 已并入 VtStreamPreprocessor.cs，
  csproj 里那条引用仍在，Linux 侧全绿，CI 一进 Gdterm.Terminal 就炸。

规则：
  - 只认 <Compile Include>；含 $( ) 的 MSBuild 生成文件跳过
  - 路径比较不区分大小写（Windows 语义），但存在性按磁盘真实路径判断
  - bin/obj/packages 不参与枚举
  - InternalsVisibleTo 的友元程序集名必须等于目标工程的 AssemblyName（默认=工程名），
    否则真机 msbuild 报 CS0122 而 csc-check（全仓编成一个程序集）永远看不见
    ——0.1.368 就是这条：Gdterm.UI 的 AssemblyName 是 gdterm

退出码：0 无问题；1 有问题（打印明细）。
用法：python3 tools/check-csproj-files.py
"""
import glob
import os
import re
import sys

SKIP_DIRS = {"bin", "obj", "packages", ".git"}


def main():
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    problems = 0
    projects = sorted(glob.glob("src/*/*.csproj"))
    for proj in projects:
        base = os.path.dirname(proj)
        text = open(proj, encoding="utf-8-sig", errors="replace").read()
        listed = set()
        for m in re.finditer(r'<Compile Include="([^"]+)"', text):
            rel = m.group(1).replace("\\", "/")
            if "$(" in rel:
                continue                      # MSBuild 生成的中间文件
            full = os.path.normpath(os.path.join(base, rel))
            listed.add(full.lower())
            if not os.path.exists(full):
                print("  引用但磁盘缺失: %s -> %s（msbuild 会报 CS2001）" % (proj, rel))
                problems += 1
        for root, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for f in files:
                if not f.endswith(".cs"):
                    continue
                full = os.path.normpath(os.path.join(root, f))
                if full.lower() not in listed:
                    print("  未登记到 csproj: %s（msbuild 会报 CS0246）" % full)
                    problems += 1
    problems += check_internals_visible_to(projects)
    print("csproj-file-list: %d 个工程，%d 处问题" % (len(projects), problems))
    return 0 if problems == 0 else 1


def assembly_name(proj):
    """工程的程序集名：<AssemblyName> 优先，缺省=工程文件名。"""
    text = open(proj, encoding="utf-8-sig", errors="replace").read()
    m = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", text)
    return (m.group(1).strip() if m else os.path.splitext(os.path.basename(proj))[0])


def check_internals_visible_to(projects):
    """InternalsVisibleTo 的名字必须是某个真实程序集名，否则 CI 才炸 CS0122。"""
    names = {assembly_name(p).lower() for p in projects}
    problems = 0
    for proj in projects:
        base = os.path.dirname(proj)
        own = assembly_name(proj).lower()
        for root, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for f in files:
                if not f.endswith(".cs"):
                    continue
                path = os.path.join(root, f)
                text = open(path, encoding="utf-8-sig", errors="replace").read()
                for m in re.finditer(r'InternalsVisibleTo\s*\(\s*"([^"]+)"', text):
                    friend = m.group(1).split(",")[0].strip().lower()
                    if friend in names:
                        continue
                    print("  InternalsVisibleTo 找不到对应程序集: %s -> \"%s\"（msbuild 会报 CS0122）"
                          % (path, m.group(1)))
                    problems += 1
                _ = own
    return problems


if __name__ == "__main__":
    sys.exit(main())
