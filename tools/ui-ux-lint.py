#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""UI/UX 静态规范门（L0）——设计语言 SSOT 的机器可读投影。

定位
====
`docs/DESIGN-LANGUAGE.md` 的 §1（色）/§2（字体）/§3（间距）/§4（组件）/§9（AntdUI）
以及 `docs/UI-SCALING-CONVENTIONS.md` 的规则 1-5，过去靠人工 `git diff | grep` 抽查。
本脚本把其中**可静态判定**的部分变成门禁：零依赖（仅 stdlib）、秒级、可在预提交与 CI 跑。

它**不**替代另外两层（见 docs/UI-UX-VERIFICATION.md）：
  - L1 结构门 `tools/ui-tree-check.py`：量真实控件树（几何/重叠/触击/停靠）
  - L2 矩阵门 / L3 视觉基线：DPI × 字号 × 主题 与 PNG 基线

规则（每条都有既有事故或 SSOT 条款作为依据）
=============================================
R01 raw-color      裸 Color.FromArgb（§1.2 禁止项）
R02 raw-font       裸 new Font("非等宽/图标字族")（§2.2 / 缩放规则 3）
R03 literal-size   字面量 new Size(N, N)（缩放规则 1/4，豁免区除外）
R04 literal-extent 字面量 Height/Width >= 20 未过 DpiScale（缩放规则 4）
R05 literal-rowstep 字面量 y += N（§2.3 / 规则 4b）
R06 nonsemantic-primary "确定/OK" 作 Primary（§4.1 明确禁止）
R07 style-override AntdUI.Button 显式 BackColor 覆盖 Type 语义（§9.3）
R08 blocking-toast 成功/完成类信息用阻断 MessageBox（§9.3）
R09 empty-state    列表型 UI 无空态引导（UX 审计 2026-10-07）
R10 consolas-text  自然语言文本用 Consolas（§4.5 / §2.1）
R11 coverage       UI 文件未登记覆盖状态（UI-UX-VERIFICATION §2.3）
R12 select-autosize AntdUI.Select 设了 AutoSize（缩放规则 4d）
R13 bare-pad       裸像素 Margin/Padding（缩放规则 4d / 规则 4）
R14 loc-from-client 构造期绝对坐标源自 ClientSize.Width（规则 2；CI 0.1.359 首捕：
                   字体 AutoScale 后客户端已变，绝对 X 飞出 docked 面板；Forms/ 生效）

基线棘轮（ratchet）
===================
存量违规写在 `.codestable/tools/ui-ux-lint-baseline.txt`（rule|file|snippet）。
CI 只在**新增**违规时失败；`--write-baseline` 重新固化，`--strict` 忽略基线全量判。
这样存量债不被掩盖，也不会因为历史问题让门永久红。

用法
====
  python3 tools/ui-ux-lint.py                  # 门禁模式（基线外的违规 => exit 1）
  python3 tools/ui-ux-lint.py --strict         # 全量模式（审查/清理时用）
  python3 tools/ui-ux-lint.py --json out.json  # 机器可读
  python3 tools/ui-ux-lint.py --write-baseline # 固化当前状态
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UI = os.path.join(ROOT, "src", "Gdterm.UI")
BASELINE = os.path.join(ROOT, ".codestable", "tools", "ui-ux-lint-baseline.txt")

# ── 既有豁免（与 docs/UI-SCALING-CONVENTIONS.md「已知遗留与豁免清单」同源）──
EXEMPT_RAW_FILES = {
    "Diagnostics/GdtermColorTable.cs",   # token 定义本身
    "Services/MenuIconFactory.cs",       # 自绘图标墨色（几何专用）
}
EXEMPT_FONT_FAMILIES = ("consolas", "courier", "cascadia", "segoe ui emoji")
EXEMPT_SIZE_FILES = {
    "Forms/MainForm.cs",                 # 窗体自身 Size 由 PerMonitorV2 缩放
    "Controls/SplitPaneControl.cs",      # 相对布局
    "Controls/SnippetSearchPanel.cs",    # 临时收集窗自身 Size
    "Controls/TerminalControl.cs",       # 单元格网格自算度量
    "Forms/ConnectionDialog.cs",         # ClientSize 在按钮行后按行高动态计算
}
# 列表型 UI 的空态豁免：非列表语义 / 空态由宿主窗体承担
EXEMPT_EMPTY_STATE = {
    "Controls/ConnectionQuickJumpForm.cs",  # 快捷跳转：空即无匹配，输入框本身即引导
    "Controls/FilePaneControl.cs",          # 目录列表空=空目录，系统语义自明
    "Forms/PasswordHealthForm.cs",          # 健康报告空态由表头状态行承担（HealthScanStateLabel）
    "Forms/DangerousCommandConfigForm.cs",  # 规则表空态由规则编辑区承担
}
LIST_UI_RE = re.compile(r"new ListView|new ListBox|new AntdUI\.Table")
EMPTY_STATE_RE = re.compile(r"EmptyStateView|EmptyHint|EmptyGuide|暂无|没有可|为空|无记录|空列表")
SUCCESS_WORDS = ("成功", "已完成", "已保存", "已复制", "已导出", "已导入", "已删除", "已清空")

RULES = {}


def rule(code, title, clause):
    def deco(fn):
        RULES[code] = (title, clause, fn)
        return fn
    return deco


def iter_cs():
    for dirpath, dirnames, filenames in os.walk(UI):
        dirnames[:] = [d for d in dirnames if d not in ("obj", "bin")]
        for fn in sorted(filenames):
            if fn.endswith(".cs"):
                yield os.path.join(dirpath, fn)


def rel(path):
    return os.path.relpath(path, UI).replace(os.sep, "/")


def read(path):
    with open(path, encoding="utf-8-sig", errors="replace") as fp:
        return fp.readlines()


def snippet(line, limit=120):
    return " ".join(line.strip().split())[:limit]


def is_comment(line):
    s = line.lstrip()
    return s.startswith("//") or s.startswith("///") or s.startswith("*")


# ────────────────────────────── R01 ──────────────────────────────
@rule("R01", "裸 Color.FromArgb", "DESIGN-LANGUAGE §1.2")
def r01(path, lines):
    if rel(path) in EXEMPT_RAW_FILES:
        return
    for i, line in enumerate(lines, 1):
        if is_comment(line):
            continue
        if "Color.FromArgb(" in line:
            yield i, snippet(line)


# ────────────────────────────── R02 ──────────────────────────────
FONT_RE = re.compile(r'new Font\(\s*"([^"]+)"')
@rule("R02", "裸字体构造", "DESIGN-LANGUAGE §2.2 / SCALING 规则 3/4c")
def r02(path, lines):
    if rel(path) == "Services/FormFontPolicy.cs":
        return
    for i, line in enumerate(lines, 1):
        if is_comment(line):
            continue
        for m in FONT_RE.finditer(line):
            fam = m.group(1).lower()
            if not any(k in fam for k in EXEMPT_FONT_FAMILIES):
                yield i, snippet(line)


# ────────────────────────────── R03 ──────────────────────────────
SIZE_RE = re.compile(r"new Size\(\s*(\d+)\s*,\s*(\d+)\s*\)")
@rule("R03", "字面量 Size", "SCALING 规则 1/4")
def r03(path, lines):
    if rel(path) in EXEMPT_SIZE_FILES:
        return
    for i, line in enumerate(lines, 1):
        if is_comment(line):
            continue
        if SIZE_RE.search(line) and "DpiScale" not in line and "MinimumSize" not in line:
            yield i, snippet(line)


# ────────────────────────────── R04 ──────────────────────────────
EXTENT_RE = re.compile(r"\b(Height|Width)\s*=\s*(\d{2,4})\s*[,;]")
@rule("R04", "字面量 Height/Width", "SCALING 规则 4")
def r04(path, lines):
    for i, line in enumerate(lines, 1):
        if is_comment(line) or "DpiScale" in line or "FormFontPolicy" in line:
            continue
        if re.search(r"\bconst\b", line):
            continue
        for m in EXTENT_RE.finditer(line):
            if int(m.group(2)) >= 20:
                yield i, snippet(line)


# ────────────────────────────── R05 ──────────────────────────────
ROWSTEP_RE = re.compile(r"\by\s*\+=\s*\d+")
@rule("R05", "字面量行距", "DESIGN-LANGUAGE §2.3 / SCALING 规则 4b")
def r05(path, lines):
    for i, line in enumerate(lines, 1):
        if is_comment(line) or "RowStep" in line:
            continue
        if ROWSTEP_RE.search(line):
            yield i, snippet(line)


# ────────────────────────────── R06 ──────────────────────────────
NONSEMANTIC_RE = re.compile(r'Text\s*=\s*"(确定|OK|Ok)"')
@rule("R06", "确定/OK 作 Primary", "DESIGN-LANGUAGE §4.1")
def r06(path, lines):
    for i, line in enumerate(lines, 1):
        if is_comment(line) or not NONSEMANTIC_RE.search(line):
            continue
        lo, hi = max(0, i - 6), min(len(lines), i + 5)
        window = "\n".join(lines[lo:hi])
        if "TTypeMini.Primary" in window:
            yield i, snippet(line)


# ────────────────────────────── R07 ──────────────────────────────
def strip_line_comment(line):
    """去掉行尾 // 注释（跳过字符串字面量内的 //）。R07 需要它：注释里提到
    “忽略 BackColor”这种说明文字，不能被当成真的写了 BackColor。"""
    in_str = False
    quote = ""
    i = 0
    n = len(line)
    while i < n:
        ch = line[i]
        if in_str:
            if ch == "\\":
                i += 2
                continue
            if ch == quote:
                in_str = False
        else:
            if ch in ("'", '"'):
                in_str = True
                quote = ch
            elif ch == "/" and i + 1 < n and line[i + 1] == "/":
                return line[:i]
        i += 1
    return line


@rule("R07", "AntdUI.Button 语义色被覆盖", "DESIGN-LANGUAGE §9.3 / §4.1")
def r07(path, lines):
    """同一个对象初始化器内 Type=TTypeMini.X 与 BackColor 并存 = 语义级别已被手涂色覆盖。

    2026-10-07 修正：旧实现从 `new AntdUI.Button` 往后扫 15 行再拼字符串，
    兄弟按钮的 BackColor 会被算进来 —— 实测 7 处命中全是假阳性（Primary 钮本身没写色，
    是相邻的“取消”钮写了 BackColor）。改为花括号配平，只判**同一个**初始化器。
    """
    for i, line in enumerate(lines, 1):
        if is_comment(line) or "new AntdUI.Button" not in line:
            continue
        # 从该行起花括号配平，取同一个 new 表达式
        depth = 0
        started = False
        block = []
        for j in range(i - 1, min(len(lines), i + 40)):
            cur = lines[j]
            block.append(cur)
            for ch in cur:
                if ch == "{":
                    depth += 1
                    started = True
                elif ch == "}":
                    depth -= 1
            if started and depth <= 0:
                break
            if not started and ";" in cur:
                break
        joined = "".join(strip_line_comment(x) for x in block)
        if "TTypeMini." in joined and "BackColor" in joined:
            yield i, snippet(line)


# ────────────────────────────── R08 ──────────────────────────────
@rule("R08", "成功信息用阻断 MessageBox", "DESIGN-LANGUAGE §9.3")
def r08(path, lines):
    for i, line in enumerate(lines, 1):
        if is_comment(line) or "MessageBox.Show" not in line:
            continue
        window = "".join(lines[i - 1:min(len(lines), i + 3)])
        if any(w in window for w in SUCCESS_WORDS):
            yield i, snippet(line)


# ────────────────────────────── R09 ──────────────────────────────
@rule("R09", "列表型 UI 无空态引导", "UX 审计 2026-10-07 (C1)")
def r09(path, lines):
    r = rel(path)
    if r in EXEMPT_EMPTY_STATE:
        return
    text = "".join(lines)
    if not LIST_UI_RE.search(text):
        return
    if EMPTY_STATE_RE.search(text):
        return
    # 定位第一处列表构造，便于报告点击跳转
    for i, line in enumerate(lines, 1):
        if LIST_UI_RE.search(line):
            yield i, snippet(line)
            return


# ────────────────────────────── R10 ──────────────────────────────
MONO_UI_RE = re.compile(r'new Font\("Consolas",[^)\n]*UIFontSize')
@rule("R10", "等宽字体跟随 UI 字号（逐处复制）", "DESIGN-LANGUAGE §2.1 / §4.2")
def r10(path, lines):
    """40 处逐点复制 `new Font("Consolas", ... UIFontSize ...)`：
    ① 等宽字体应走独立策略（FormFontPolicy.MonoFont），不该被 8–24pt 的 UI 字号直接放大，
       否则 ListView/固定行高的列表在 UIFontSize=20 时会裁切；
    ② 每个点各自写 fallback pt（8.5/9/9.5/10/11/14），字号阶梯实际已分裂；
    ③ 逐处 new Font 不 Dispose，面板重建即泄漏 GDI 句柄。"""
    for i, line in enumerate(lines, 1):
        if is_comment(line):
            continue
        if MONO_UI_RE.search(line):
            yield i, snippet(line)


# ────────────────────────────── R11 ──────────────────────────────
REGISTRY = os.path.join(ROOT, ".codestable", "tools", "ui-coverage-registry.txt")
_registry_cache = {}


def load_registry():
    if "map" in _registry_cache:
        return _registry_cache["map"], _registry_cache["floor"]
    m, floor = {}, 0
    if os.path.exists(REGISTRY):
        with open(REGISTRY, encoding="utf-8") as fp:
            for line in fp:
                line = line.strip()
                if not line:
                    continue
                if line.startswith("#"):
                    if "floor:" in line:
                        try:
                            floor = int(line.split("floor:")[1].split()[0])
                        except ValueError:
                            pass
                    continue
                parts = [x.strip() for x in line.split("|")]
                if len(parts) >= 2:
                    m[parts[0]] = parts[1]
    _registry_cache["map"], _registry_cache["floor"] = m, floor
    return m, floor


@rule("R11", "UI 文件未登记覆盖状态", "UI-UX-VERIFICATION §2.3")
def r11(path, lines):
    r = rel(path)
    if not (r.startswith("Controls/") or r.startswith("Forms/")):
        return
    if r.endswith(".Designer.cs"):
        return
    m, _ = load_registry()
    if r not in m:
        yield 1, "not in .codestable/tools/ui-coverage-registry.txt"


# ────────────────────────────── R12 ──────────────────────────────
@rule("R12", "AntdUI.Select 设了 AutoSize", "SCALING 规则 4d")
def r12(path, lines):
    for i, line in enumerate(lines, 1):
        if is_comment(line) or "new AntdUI.Select" not in line:
            continue
        # 只认带初始化块的写法；`new AntdUI.Select()` 后面若紧跟别家控件的
        # 多行初始化会被块扫描误判（ConnectionDialog 的 AddRow 折行就是这么中的）
        nxt = lines[i].strip() if i < len(lines) else ""
        if "{" not in line.split("new AntdUI.Select", 1)[1] and not nxt.startswith("{"):
            continue
        block = [line]
        j = i
        while j < len(lines) and j < i + 12:
            block.append(lines[j])
            if "};" in lines[j] or lines[j].strip() == "}":
                break
            j += 1
        if any("AutoSize" in b and not is_comment(b) for b in block):
            yield i, snippet(line)


# ────────────────────────────── R13 ──────────────────────────────
BARE_PAD_RE = re.compile(r"(Margin|Padding)\s*=\s*new Padding\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\)")
@rule("R13", "裸像素 Margin/Padding", "SCALING 规则 4d / 规则 4")
def r13(path, lines):
    for i, line in enumerate(lines, 1):
        if is_comment(line) or "DpiScale" in line:
            continue
        if BARE_PAD_RE.search(line):
            yield i, snippet(line)


# ────────────────────────────── R14 ──────────────────────────────
# 构造期绝对坐标页脚反模式（CI 0.1.359 首捕教训）：
#   Location = … ClientSize.Width - N …
# 字体变更触发 AutoScaleMode=Font 缩放后 ClientSize 已变，绝对 X 按放大宽
# 计算，Show 后回弹设计值，docked 父面板跟随而按钮不随 → 飞出面板。
# 仅 Forms/ 生效：该目录是构造期布局区；Resize 处理器里的动态重算合法，
# 但出现在窗体构造器里几乎必是此反模式。页脚按钮一律 FlowLayoutPanel 流式。
LOC_FROM_CLIENT_RE = re.compile(r"Location\s*=.*ClientSize\.Width")
@rule("R14", "构造期绝对坐标源自 ClientSize.Width", "SCALING 规则 4e（0.1.359 门禁首捕）")
def r14(path, lines):
    if "/Forms/" not in path.replace("\\", "/"):
        return
    for i, line in enumerate(lines, 1):
        if is_comment(line):
            continue
        if LOC_FROM_CLIENT_RE.search(line):
            yield i, snippet(line)


# ────────────────────────────── R15 ──────────────────────────────
# 对话框必须有 Esc 出路：CancelButton 未绑 = 键盘用户只能点 × 或找鼠标。
# 这条是 CI 20 分钟反馈换来的：LogViewerForm 只加了「关闭」按钮没绑 CancelButton，
# 冒烟门 R3（DialogsSmoke 的 cancelbtn 断言）在 Windows 上才红，本地零信号。
# 免判：MainForm（主窗不是对话框）、TrashForm（工具小窗，按钮全为操作语义，冒烟门已免判）。
ESC_EXEMPT = {
    "Forms/MainForm.cs",
    "Forms/TrashForm.cs",
}


@rule("R15", "对话框未绑 Esc（CancelButton）", "UI-UX-VERIFICATION §2.3 / 冒烟门 R3")
def r15(path, lines):
    r = rel(path)
    if not (r.startswith("Forms/") or r.startswith("Controls/")):
        return
    if r.endswith(".Designer.cs") or r in ESC_EXEMPT:
        return
    # 只看代码行：注释里提到 CancelButton/Escape（比如写"这里为什么不绑"）不算实现
    joined = "".join(l for l in lines if not is_comment(l))
    if "class " not in joined:
        return
    if "CancelButton" in joined:
        return
    # 自己截 Esc 也算"有出路"：命令面板/快速跳转（输入框吞 CancelButton）、
    # 扫描中心（运行中不许硬关）都必须显式判 Keys.Escape。
    if "Keys.Escape" in joined:
        return
    # 只判"窗体"：继承 Form / AntdUI.Window 的类才谈得上 Esc 语义
    for i, line in enumerate(lines, 1):
        if is_comment(line):
            continue
        if re.search(r"class\s+\w+\s*:\s*(?:.*\b)?(?:AntdUI\.)?(?:Window|Form)\b", line):
            yield i, snippet(line)
            return


# ────────────────────────────── 主流程 ──────────────────────────────
def scan():
    hits = []
    for path in iter_cs():
        lines = read(path)
        for code, (title, clause, fn) in sorted(RULES.items()):
            for ln, text in fn(path, lines):
                hits.append({
                    "rule": code,
                    "title": title,
                    "clause": clause,
                    "file": rel(path),
                    "line": ln,
                    "text": text,
                })
    return hits


def key_of(h):
    # 行号不参与 key：容忍上下插入行导致的漂移
    return "%s|%s|%s" % (h["rule"], h["file"], re.sub(r"\s+", " ", h["text"]))


def load_baseline():
    if not os.path.exists(BASELINE):
        return set()
    out = set()
    with open(BASELINE, encoding="utf-8") as fp:
        for line in fp:
            line = line.rstrip("\n")
            if line and not line.startswith("#"):
                out.add(line)
    return out


def write_baseline(hits):
    os.makedirs(os.path.dirname(BASELINE), exist_ok=True)
    with open(BASELINE, "w", encoding="utf-8", newline="\n") as fp:
        fp.write("# UI/UX L0 静态门基线（棘轮）。新增违规会让 CI 红；\n")
        fp.write("# 修复后运行 `python3 tools/ui-ux-lint.py --write-baseline` 收紧。\n")
        fp.write("# 格式：rule|file|snippet\n")
        for k in sorted(set(key_of(h) for h in hits)):
            fp.write(k + "\n")


def main(argv):
    strict = "--strict" in argv
    json_out = None
    if "--json" in argv:
        json_out = argv[argv.index("--json") + 1] if argv.index("--json") + 1 < len(argv) else "ui-ux-lint.json"

    hits = scan()
    baseline = load_baseline()

    if "--write-baseline" in argv:
        write_baseline(hits)
        print("baseline written: %d entries -> %s" % (len(set(key_of(h) for h in hits)), BASELINE))
        return 0

    new = [h for h in hits if key_of(h) not in baseline] if not strict else hits

    by_rule = {}
    for h in hits:
        by_rule.setdefault(h["rule"], []).append(h)

    print("=== ui-ux-lint (L0 静态规范门) ===")
    print("扫描 %d 个 .cs；命中 %d 条；基线 %d 条；基线外(新增) %d 条"
          % (sum(1 for _ in iter_cs()), len(hits), len(baseline), len(new)))
    for code in sorted(RULES):
        title, clause, _ = RULES[code]
        n = len(by_rule.get(code, []))
        nnew = len([h for h in new if h["rule"] == code])
        print("  %s %-28s 存量=%-3d 新增=%-3d  [%s]" % (code, title, n, nnew, clause))

    # 覆盖度记分牌：dump/manual 之外的面板即"零自动验证"区
    reg, floor = load_registry()
    covered = sum(1 for v in reg.values() if v.startswith("dump:"))
    manual = sum(1 for v in reg.values() if v.startswith("manual:"))
    na = sum(1 for v in reg.values() if v.startswith("n/a:"))
    total = len(reg)
    print("  [coverage] dump=%d manual=%d none=%d n/a=%d 合计=%d  下限=%d"
          % (covered, manual, total - covered - manual - na, na, total, floor))
    if floor and covered < floor:
        print("  [coverage] 回退！dump 覆盖 %d < 下限 %d" % (covered, floor))
        new = new + [{"rule": "R11", "title": "覆盖度回退", "clause": "UI-UX-VERIFICATION §2.3",
                      "file": "(coverage)", "line": 0, "text": "dump=%d < floor=%d" % (covered, floor)}]

    if new:
        print("\n--- 基线外违规（必须修）---")
        for h in sorted(new, key=lambda x: (x["rule"], x["file"], x["line"])):
            print("  %s %s:%d  %s" % (h["rule"], h["file"], h["line"], h["text"]))
    elif not strict:
        print("\n基线外无新增违规。")

    if json_out:
        with open(json_out, "w", encoding="utf-8", newline="\n") as fp:
            json.dump({"total": len(hits), "new": len(new), "hits": hits}, fp,
                      ensure_ascii=False, indent=2)
        print("\njson -> %s" % json_out)

    if strict:
        return 1 if hits else 0
    return 1 if new else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
