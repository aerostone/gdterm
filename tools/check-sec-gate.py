#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""危险命令闸门的结构门（L0 静态判定）——防止旁路入口重新长回来。

为什么需要这个门
================
2026-11 审计发现：危险命令拦截在 4 个 UI 入口各写了一份
`detector.Check() → new DangerousCommandDialog() → AddToWhitelist()`，
而粘贴 / 宏回放 / 快捷键绑定 / 登录脚本 4 条路**完全没接**——

    一条 `rm -rf /` 手敲一次要点 3 下确认，粘贴进终端却直接执行。

修法不是"再补 4 处 if"，而是把所有入口收敛到 `CommandGate` + `DangerousCommandPrompt`。
但收敛之后**最大的风险是它重新发散**：下一个人要加一个"一键跑命令"的入口，
最快的写法就是 `new DangerousCommandDialog(...)`——编译过、功能好、闸门破了。
机制守不住的东西，用门禁守住。

规则
====
S01 dialog-bypass    `new DangerousCommandDialog(` 只准出现在 DangerousCommandPrompt（唯一出口）
S02 raw-check        `.Check(` 只准出现在 Gdterm.Security（判定只走 CommandGate）
S03 raw-whitelist    UI 里 `detector.AddToWhitelist(cmd)` 只准出现在设置表单（用户显式承认）
S04 unclassified     InputSource 的每个成员都必须至少有一个调用点（新入口必须标来源）

用法
====
    python tools/check-sec-gate.py            # 违规即 exit 1
    python tools/check-sec-gate.py --json out.json
"""
from __future__ import print_function

import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")

SKIP_DIRS = {"bin", "obj", ".vs", "node_modules"}


def iter_cs():
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            if name.endswith(".cs"):
                full = os.path.join(dirpath, name)
                yield full, os.path.relpath(full, ROOT).replace(os.sep, "/")


def read(path):
    with io.open(path, encoding="utf-8-sig", errors="replace") as fp:
        return fp.read()


def hits_for(files, predicate):
    out = []
    for full, rel in files:
        try:
            lines = read(full).split("\n")
        except Exception as ex:  # pragma: no cover - 读取失败本身就是问题
            print("  [read] %s: %s" % (rel, ex))
            continue
        for i, line in enumerate(lines, 1):
            code = line.strip()
            # 注释里写“别这么干”不该被当成这么干
            if code.startswith("//") or code.startswith("*") or code.startswith("/*"):
                continue
            if predicate(rel, line):
                out.append({"file": rel, "line": i, "text": line.strip()[:160]})
    return out


def main(argv):
    json_out = None
    if "--json" in argv:
        json_out = argv[argv.index("--json") + 1]

    files = list(iter_cs())
    violations = []

    def add(rule, title, why, hits):
        for h in hits:
            h["rule"] = rule
            h["title"] = title
            h["why"] = why
            violations.append(h)

    prompt_file = "src/Gdterm.UI/Services/DangerousCommandPrompt.cs"
    dialog_file = "src/Gdterm.UI/Controls/DangerousCommandDialog.cs"
    config_form = "src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs"

    # S01 —— 弹窗只能由统一出口构造
    add("S01", "旁路弹窗",
        "入口必须走 DangerousCommandPrompt.Confirm（判定+弹窗+审计+豁免一次做完）",
        hits_for(files, lambda rel, line: "new DangerousCommandDialog(" in line
                 and rel not in (prompt_file, dialog_file)))

    # S02 —— 判定只能出现在 Security 层
    add("S02", "裸判定",
        "UI/终端层禁止直接调 detector.Check()，一律经 CommandGate.Inspect",
        hits_for(files, lambda rel, line: re.search(r"\b_detector\.Check\(", line) is not None
                 and "/Gdterm.Security/" not in "/" + rel))

    # S03 —— 命令级白名单写入只准在设置表单里（用户显式操作）
    add("S03", "裸白名单写入",
        "运行时豁免请用 CommandGate.ExemptRuleForSession / AddRuleToWhitelist(ruleId)",
        hits_for(files, lambda rel, line: ".AddToWhitelist(" in line
                 and "SecretScan" not in rel
                 and rel not in (config_form, prompt_file)))

    # S04 —— InputSource 每个成员都要有调用点
    enum_path = os.path.join(SRC, "Gdterm.Security", "InputSource.cs")
    if os.path.isfile(enum_path):
        enum_text = read(enum_path)
        body = enum_text.split("public enum InputSource", 1)
        body = body[1].split("}", 1)[0] if len(body) > 1 else ""
        members = re.findall(r"^\s*([A-Z][A-Za-z0-9_]*)\s*=\s*\d+", body, re.M)
        joined = "\n".join(read(f) for f, _ in files)
        for name in members:
            if ("InputSource." + name) not in joined:
                violations.append({
                    "rule": "S04", "title": "未分类的输入来源",
                    "why": "新入口必须标 InputSource，否则拦截日志无法回溯",
                    "file": "src/Gdterm.Security/InputSource.cs", "line": 0,
                    "text": "InputSource.%s 没有任何调用点" % name,
                })

    if violations:
        print("sec-gate: %d 处违规" % len(violations))
        for v in sorted(violations, key=lambda x: (x["rule"], x["file"], x["line"])):
            print("  %s %s:%d  %s" % (v["rule"], v["file"], v["line"], v["text"]))
            print("        → %s" % v["why"])
    else:
        print("sec-gate: OK（闸门无旁路，%d 个源文件）" % len(files))

    if json_out:
        with io.open(json_out, "w", encoding="utf-8", newline="\n") as fp:
            fp.write(json.dumps({"violations": violations}, ensure_ascii=False, indent=2))
        print("json -> %s" % json_out)

    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
