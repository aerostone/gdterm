# -*- coding: utf-8 -*-
"""change 2026-09-25-dangerous-cmd-dock-overlap 的本地 RED/GREEN 驱动。

本机无 dotnet/mono（见 attention.md 与包内证据），WinForms 无法编译运行，
故动态证据由 CI 提供；本驱动只做两件本机能做的事：
  ① 用真实 CI 产物冻结的夹具证明 R5 对危险签名仍然红、对修复后几何转绿（规则口径可满足性）；
  ② 断言 C#/Python 源码确实改到位（门挂上、豁免撤销、修饰符可复用）。

用法: python3 .codestable/changes/2026-09-25-dangerous-cmd-dock-overlap/fixtures/check-dock-gate.py
退出码 0=全过，非 0=有失败。
"""
import os
import re
import subprocess
import sys
import tempfile
import shutil

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
FIX = os.path.dirname(os.path.abspath(__file__))
CHECKER = os.path.join(ROOT, "tools", "ui-tree-check.py")
SIM = os.path.join(FIX, "dock-sim.py")

fails = []
oks = []


def ok(cond, what, detail=""):
    (oks if cond else fails).append(what)
    print(("  ok  : " if cond else "  FAIL: ") + what + (("  " + detail) if detail else ""))


def run(cmd):
    p = subprocess.run(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return p.returncode, p.stdout.decode("utf-8", "replace")


def read(rel):
    with open(os.path.join(ROOT, rel), encoding="utf-8-sig") as fh:
        return fh.read()


print("=== S1 规则口径：同一口径对修复前必红、对修复后必绿（两份真实 CI 产物）===")


def only(fixture):
    """把单个夹具放进独立临时目录送检 —— 两份夹具必须分开检，
    否则负向夹具的 3 处失败会混进正向结果里（首版即栽在此）。"""
    d = tempfile.mkdtemp(prefix="dockgate-one-")
    shutil.copy(os.path.join(FIX, fixture), d)
    rc, out = run([sys.executable, CHECKER, d])
    shutil.rmtree(d, ignore_errors=True)
    return rc, out


rc, out = only("dangerous-cmd-prefix.json")
ok(rc == 1, "[负向] CI 323 修复前产物必须 FAIL(exit=1)", "exit=%d" % rc)
ok(out.count("FAIL: 停靠遮挡") == 3, "[负向] R5 命中 3 处（工具栏/白名单/状态条 各压住表体）",
   "命中=%d" % out.count("FAIL: 停靠遮挡"))
for kw, label in (("36800", "工具栏 Top vs 表 Fill 36800px²"),
                  ("129600", "白名单 Bottom vs 表 Fill 129600px²"),
                  ("6812", "状态条 Bottom vs 表 Fill 6812px²")):
    ok(kw in out, "[负向] 交叠面积复核 " + label)
other = [ln for ln in out.splitlines() if ln.startswith("  FAIL:") and "停靠遮挡" not in ln]
ok(not other, "[负向] 同夹具上除 R5 外无其他规则失败", "其他失败=%d" % len(other))

rc, out = only("dangerous-cmd-fixed.json")
ok(rc == 0, "[正向] CI 327 修复后产物必须 ALL OK(exit=0)", "exit=%d" % rc)
ok("停靠无遮挡=0" in out, "[正向] R5 报 0 处遮挡")
ok(not [ln for ln in out.splitlines() if ln.startswith("  FAIL:")], "[正向] 无任何规则失败")

print("=== S2 停靠模型自证 + 修复后几何预测（可证伪）===")
# 注意：SIM 会把预测几何写成 dump 文件，故一律指向临时目录，绝不写进 fixtures/
# （否则 FIX 里多出一份"预测合成物"，与"真实产物夹具"混在一起，且会随模型改动变陈旧）
simtmp = tempfile.mkdtemp(prefix="dockgate-sim-")
try:
    for f in os.listdir(FIX):
        if f.endswith(".json"):
            shutil.copy(os.path.join(FIX, f), simtmp)
    rc, out = run([sys.executable, SIM, simtmp])
finally:
    shutil.rmtree(simtmp, ignore_errors=True)
ok(rc == 0, "模型自证通过（三份真实 dump 逐值复现，含修复前破损几何）", "exit=%d" % rc)
ok("ok    dangerous-cmd @CI323" in out, "自证涵盖 dangerous-cmd 修复前实测几何")
ok("ok    scanner-center @CI321" in out and "ok    scanner-center @CI323" in out,
   "自证涵盖 scanner-center 修复前/后两侧（模型能区分二者）")
m = re.findall(r"(table|toolbar|wlPanel|status)\s+预测 bounds=\((\d+), (\d+), (\d+), (\d+)\)", out)
pred = {k: tuple(int(v) for v in g) for k, *g in m}
ok(pred.get("toolbar") == (0, 0, 800, 46), "预测 工具栏 Top=(0,0,800,46)", str(pred.get("toolbar")))
ok(pred.get("table") == (0, 46, 800, 286), "预测 规则表 Fill=(0,46,800,286)", str(pred.get("table")))
ok(pred.get("wlPanel") == (0, 332, 800, 162), "预测 白名单 Bottom=(0,332,800,162)", str(pred.get("wlPanel")))
ok(pred.get("status") == (0, 494, 262, 26), "预测 状态条 Bottom=(0,494,262,26) 贴最底", str(pred.get("status")))
t, w = pred.get("table"), pred.get("wlPanel")
if t and w:
    ok(t[1] == 46 and t[1] + t[3] == w[1],
       "预测几何自洽：表顶=工具栏底=46、表底=白名单顶=332（与三条边停靠零相交）")

print("=== S3 修复后 dump 过 R5（合成产物，CI 为最终裁判）===")
# 关键：生成目录与受检目录必须分开——若把"修复前"夹具与被检查的"修复后"dump 放在
# 同一目录，ui-tree-check 会把两者一起检，修复前那 3 处遮挡会再次计入（首版即栽在此）。
scratch = tempfile.mkdtemp(prefix="dockgate-gen-")
checkdir = tempfile.mkdtemp(prefix="dockgate-chk-")
try:
    for f in os.listdir(FIX):
        if f.endswith(".json"):
            shutil.copy(os.path.join(FIX, f), scratch)
    rc, out = run([sys.executable, SIM, scratch])
    ok(rc == 0, "由前缀夹具生成修复后 dump")
    ok(os.path.exists(os.path.join(scratch, "dangerous-cmd-postfix.json")), "生成物 dangerous-cmd-postfix.json 存在")
    shutil.copy(os.path.join(scratch, "dangerous-cmd-postfix.json"), checkdir)
    rc, out = run([sys.executable, CHECKER, checkdir])
    ok("停靠无遮挡=0" in out, "修复后几何 R5 报 0 处遮挡")
    ok("ALL OK" in out and rc == 0, "修复后几何整检 ALL OK(exit=0)", "exit=%d" % rc)
finally:
    shutil.rmtree(scratch, ignore_errors=True)
    shutil.rmtree(checkdir, ignore_errors=True)

print("=== S3b 工具文件字节卫生（本仓 BOM 反复咬人的地方）===")
raw_chk = open(CHECKER, "rb").read()
ok(not raw_chk.startswith(b"\xef\xbb\xbf"),
   "tools/ui-tree-check.py 无 BOM（BOM 在 #! 之前会让 ./tools/ui-tree-check.py 直接执行失败）")
ok(raw_chk.startswith(b"#!/usr/bin/env python3"), "shebang 位于文件第 0 字节")
ok(b"\r\n" not in raw_chk, "LF 换行，未混入 CRLF")
try:
    import ast as _ast
    _ast.parse(open(CHECKER, encoding="utf-8").read())
    ok(True, "tools/ui-tree-check.py 语法可解析")
except SyntaxError as e:
    ok(False, "tools/ui-tree-check.py 语法可解析", str(e))

print("=== S4 源码落点（静态断言，动态由 CI 实测仲裁）===")
chk = read("tools/ui-tree-check.py")
ok("停靠遮挡免判(dangerous-cmd" not in chk, "R5 的危险命令前缀豁免已撤销（规则不再对已修缺陷失灵）")
ok('"dangerous-cmd"' in chk, "R3 关闭绑定豁免仍保留 dangerous-cmd（无关闭语义属设计）")
ok("EDGE = (\"Top\", \"Bottom\", \"Left\", \"Right\")" in chk, "R5 规则仍在（非被整体删除）")
ok("Fill vs Fill" in chk or "Fill 与 Fill" in chk, "R5 明确豁免 Fill vs Fill（同格覆盖层）")

dlg = read("src/Gdterm.Tests/Ui/DialogsSmoke.cs")
run_ = read("src/Gdterm.Tests/Ui/UiSmokeRunner.cs")
ok(dlg.count("UiSmokeRunner.AssertNoDockOverlap(") == 2,
   "DialogsSmoke 两处挂门（Show 全量对话框 + ShowConnectionVariants）",
   "count=%d" % dlg.count("UiSmokeRunner.AssertNoDockOverlap("))
ok("internal static void AssertNoDockOverlap" in run_, "AssertNoDockOverlap 提升为 internal static 可复用")
ok(run_.count("AssertNoDockOverlap(") == 5 + 0 or run_.count("AssertNoDockOverlap(") >= 5,
   "UiSmokeRunner 内定义+4 个契约内调用点仍在", "count=%d" % run_.count("AssertNoDockOverlap("))
i_dump = dlg.find('File.WriteAllText(Path.Combine(outDir, name + ".json")')
i_gate = dlg.find("UiSmokeRunner.AssertNoDockOverlap(")
ok(i_dump != -1 and i_gate != -1 and i_dump < i_gate,
   "门在 dump 写出之后（失败时仍留产物可诊断）")

src = read("src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs")
order = re.findall(r"Controls\.Add\((_ruleTable|toolbar|wlPanel|_statusLabel)\)", src)
ok(order == ["_ruleTable", "toolbar", "wlPanel", "_statusLabel"],
   "装配顺序改为 Fill 最先（Fill 最低索引）", "顺序=" + ",".join(order))
ok("原注释\"Fill 必须最后添加\"已被真实 dump 几何证伪" in src,
   "就地留注释说明为何不能改回（防回归）")

csproj = read("src/Gdterm.Tests/Gdterm.Tests.csproj")
ok("FlaUI" not in csproj and "WinAppDriver" not in csproj and "TestStack" not in csproj,
   "零新依赖：未引入 FlaUI/WinAppDriver/TestStack")

gen = sorted(f for f in os.listdir(FIX) if f.endswith(".json"))
ok(gen == ["dangerous-cmd-fixed.json", "dangerous-cmd-prefix.json"],
   "驱动未向 fixtures/ 写入生成物（只留两份真实产物夹具）", "实际=" + ",".join(gen))

print()
print("DOCK-GATE-CHECK %s  (%d 项检查，%d 失败)" %
      ("ALL OK" if not fails else "FAIL " + str(len(fails)), len(oks) + len(fails), len(fails)))
for f in fails:
    print("  - " + f)
sys.exit(1 if fails else 0)
