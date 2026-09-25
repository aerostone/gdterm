#!/usr/bin/env python3
"""包 E(2026-09-25-ui-cancel-height-unify)本地 RED/GREEN 驱动。
S1 FieldHeight SSOT;S2 漂移口径清零;S3 生成器取消;S4 向导取消;S5 豁免不回归。
"""
import sys, re, io, glob

def read(p):
    return io.open(p, "r", encoding="utf-8-sig").read()

def has(pat, text):
    return re.search(pat, text) is not None

checks = []
def check(ok, what):
    checks.append((ok, what))
    print(("  ok: " if ok else "  FAIL: ") + what)

FFP = read("src/Gdterm.UI/Services/FormFontPolicy.cs")
GEN = read("src/Gdterm.UI/Forms/PasswordGeneratorForm.cs")
WIZ = read("src/Gdterm.UI/Forms/SetupWizardForm.cs")

# ---- S1: FieldHeight API ----
check(has(r"public static int FieldHeight\(Control c\)", FFP), "S1 FieldHeight(Control) 存在")
check(has(r"FieldHeight[\s\S]{0,300}DpiScale\.V\(c, 38\)", FFP), "S1 公式含 38 地板")
check(has(r"FieldHeight[\s\S]{0,300}RowStep\(c\)", FFP), "S1 公式含 RowStep")

# ---- S2: 漂移口径清零 ----
ALL = ""
for f in glob.glob("src/Gdterm.UI/**/*.cs", recursive=True):
    ALL += io.open(f, encoding="utf-8-sig").read()
check(not has(r"Math\.Max\(DpiScale\.V\(\w+, 38\), FormFontPolicy\.RowStep", ALL)
      or has(r"public static int FieldHeight", FFP) is False,
      "S2 本地 38 公式已被 FieldHeight 取代(或尚无 API 时跳过)")
check(not has(r"DpiScale\.V\(this, 30\), Services\.FormFontPolicy\.RowStep", ALL), "S2 30 地板输入框清零")
check(not has(r"DpiScale\.V\(this, 36\), Gdterm\.UI\.Services\.FormFontPolicy\.RowStep", ALL), "S2 36 地板清零")
check(not has(r"DpiScale\.S\(dialog, 335, fieldH\)", ALL) or "FieldHeight" in read("src/Gdterm.UI/Services/MasterPasswordPrompt.cs"),
      "S2 master 已走 FieldHeight")
check(ALL.count("FormFontPolicy.FieldHeight(") + ALL.count("FieldHeight(this)") + ALL.count("FieldHeight(dialog)") + ALL.count("FieldHeight(form)") + ALL.count("FieldHeight(c)") >= 15, "S2 FieldHeight 调用点 ≥15")
# 锁屏豁免保留
LOCK = read("src/Gdterm.UI/Controls/LockOverlayControl.cs")
check(has(r"DpiScale\.V\(this, 30\)", LOCK), "S5 锁屏 30 地板豁免保留")

# ---- S3: 生成器 ----
check(has(r"AcceptButton\s*=", GEN), "S3 生成器 AcceptButton")
check(has(r"CancelButton\s*=", GEN), "S3 生成器 CancelButton")
check(has(r"Text = " + '"' + "关闭" + '"', GEN), "S3 关闭按钮存在")

# ---- S4: 向导 ----
check(has(r"Text = " + '"' + "取消" + '"', WIZ), "S4 取消按钮存在")
check(has(r"CancelButton\s*=", WIZ), "S4 CancelButton 绑定")
check(has(r"_cancelButton\.Visible = !IsCompleted|IsCompleted[\s\S]{0,120}_cancelButton\.Visible", WIZ), "S4 完成后隐藏取消钮")
check(has(r"_cancelButton\.Click[\s\S]{0,200}Close\(\)", WIZ), "S4 取消钮走 Close(触发既有确认)")

# ---- S5: 豁免清单不回归 ----
QJ = read("src/Gdterm.UI/Controls/ConnectionQuickJumpForm.cs")
SC = read("src/Gdterm.UI/Forms/ScannerCenterForm.cs")
check(not re.search(r"^\s*CancelButton\s*=", QJ, re.M), "S5 QuickJump 保持无 CancelButton 赋值(命令面板豁免)")
check(not re.search(r"^\s*CancelButton\s*=", SC, re.M), "S5 ScannerCenter 保持无 CancelButton 赋值(ESC 守卫豁免)")

fails = [w for ok, w in checks if not ok]
print("CANCEL-HEIGHT-CHECK", "ALL OK" if not fails else ("FAIL " + str(len(fails))),
      "(" + str(len(checks)) + " checks)")
sys.exit(1 if fails else 0)
