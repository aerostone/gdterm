#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""编码归一的本地反向驱动（零依赖，仅 stdlib）。

因为开发机没有 dotnet/PowerShell，CI 的真实执行只能由后续 AppVeyor 实测仲裁；
本驱动负责证明以下六件事在本地就是可证伪的：

  S1 守卫本体在仓库上 exit 0
  S2 守卫有牙齿（临时目录合成样本：能报出该报的、放过该放的）
  S3 逐字节不变性（18 个去 BOM 文件 == HEAD blob[3:]；pack-release.ps1 == BOM+HEAD）
  S4 白名单之外 BOM 归零，且仓库 BOM 集合与独立重算的期望集合一致
  S5 .editorconfig 与 appveyor.yml 接线到位
  S6 卫生与反向检查（守卫自身无 BOM/仅 stdlib；fixtures/ 无生成物残留）

运行：python3 .codestable/changes/2026-09-25-utf8-encoding-normalization/fixtures/check-utf8-normalization.py
"""
import hashlib
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[4]      # 仓库根
GUARD = ROOT / "tools" / "check-encoding.py"
BOM = b"\xef\xbb\xbf"
FIXDIR = pathlib.Path(__file__).resolve().parent

fails = []
checks = 0


def check(ok, what):
    global checks
    checks += 1
    if ok:
        print("  ok: %s" % what)
    else:
        print("  FAIL: %s" % what)
        fails.append(what)


def run_guard(root):
    p = subprocess.run([sys.executable, str(GUARD), str(root)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def git(*args):
    return subprocess.run(["git", "-C", str(ROOT)] + list(args),
                          capture_output=True).stdout


def tracked():
    out = git("ls-files", "-z").decode("utf-8", "surrogateescape")
    return [f for f in out.split("\0") if f]


def expected_bom_allowed(rel):
    """独立重算一遍白名单（刻意不复用守卫的函数，做交叉核对）。"""
    if rel.startswith(("third_party/", "lib/", "vendor/")):
        return True
    if rel.startswith(".codestable/changes/") and "/fixtures/" in rel:
        return True
    if rel.endswith(".ps1"):
        return True
    return False


print("== S1 守卫本体 ==")
rc, out = run_guard(ROOT)
check(rc == 0, "仓库上 exit=0（实际 %d）" % rc)
check("ENCODING-CHECK ALL OK" in out, "输出 ALL OK")
m_checked = m_bin = 0
for line in out.splitlines():
    if line.startswith("encoding guard:"):
        parts = line.split()
# 行形如: "encoding guard: 551 text files checked, 16 binary skipped (via git)"
        # （用下标而不再引入 re，以保持 S6 的"仅 stdlib 导入"断言字面成立）
        m_checked, m_bin = int(parts[2]), int(parts[6])
check(m_checked > 500, "文本文件巡检量 >500（实际 %d）" % m_checked)
check(m_bin >= 16, "二进制按扩展名跳过 >=16（实际 %d）" % m_bin)

print("\n== S2 守卫牙齿（临时目录合成样本）==")
tmp = tempfile.mkdtemp(prefix="utf8-teeth-")
try:
    def w(rel, data):
        p = pathlib.Path(tmp) / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)

    w("ok.cs", "using System; // 中文 ✓\n".encode("utf-8"))
    w("bom.md", BOM + "---\ntitle: x\n---\n".encode("utf-8"))
    w("bad.txt", b"head \xc3\x28 tail\n")                    # 非法 UTF-8 续字节
    w("ansi.ps1", "$msg = '中文提示'\n".encode("utf-8"))      # 非 ASCII 且无 BOM
    w("good.ps1", BOM + "$msg = '中文提示'\n".encode("utf-8"))
    w("third_party/x.cs", BOM + "// vendored\n".encode("utf-8"))
    w(".codestable/changes/z/fixtures/f.json", BOM + b"{\"a\":1}")
    w("skip.dll", b"MZ\x90\x00\xff\xfe binary")

    rc, out = run_guard(tmp)
    check(rc == 1, "合成目录 exit=1（实际 %d）" % rc)
    check("via walk" in out, "非 git 目录回退到文件遍历")
    hits = [l for l in out.splitlines() if l.startswith("  FAIL ")]
    check(len(hits) == 3, "恰好 3 处 FAIL（实际 %d）" % len(hits))
    check(any("bom.md" in h for h in hits), "抓到 b.md 类：不该带 BOM 的 .md")
    check(any("bad.txt" in h for h in hits), "抓到非法 UTF-8 字节")
    check(any("ansi.ps1" in h for h in hits), "抓到无 BOM 的非 ASCII .ps1")
    check(not any("good.ps1" in h for h in hits), "放过带 BOM 的非 ASCII .ps1")
    check(not any("third_party" in h for h in hits), "放过 third_party/ 下的 BOM")
    check(not any("fixtures" in h for h in hits), "放过 CI 夹具的 BOM")
    check(not any("skip.dll" in h for h in hits), "按扩展名跳过二进制 .dll")

    # 牙齿是"内容驱动"而非"文件名驱动"：给 ansi.ps1 补 BOM 后该条必须消失
    (pathlib.Path(tmp) / "ansi.ps1").write_bytes(BOM + "$msg = '中文提示'\n".encode("utf-8"))
    rc, out = run_guard(tmp)
    hits2 = [l for l in out.splitlines() if l.startswith("  FAIL ")]
    check(len(hits2) == 2, "补 BOM 后降为 2 处 FAIL（实际 %d）" % len(hits2))
    check(not any("ansi.ps1" in h for h in hits2), "补 BOM 后该条消失（证明按内容判定）")
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print("\n== S3 逐字节不变性（以 HEAD blob 为基准）==")
# 只看工作区：项目自有的 .cs/.csproj 现在都不应带 BOM
still = [r for r in tracked()
         if r.endswith((".cs", ".csproj")) and not r.startswith(("third_party/", "lib/", "vendor/"))
         and (ROOT / r).read_bytes().startswith(BOM)]
check(len(still) == 0, "工作区已无项目自有 .cs/.csproj 带 BOM（剩 %d）" % len(still))

nums = {}
for n in git("diff", "--numstat").decode("utf-8").splitlines():
    if not n.strip():
        continue
    ins, dele, path = n.split("\t")[:3]
    if ".runtime" in path:          # 运行时指针不属于字节归一的范围
        continue
    nums[path] = (ins, dele)

# 字节层改动的自证形式：整个文件只差 1 行（即首行那个 BOM）
byte_only = sorted(p for p, d in nums.items() if d == ("1", "1"))
multi = sorted(p for p, d in nums.items() if d != ("1", "1"))
check(len(byte_only) == 19, "字节层改动（diff 恰为 1/1）应为 19 个（实际 %d）" % len(byte_only))
check(multi == ["appveyor.yml"], "非字节层改动只有 appveyor.yml（实际 %s）" % multi)

bytes_ok, bps_ok = [], None
for rel in byte_only:
    head = git("show", "HEAD:" + rel)
    cur = (ROOT / rel).read_bytes()
    if rel.endswith(".ps1"):
        bps_ok = (cur == BOM + head, rel)
    else:
        bytes_ok.append((rel, cur == head[3:] and not cur.startswith(BOM)))
bad = [r for r, ok in bytes_ok if not ok]
check(len(bytes_ok) == 18, "受控去 BOM 文件 18 个（实际 %d）" % len(bytes_ok))
check(not bad, "18 个文件正文与 HEAD blob[3:] 逐字节相同（坏 %d）" % len(bad))
check(bps_ok is not None and bps_ok[0], "pack-release.ps1 == BOM + HEAD 原字节（%s）"
      % (bps_ok[1] if bps_ok else "未找到"))

print("\n== S4 白名单之外 BOM 归零（包含关系，非相等）==")
# 守卫的规则是"带 BOM 的文件必须被白名单许可"，即 actual ⊆ allowed。
# 反向做一个集合等式是本驱动初稿的错——把"许可"误读成"必须带"。
actual = set()
for rel in tracked():
    try:
        if (ROOT / rel).read_bytes().startswith(BOM):
            actual.add(rel)
    except OSError:
        pass
allowed = set(r for r in tracked() if expected_bom_allowed(r))
violation = sorted(actual - allowed)
check(not violation, "所有带 BOM 的文件都被白名单许可（越界 %d）" % len(violation))
groups = {}
for r in sorted(actual):
    key = ("third_party/" if r.startswith("third_party/") else
           "lib/" if r.startswith("lib/") else
           "vendor/" if r.startswith("vendor/") else
           "fixtures/" if "/fixtures/" in r else
           "ps1" if r.endswith(".ps1") else "!! 未归类")
    groups[key] = groups.get(key, 0) + 1
print("     BOM 存量分布: %s" % ", ".join("%s=%d" % kv for kv in sorted(groups.items())))
print("     白名单许可但未带 BOM: %d 个（许可 ≠ 必须）" % len(allowed - actual))
check("!! 未归类" not in groups, "没有白名单外的 BOM 存量")
check(groups.get("third_party/", 0) >= 60, "third_party/ 上游 BOM 原样保留（%d）"
      % groups.get("third_party/", 0))
check(not any(r.endswith((".cs", ".csproj", ".md", ".json"))
              and not expected_bom_allowed(r) for r in actual),
      "项目自有 .cs/.csproj/.md/.json 全部无 BOM")

print("\n== S5 .editorconfig 与 CI 接线 ==")
ec = (ROOT / ".editorconfig").read_text(encoding="utf-8")
check("root = true" in ec, ".editorconfig 有 root = true")
check("[*.ps1]" in ec and "charset = utf-8-bom" in ec, ".editorconfig 声明 .ps1 带 BOM")
check("charset = utf-8\n" in ec.replace("\r", ""), ".editorconfig 声明文本纯 UTF-8")
check("end_of_line = lf" in ec, ".editorconfig 固化 LF（全仓 532 文件本就 LF）")

yml = (ROOT / "appveyor.yml").read_text(encoding="utf-8")
i_tree = yml.find("tools\\ui-tree-check.py")
i_enc = yml.find("tools\\check-encoding.py")
check(i_tree != -1, "树检门调用仍在")
check(i_enc != -1, "编码门调用已接入")
check(i_tree != -1 and i_enc > i_tree, "编码门排在树检门之后")
check("encoding guard failed exit=" in yml, "编码门非零退出即 throw（不静默跳过）")
check(yml.count("PYTHONIOENCODING") == 1, "复用同一 ps 块的 PYTHONIOENCODING")

print("\n== S6 卫生与反向检查 ==")
graw = GUARD.read_bytes()
check(not graw.startswith(BOM), "守卫自身无 BOM")
check(graw.startswith(b"#!/usr/bin/env python3"), "守卫 shebang 在第 0 字节")
check(b"\r\n" not in graw, "守卫无 CRLF")
imports = sorted(set(l.split()[1] for l in graw.decode("utf-8").splitlines()
                     if l.startswith("import ")))
check(imports == ["os", "subprocess", "sys"], "守卫仅 stdlib 导入（实际 %s）" % imports)
check(subprocess.run([sys.executable, "-c",
                      "import ast;ast.parse(open(r'%s',encoding='utf-8').read())" % GUARD],
                     capture_output=True).returncode == 0, "守卫可被 ast.parse")
leftover = sorted(p.name for p in FIXDIR.iterdir() if p.name != pathlib.Path(__file__).name)
check(not leftover, "fixtures/ 无生成物残留（实际 %s）" % (leftover or "空"))

erc = (ROOT / ".editorconfig").read_bytes()
check(not erc.startswith(BOM) and b"\r\n" not in erc, "新增 .editorconfig 字节卫生")

print("\nUTF8-NORM-CHECK %s over %d checks" %
      ("ALL OK" if not fails else "FAIL %d" % len(fails), checks))
sys.exit(0 if not fails else 1)
