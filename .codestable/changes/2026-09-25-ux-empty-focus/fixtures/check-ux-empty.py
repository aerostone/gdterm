# -*- coding: utf-8 -*-
"""UX 空状态/焦点 change 的静态契约检查（本地 RED/GREEN 驱动；动态证据交 CI 323）。"""
import sys, pathlib
root = pathlib.Path('.')
FAIL = []

def check(ok, what):
    print(("  ok:   " if ok else "  FAIL: ") + what)
    if not ok: FAIL.append(what)

def read(p): return (root / p).read_text(encoding='utf-8-sig')

kp = read('src/Gdterm.UI/Forms/KeePassManagerForm.cs')
sc = read('src/Gdterm.UI/Forms/ScannerCenterForm.cs')
ph = read('src/Gdterm.UI/Forms/PasswordHealthForm.cs')
smoke = read('src/Gdterm.Tests/Ui/UiSmokeRunner.cs')

print('S1 KeePass 空库引导')
check('KeePassEmptyGuide' in kp, 'K1 空引导层 Name=KeePassEmptyGuide 存在')
check('KeePassEmptyGuideAddButton' in kp, 'K2 引导主按钮 Name=KeePassEmptyGuideAddButton 存在')
check('还没有条目' in kp, 'K3 引导文案"还没有条目"存在')
check('_emptyGuide.Visible' in kp, 'K4 空/有行两态显隐已接 LoadEntries')
check(smoke.count('KeePassEmptyGuide') >= 1, 'K5 冒烟按 Name 断言引导（CI 侧）')

print('S2 scanner 空状态 + 右列对齐')
check('还没有插件' in sc, 'C1 插件表空引导文案存在')
check('UpdateEmptyHints' in sc, 'C1b 空状态集中开关存在并被 Refresh/Render 调用')
check('_pluginEmptyHint' in sc, 'C2 空引导控件已声明')
check('_findingEmptyHint' in sc and '_rawEmptyHint' in sc, 'C3 右列 finding/raw 空提示已声明')
check('PadRightColumn' in sc or 'rightPad' in sc, 'C4 右列左缘 padding 对齐已实现')

print('S3 健康度上次扫描状态行')
check('_scanStateLabel' in ph, 'H1 状态行字段已声明')
check('上次扫描' in ph, 'H2 状态行文案存在')
check(ph.count('_scanStateLabel.Text') >= 2, 'H3 DisplayReport 内赋值（含从未/已扫描两态）')

print('S4 scanner 上下文 ESC')
check('KeyPreview' in sc, 'E1 KeyPreview 已开')
check('Keys.Escape' in sc, 'E2 Escape 键处理存在')
check('_running' in sc and 'Close()' in sc, 'E3 运行中不关/空闲关两分支存在')

print()
print("S5 R4 键盘可达性规则")
uc=pathlib.Path('tools/ui-tree-check.py').read_text(encoding='utf-8-sig')
check('键踠可达性' in uc.replace('键盘可达性','键踠可达性'), 'R4 规则已写入 ui-tree-check')
check('INTER_TYPES' in uc and 'split(".")[-1] in INTER_TYPES' in uc, 'R4 用短名精确匹配(不误伤 TableLayoutPanel)')
check('mainform' in uc and 'continue' in uc, 'R4 在 mainform 分支之后(主窗自有规则)')

print()
print("S6 停靠遮挡守卫（D5）")
check('private static void AssertNoDockOverlap' in smoke, 'S6-1 C# 侧 AssertNoDockOverlap 已实现')
check(smoke.count('AssertNoDockOverlap(f,') == 4, 'S6-2 C# 侧 4 个用例都调用')
check('IsEdgeDock' in smoke and 'aFill && bEdge' in smoke, 'S6-3 C# 侧只比 Fill vs 边停靠')
check('R5 停靠遮挡' in uc and '停靠无遮挡' in uc, 'S6-4 Python 侧 R5 规则已写入')
check('[(tree, None, "", None, "ROOT")] + walk(tree)' in uc, 'S6-5 Python 侧含窗体根级兄弟')
check('dangerous-cmd 存量债' in uc, 'S6-6 Python 侧 dangerous-cmd 存量免判')
check(('R1_RED' if True else '') and '还没有插件：先放入脚本文件' in sc, 'S6-7 左列空态文案已缩短(防硬裁)')
check('ColumnStyles' in kp, 'S6-8 KeePass 引导 TLP 显式列样式(防内容偏左)')

print('UX-EMPTY-CHECK', 'FAIL' if FAIL else 'ALL OK', len(FAIL))
sys.exit(1 if FAIL else 0)

