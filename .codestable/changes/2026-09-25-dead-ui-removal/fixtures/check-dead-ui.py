#!/usr/bin/env python3
"""包 F(2026-09-25-dead-ui-removal)RED/GREEN 驱动。零依赖。"""
import sys, re, io, glob, os

def read(p):
    return io.open(p, "r", encoding="utf-8-sig").read()

checks = []
def check(ok, what):
    checks.append((ok, what))
    print(("  ok: " if ok else "  FAIL: ") + what)

SBP = "src/Gdterm.UI/Controls/SftpBrowserPanel.cs"
SMT = "src/Gdterm.UI/Controls/SharedMenuTip.cs"
FPC = "src/Gdterm.UI/Controls/FilePaneControl.cs"
CSJ = "src/Gdterm.UI/Gdterm.UI.csproj"

phase_green = (sys.argv[1] if len(sys.argv) > 1 else "red") == "green"

# ---- RED 侧:三死码存在(green 模式下断言已删,作为反向) ----
if phase_green:
    check(not os.path.exists(SBP), "GREEN SftpBrowserPanel.cs 已删除")
    check("DarkMenuRenderer" not in read(SMT), "GREEN DarkMenuRenderer 已删除")
    check("PreviewBoxShim" not in read(FPC), "GREEN PreviewBoxShim 已删除")
else:
    check(os.path.exists(SBP), "RED SftpBrowserPanel.cs 存在")
    check("DarkMenuRenderer" in read(SMT), "RED DarkMenuRenderer 存在")
    check("PreviewBoxShim" in read(FPC), "RED PreviewBoxShim 存在")

# ---- GREEN 断言(实现后翻转) ----
def green():
    g = []
    # S1 全仓零引用(排除本包 fixtures 与审计文档)
    blob = ""
    for f in glob.glob("src/**/*.cs", recursive=True):
        blob += io.open(f, encoding="utf-8-sig").read()
    for name in ("SftpBrowserPanel", "PreviewBoxShim", "DarkMenuRenderer"):
        g.append((f"S1 全仓零引用 {name}", name not in blob))
    # S2 csproj
    cs = read(CSJ)
    g.append(("S2 csproj 无 SftpBrowserPanel", "SftpBrowserPanel" not in cs))
    # S3 ToolTipText2 扩展保留 + 使用方
    smt = read(SMT)
    g.append(("S3 SharedMenuTip 保留 ButtonTipExtension/ToolTipText2", "ToolTipText2" in smt))
    bb = io.open("src/Gdterm.UI/Controls/BottomBarPanel.cs", encoding="utf-8-sig").read()
    g.append(("S3 BottomBarPanel.ToolTipText2 使用不回归", "ToolTipText2(" in bb))
    # S4 PreviewBox 本体保留
    fpc = read(FPC)
    g.append(("S4 PreviewBox 本体保留", "internal static class PreviewBox" in fpc))
    g.append(("S4 两处调用点保留", fpc.count("PreviewBox.Show(FindForm()") == 2))
    return g

fails = [w for ok, w in checks if not ok]
# GREEN 判据在 RED 阶段必须全 FAIL,否则说明删除前就有已达标项(自检)
phase = sys.argv[1] if len(sys.argv) > 1 else "red"
if phase == "green":
    for w, ok in green():
        check(ok, w)
else:
    for w, _ in green():
        check(False, w + " (GREEN 判据,实现后应翻绿)")

fails = [w for ok, w in checks if not ok]
print("DEAD-UI-CHECK", "ALL OK" if not fails else ("FAIL " + str(len(fails))),
      "(" + str(len(checks)) + " checks)")
sys.exit(1 if fails else 0)
