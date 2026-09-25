#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""2026-09-25-ci-tree-gate 本地驱动（零依赖，stdlib）。

本机无 dotnet / 无 PowerShell，"门是否真的被执行"只能由真实 CI 仲裁；
可在本地判定的部分有：
  S1 校验器全文可 GBK 编码（cp936 管道不再 UnicodeEncodeError）
  S2 改字符未改变口径（对 CI 327 全部真实 dump 仍 ALL OK）
  S3 appveyor.yml 的门静态结构（位置 / 非零 throw / 解释器解析 / 不静默跳过 / 编码环境）
  S4 字节卫生 + 零依赖 + 未越界改别的文件
用法: python3 fixtures/check-ci-gate.py [ciDumpDir]
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
CHECKER = os.path.join(ROOT, "tools", "ui-tree-check.py")
UICHECK = os.path.join(ROOT, "tools", "ui-check.py")
YML = os.path.join(ROOT, "appveyor.yml")
DUMPS = sys.argv[1] if len(sys.argv) > 1 else "/tmp/ci327"

fails = []


def ok(cond, what, detail=""):
    if cond:
        print("  ok  : %s%s" % (what, ("  " + detail) if detail else ""))
    else:
        print("  FAIL: %s%s" % (what, ("  " + detail) if detail else ""))
        fails.append(what)


def nongbk(path):
    """返回该文件中无法用 GBK 编码的字符统计。"""
    s = open(path, encoding="utf-8").read()
    bad = {}
    for ch in set(s):
        try:
            ch.encode("gbk")
        except UnicodeEncodeError:
            bad[ch] = bad.get(ch, 0) + s.count(ch)
    return bad


print("=== S1 校验器全文可 GBK 编码（cp936 管道安全）===")
bad = nongbk(CHECKER)
ok(not bad, "tools/ui-tree-check.py 无非 GBK 字符", "实际=" + repr(bad) if bad else "0 个")

# 断言本身的牙齿：拿一段确定含 '²' 的文本喂同一函数，必须报非空
# （用 tempfile，不往 fixtures/ 里落临时物）
import tempfile
fd, tmp = tempfile.mkstemp(suffix=".txt")
os.close(fd)
open(tmp, "w", encoding="utf-8").write("面积 100px\u00b2\n")
ok(bool(nongbk(tmp)), "S1 断言有牙齿（含 '²' 的样本必须被判非 GBK 可编码）",
   "探针=" + repr(nongbk(tmp)))
os.remove(tmp)
ok(not [f for f in os.listdir(HERE) if f.endswith(".txt")], "驱动未在 fixtures/ 留下临时物")

print("=== S2 改字符未改变口径（真实 dump 全绿）===")
rc, out = 1, ""
if os.path.isdir(DUMPS):
    r = subprocess.run([sys.executable, CHECKER, DUMPS], capture_output=True, text=True)
    rc, out = r.returncode, (r.stdout or "") + (r.stderr or "")
    n = len([f for f in os.listdir(DUMPS) if f.endswith(".json")])
    ok(rc == 0, "对 CI 327 全部 dump 仍 ALL OK", "dump=%d exit=%d" % (n, rc))
    ok("UI-TREE-CHECK ALL OK" in out, "汇总行为 ALL OK")
else:
    ok(False, "找到真实 dump 目录", DUMPS)

print("=== S3 appveyor.yml 门结构 ===")
yml = open(YML, encoding="utf-8").read()
lines = yml.splitlines()


def idx(sub):
    for i, l in enumerate(lines):
        if sub in l:
            return i
    return -1


i_upload = max(idx("Push-AppveyorArtifact"), idx("Push-AppveyorArtifact"))
i_gate = idx("ui-tree-check.py")
ok(i_gate > 0, "test_script 内出现 tools/ui-tree-check.py 调用")
ok(i_gate > i_upload, "门在产物上传之后（门红了仍留证据可诊断）",
   "上传行=%d 门行=%d" % (i_upload, i_gate))
ok("Get-Command python" in yml, "解析 python 解释器")
ok('"-3"' in yml or "'-3'" in yml, "含 py -3 回退")
ok("python not found" in yml and "throw" in yml, "解释器缺失时 throw（拒绝静默跳过）")
ok("PYTHONIOENCODING" in yml and "PYTHONUTF8" in yml, "设 PYTHONIOENCODING=utf-8 / PYTHONUTF8=1")
ok("$LASTEXITCODE -ne 0" in yml and yml.count("throw") >= 3, "非零退出即 throw")
# 门不得放在 after_test（打包段），否则失败归因会与打包纠缠
i_after = idx("after_test:")
ok(0 < i_gate < i_after if i_after > 0 else False, "门位于 test_script，未侵入 after_test 打包段",
   "after_test 行=%d" % i_after)

print("=== S4 字节卫生 + 零依赖 + 未越界 ===")
raw = open(CHECKER, "rb").read()
ok(not raw.startswith(b"\xef\xbb\xbf"), "tools/ui-tree-check.py 无 BOM")
ok(raw.startswith(b"#!/usr/bin/env python3"), "shebang 在第 0 字节")
ok(b"\r\n" not in raw, "LF 换行")
import ast  # noqa: E402
try:
    ast.parse(raw.decode("utf-8"))
    ok(True, "语法可解析")
except SyntaxError as e:
    ok(False, "语法可解析", str(e))
mods = sorted({n.name.split(".")[0] for n in ast.walk(ast.parse(raw.decode("utf-8")))
               if isinstance(n, ast.Import) for n in n.names} |
              {n.module.split(".")[0] for n in ast.walk(ast.parse(raw.decode("utf-8")))
               if isinstance(n, ast.ImportFrom) and n.module})
ok(set(mods) <= {"json", "os", "sys"}, "校验器仍零第三方依赖", "imports=" + ",".join(mods))

diff = subprocess.run(["git", "diff", "--name-only", "HEAD"], cwd=ROOT,
                      capture_output=True, text=True).stdout.split()
ok(not [f for f in diff if f.startswith("src/")], "未触碰 src/ 下任何文件", "实际=" + ",".join(diff))
ok("tools/ui-check.py" not in diff, "未改动 tools/ui-check.py（保持审查者手动跑的定位）")
ok(all(f in ("appveyor.yml", "tools/ui-tree-check.py",
             ".codestable/.runtime/current-package") for f in diff),
   "改动范围仅限契约内文件", "实际=" + ",".join(diff) if diff else "（干净）")

print()
if fails:
    print("CI-GATE-CHECK FAIL %d  (共检查项见上)" % len(fails))
    for f in fails:
        print("  - " + f)
    sys.exit(1)
print("CI-GATE-CHECK ALL OK")
