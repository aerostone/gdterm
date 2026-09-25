#!/usr/bin/env python3
"""包 G(2026-09-25-hotkey-ssot-unlock-guide)RED/GREEN 驱动。零依赖。"""
import sys, io, os

checks = []
def check(ok, what):
    checks.append((ok, what))
    print(("  ok: " if ok else "  FAIL: ") + what)

def read(p, sig=True):
    enc = "utf-8-sig" if sig else "utf-8"
    return io.open(p, "r", encoding=enc).read()

ROUTER = "src/Gdterm.UI/Services/MainFormCommandRouter.cs"
LAUNCH = "src/Gdterm.UI/Services/ToolsDialogsLauncher.cs"
OLD_TIP = "src/Gdterm.UI/Controls/SharedMenuTip.cs"
NEW_TIP = "src/Gdterm.UI/Controls/MenuTip.cs"
CSJ = "src/Gdterm.UI/Gdterm.UI.csproj"

router = read(ROUTER)
launch = read(LAUNCH)
green = len(sys.argv) > 1 and sys.argv[1] == "green"

# ---- S1 router 注册表 ----
check("HotkeyEntry" in router or "_registry" in router or "Registry" in router,
      "S1 router 注册表结构存在" + ("" if not green else ""))
check(router.count("(Keys.Control | Keys.Shift | Keys.") >= 7 or "BuildRegistry" in router,
      "S1 表覆盖 ≥7 个 Ctrl+Shift 组合")
if green:
    # 行为等价:表化后显式 if 特例链仍在(Escape/F11/Ctrl+Tab/Alt+Digit)
    for kw in ("Keys.Escape", "Keys.F11", "Keys.Control | Keys.Tab", "Keys.D1"):
        check(kw in router, f"S1 特例链保留 {kw}")

# ---- S2 帮助同源 ----
hardcoded = "Ctrl + Shift + K    快速跳转" in launch
check(not hardcoded if green else hardcoded,
      "S2 launcher 硬编码键位串" + ("已清除" if green else "在位(RED)"))
if green:
    check("HotkeyHelp" in router or "BuildHelpText" in router or "RenderHelp" in router or "HelpText" in router,
          "S2 router 暴露帮助渲染")

# ---- S3 解锁引导 ----
cnt_unlock = launch.count("new KeePassUnlockForm")
if green:
    check(cnt_unlock >= 1, "S3 解锁引导存在(≥1 处)")
    # 三个 Open* 都走 EnsureKeePassUnlocked 之类的公共方法才算收口
    check("EnsureKeePassUnlocked" in launch or "EnsureUnlocked" in launch,
          "S3 三方法收口到公共解锁助手")
    check(launch.count("密码库未解锁") <= 1, "S3 死端提示收敛(≤1 处兜底)")
else:
    check(cnt_unlock == 0, "S3 RED:未解锁尚无引导")

# ---- S4 改名 ----
if green:
    check(os.path.exists(NEW_TIP) and not os.path.exists(OLD_TIP), "S4 SharedMenuTip.cs→MenuTip.cs")
    check("MenuTipExtension" in read(NEW_TIP), "S4 类改名 MenuTipExtension")
    cs = read(CSJ)
    check("MenuTip.cs" in cs and "SharedMenuTip.cs" not in cs, "S4 csproj 更新")
else:
    check(os.path.exists(OLD_TIP) and not os.path.exists(NEW_TIP), "S4 RED:旧名在位")

fails = [w for ok, w in checks if not ok]
print("HOTKEY-SSOT-CHECK", "ALL OK" if not fails else ("FAIL " + str(len(fails))),
      "(" + str(len(checks)) + " checks)")
sys.exit(1 if fails else 0)
