#!/usr/bin/env python3
"""包 D(2026-09-25-ui-fix-audit-findings)本地 RED/GREEN 驱动。
S1 LogonScriptPanel ↑/↓ Click 接线;S2 MasterPasswordPrompt fieldH+CancelButton;
S3 MakeNumber DpiScale.V;S4 反向边界。零依赖,utf-8 容忍 BOM。
"""
import sys, re, io

ROOT = "."
FILES = {
    "logon": "src/Gdterm.UI/Controls/LogonScriptPanel.cs",
    "master": "src/Gdterm.UI/Services/MasterPasswordPrompt.cs",
    "appear": "src/Gdterm.UI/Forms/AppearanceSettingsForm.cs",
}

def read(p):
    return io.open(p, "r", encoding="utf-8-sig").read()

def has(pattern, text, flags=0):
    return re.search(pattern, text, flags) is not None

checks = []
def check(ok, what):
    checks.append((ok, what))
    print(("  ok: " if ok else "  FAIL: ") + what)

L = read(FILES["logon"])
M = read(FILES["master"])
A = read(FILES["appear"])

# ---- S1: F1 死按钮修复后 ----
s1 = []
s1.append(("btnUp 声明存在", has(r"var btnUp = new AntdUI\.Button", L)))
s1.append(("btnDown 声明存在", has(r"var btnDown = new AntdUI\.Button", L)))
s1.append(("btnUp 已接 Click", has(r"btnUp\.Click \+=", L)))
s1.append(("btnDown 已接 Click", has(r"btnDown\.Click \+=", L)))
s1.append(("上移含相邻交换逻辑", has(r"btnUp\.Click[\s\S]{0,400}steps\.Insert\(i - 1, cur\)", L)))
s1.append(("下移含相邻交换逻辑", has(r"btnDown\.Click[\s\S]{0,400}steps\.Insert\(i \+ 1, cur\)", L)))
s1.append(("排序后 refreshSteps 被调", has(r"btnUp\.Click[\s\S]{0,400}refreshSteps\(\);", L) and has(r"btnDown\.Click[\s\S]{0,400}refreshSteps\(\);", L)))
s1.append(("btnAddStep 已接线(不回归)", has(r"btnAddStep\.Click \+=", L)))
s1.append(("btnDelStep 已接线(不回归)", has(r"btnDelStep\.Click \+=", L)))
for w, ok in s1:
    check(ok, "S1 " + w)

# ---- S2: F4 主密码框修复后 ----
check(not has(r"Size = DpiScale\.S\(dialog, 335, 28\)", M), "S2 pwdBox 不再字面 28 高")
check(has(r"Size = DpiScale\.S\(dialog, 335, fieldH\)", M), "S2 pwdBox 高度来自 fieldH 变量")
check(has(r"int fieldH = Math\.Max\(DpiScale\.V\(dialog, 38\), FormFontPolicy\.RowStep\(dialog\)\)", M), "S2 fieldH 为仓库标准口径")
check(has(r"dialog\.CancelButton = cancelBtn", M), "S2 CancelButton 已绑(ESC 可取消)")
check(has(r"var cancelBtn = new AntdUI\.Button[\s\S]{0,240}DialogResult = DialogResult\.Cancel", M), "S2 取消按钮含 Cancel 语义")

# ---- S3: F9 修复后 ----
check(not has(r"Size = new Size\(86, _fieldHeight\)", A), "S3 裸 86 宽已消除")
check(has(r"Size = new Size\(DpiScale\.V\(this, 86\), _fieldHeight\)", A), "S3 86 宽走 DpiScale.V")

# ---- S4: 反向边界(本包不触其他文件的行为) ----
check(has(r"btnAddStep\.Click[\s\S]{0,200}refreshSteps\(\); \}", L), "S4 +/− handler 保持(先决形态)")
check(len(re.findall(r"DialogResult = DialogResult\.Cancel", M)) == 2, "S4 master 恰两处 Cancel 语义(按钮+键盘)")

fails = [w for ok, w in checks if not ok]
print("UI-FIX-CHECK", "ALL OK" if not fails else ("FAIL " + str(len(fails))),
      "(" + str(len(checks)) + " checks)")
sys.exit(1 if fails else 0)
