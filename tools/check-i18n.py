#!/usr/bin/env python3
"""L10n 词典门禁：L10n.T/Tf 的中文字面量必须在 en 词典里有条目。

为什么需要这道门（而不是靠人review）：
    "i18n 已完成"是审计里最容易自我欺骗的一条——代码能编译、中文界面一切正常，
    只有切到 English 才发现某句原样是中文。词典是逐批手工补的，`L10n.T("[跳过] ...")`
    这类新写法加进去时没人会记得同步词典，于是缺口只会随时间单调增长。
    本门禁把"新增用户可见文案"和"补英文"绑成同一个动作：漏了就直接 FAIL。

判定口径：
    * 扫描 src/ 下所有 .cs（排除 Gdterm.Tests / obj / bin）；
    * 抽出 L10n.T("...") / L10n.Tf("...") 的**字面量**参数（含中文才检查）；
    * 与 L10n.cs 里 Strings.En 的键集合比对，缺失即报；
    * ALLOWLIST 是"有意保留中文"的白名单（如功能原文：远程脚本片段、AI 提示词），
      每一条都要写理由——白名单本身也会被审计。

用法：
    python tools/check-i18n.py              # 失败退出码 1
    python tools/check-i18n.py --list       # 只列缺口，不判失败（补词典时干活用）
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
L10N = os.path.join(SRC, "Gdterm.Core", "L10n", "L10n.cs")

# 有意不翻译：功能原文（远端脚本 / AI 提示词 / 命令片段），翻译反而错
ALLOWLIST = {
    # 例子（当前为空）：("文件", "原文片段")
}

CJK = re.compile(r"[\u4e00-\u9fff]")
# L10n.T("...") / L10n.Tf("...")：只认单行字面量（拼接由下面的 CONCAT 兜住）
CALL = re.compile(r'L10n\.Tf?\(\s*"((?:[^"\\]|\\.)*)"')
KEY = re.compile(r'\["((?:[^"\\]|\\.)*)"\]\s*=')
SKIP_DIRS = {"obj", "bin", "Gdterm.Tests"}


def rel(path):
    return os.path.relpath(path, ROOT).replace(os.sep, "/")


def read(path):
    with io.open(path, encoding="utf-8-sig", errors="replace") as fp:
        return fp.read()


def dict_keys():
    src = read(L10N)
    return set(KEY.findall(src))


def scan():
    """→ [(相对路径, 行号, 字面量)]"""
    hits = []
    for root, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            if os.path.normpath(path) == os.path.normpath(L10N):
                continue  # 词典自身
            text = read(path)
            for m in CALL.finditer(text):
                lit = m.group(1)
                if not CJK.search(lit):
                    continue
                hits.append((rel(path), text.count("\n", 0, m.start()) + 1, lit))
    return hits


def main(argv):
    list_only = "--list" in argv
    keys = dict_keys()
    missing = []
    for path, line, lit in scan():
        if lit in keys:
            continue
        if (path.split("/")[-1], lit) in ALLOWLIST:
            continue
        missing.append((path, line, lit))

    if not missing:
        print("i18n-gate: OK（所有 L10n.T 中文字面量都有英文条目）")
        return 0

    print("i18n-gate: %d 处中文字面量在 L10n.cs 词典里没有英文条目：" % len(missing))
    for path, line, lit in missing:
        preview = lit.replace("\n", "\\n")
        print("  %s:%d  %s" % (path, line, preview[:110]))
    print("\n补法：在 L10n.cs 的 Strings.En 里加 [\"原文\"] = \"English\"（键必须与代码字面量逐字一致，"
          "含 \\n 转义与尾随空格）。确有理由不译的，加进本文件 ALLOWLIST 并写理由。")
    return 0 if list_only else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
