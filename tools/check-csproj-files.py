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
        # XML 良构性：文件列表对得上也可能标签没配对（0.1.374 就是这样：本地门禁全绿，
        # CI 在 nuget restore 阶段 MSB4025 才炸）。先在最便宜的地方拦住。
        try:
            import xml.etree.ElementTree as ET
            ET.fromstring(re.sub(r'&(?!amp;|lt;|gt;|quot;|apos;|#)', '&amp;', text))
        except Exception as ex:
            print("  XML 不良构: %s -> %s（msbuild 会报 MSB4025）" % (proj, ex))
            problems += 1
            continue
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
    problems += check_cross_assembly_internal_types(projects)
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


def project_sources(proj):
    """该工程参与编译的 .cs（按 csproj 的 Compile Include，跳过 MSBuild 生成项）。"""
    base = os.path.dirname(proj)
    text = open(proj, encoding="utf-8-sig", errors="replace").read()
    out = []
    for m in re.finditer(r'<Compile Include="([^"]+)"', text):
        rel = m.group(1).replace("\\", "/")
        if "$(" in rel:
            continue
        out.append(os.path.normpath(os.path.join(base, rel)))
    return out


TYPE_RE = re.compile(r'^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*'
                     r'(?:class|struct|interface|enum|delegate)\s+([A-Za-z_][A-Za-z0-9_]*)', re.M)


def strip_comments_and_strings(text):
    """去掉注释与字符串字面量——只在注释里提到类型名不算使用（否则全是假阳性）。"""
    out = []
    i, n = 0, len(text)
    while i < n:
        two = text[i:i + 2]
        if two == "//":
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif two == "/*":
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        elif text[i] == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
            i += 1
        elif text[i] == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == "\\" else 1
            i += 1
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def friend_assemblies(proj):
    """读工程源码里声明的 InternalsVisibleTo。"""
    friends = set()
    for path in project_sources(proj):
        if not os.path.exists(path):
            continue
        text = open(path, encoding="utf-8-sig", errors="replace").read()
        for m in re.finditer(r'InternalsVisibleTo\s*\(\s*"([^"]+)"', text):
            friends.add(m.group(1).split(",")[0].strip().lower())
    return friends


def check_cross_assembly_internal_types(projects):
    """另一个工程用了本工程的 internal 类型 → 真机 CS0122/CS0012。"""
    internal_types = {}      # 类型名 -> 声明它的工程
    owners = {}
    for proj in projects:
        name = assembly_name(proj)
        owners[name.lower()] = proj
        for path in project_sources(proj):
            if not os.path.exists(path):
                continue
            text = open(path, encoding="utf-8-sig", errors="replace").read()
            for m in TYPE_RE.finditer(text):
                decl_line = m.group(0)
                if "public " in decl_line:
                    continue
                internal_types.setdefault(m.group(1), set()).add(name)

    # 谁被谁授权：工程 -> 允许看它 internal 的程序集名
    grants = {assembly_name(p).lower(): friend_assemblies(p) for p in projects}

    problems = 0
    for proj in projects:
        me = assembly_name(proj)
        for path in project_sources(proj):
            if not os.path.exists(path):
                continue
            text = strip_comments_and_strings(
                open(path, encoding="utf-8-sig", errors="replace").read())
            for tname, declares in internal_types.items():
                if me in declares or len(declares) != 1:
                    continue
                owner = list(declares)[0]
                if me.lower() in grants.get(owner.lower(), set()):
                    continue
                if re.search(r'\b' + re.escape(tname) + r'\b', text) is None:
                    continue
                print("  跨工程使用 internal 类型: %s 用了 %s 的内部类型 %s（真机报 CS0122）"
                      % (path, owner, tname))
                problems += 1
    return problems


if __name__ == "__main__":
    sys.exit(main())
