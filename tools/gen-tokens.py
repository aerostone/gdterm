#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""色彩 token SSOT 生成器（审计 C9-1 / C5 文档漂移根治）。

SSOT 是 `src/Gdterm.UI/Diagnostics/GdtermColorTable.cs`，本脚本解析它：
  - 静态字段初始值 = Dark 默认（必须与 ApplyTheme 的 Dark/default 分支一致，否则报错）；
  - ApplyTheme 的 Darker / OLED 分支 = 另两套主题；
  - 主题分支里没有出现的 token（OnAccent/OnDanger/OnInfo/Overlay/四语义色）= 三主题共享。

产物：
  1. docs/tokens.json            —— 机器可读全量色值（无时间戳，幂等）；
  2. docs/DESIGN-LANGUAGE.md §1  —— `<!-- TOKENS:BEGIN -->` 与 `<!-- TOKENS:END -->`
     之间的生成表格（手改必漂，2026-10-07 审计 C5 的实证：文档 #11161A vs 代码 #0D1116）。

用法：
  python3 tools/gen-tokens.py            # 重新生成（改完色值后跑一次）
  python3 tools/gen-tokens.py --check    # CI 模式：文档/JSON 与代码不一致 => exit 1
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "Gdterm.UI", "Diagnostics", "GdtermColorTable.cs")
JSON_OUT = os.path.join(ROOT, "docs", "tokens.json")
DOC = os.path.join(ROOT, "docs", "DESIGN-LANGUAGE.md")
BEGIN = "<!-- TOKENS:BEGIN"
END = "<!-- TOKENS:END"

THEMES = ["Dark", "Darker", "OLED"]

# token → 一句话语义（生成表最后一列；新增 token 在此补一行，缺省显示 "—"）
SEMANTICS = {
    "Background": "最底层背景（窗体底色、列表区）",
    "Surface": "卡片/控件面（输入框底、工具栏）",
    "Surface2": "悬浮层/弹出面板面（与 Surface 拉开一档）",
    "Border": "1px 分隔线（弱边框）",
    "BorderStrong": "交互件边缘（按钮/输入框描边，对 Surface ≥3:1）",
    "Accent": "唯一强调色（薄荷绿）：主按钮、选中态",
    "Foreground": "主文字（标题、正文）",
    "Muted": "次级文字（说明、占位）",
    "Hover": "悬浮态面",
    "Pressed": "按压态面",
    "Tint": "选中态强调底（Accent 10% 透明叠加）",
    "OnAccent": "Accent 实心按钮上的文字",
    "OnDanger": "Danger 实心按钮上的文字",
    "OnInfo": "Info 底色上的文字",
    "Overlay": "锁定遮罩",
    "Danger": "危险/删除/错误文字",
    "Warning": "告警文字",
    "Success": "成功状态文字",
    "Info": "链接/信息（仅限可点击文字）",
}


def parse_argb(args_str):
    """Color.FromArgb(...) 参数 → #RRGGBB 或 #AARRGGBB（Windows 顺序，alpha 在前）。"""
    vals = []
    for part in args_str.split(","):
        part = part.strip()
        v = int(part, 0)  # 0x.. 或十进制
        if not 0 <= v <= 255:
            raise ValueError("color channel out of range: %r" % part)
        vals.append(v)
    if len(vals) == 3:
        return "#%02X%02X%02X" % tuple(vals)
    if len(vals) == 4:
        return "#%02X%02X%02X%02X" % tuple(vals)
    raise ValueError("FromArgb arity %d: %r" % (len(vals), args_str))


def parse_source(src):
    """返回 (token_order, values)：values[token][theme] = hex；共享 token 只在 "Dark" 键下。"""
    token_re = re.compile(
        r"public static Color (\w+) \{ get \{ return (s_\w+); \} \}")
    fields = {}          # s_xxx -> TokenName（属性定义顺序 = 展示顺序）
    for m in token_re.finditer(src):
        fields[m.group(2)] = m.group(1)
    if not fields:
        raise ValueError("no token properties found — parser broken?")

    # 静态字段初始值（Dark 默认）
    init = {}
    for m in re.finditer(
            r"private static Color (s_\w+) = Color\.FromArgb\(([^)]+)\);", src):
        init[m.group(1)] = parse_argb(m.group(2))

    # ApplyTheme 各 case 分支
    themes = {}          # theme -> {s_xxx: hex}
    for theme in THEMES:
        case_re = re.compile(r'case "%s":' % theme)
        m = case_re.search(src)
        if m is None:
            raise ValueError("ApplyTheme missing case \"%s\"" % theme)
        # Dark 分支后紧跟 default:，分支体到第一个 break;
        block = src[m.end():src.find("break;", m.end())]
        block = re.sub(r"case \"\w+\":|default:", "", block)
        tvals = {}
        for am in re.finditer(r"(s_\w+) = Color\.FromArgb\(([^)]+)\);", block):
            tvals[am.group(1)] = parse_argb(am.group(2))
        if not tvals:
            raise ValueError("ApplyTheme case \"%s\" has no assignments" % theme)
        themes[theme] = tvals

    # 校验：静态初始值 == Dark 分支（两处手改必漂，CI 直接红）
    for f, hexv in init.items():
        if themes["Dark"].get(f) not in (None, hexv):
            raise ValueError("static init %s=%s 与 ApplyTheme Dark 分支 %s 不一致"
                             % (f, hexv, themes["Dark"].get(f)))
    for f, hexv in themes["Dark"].items():
        if f in init and init[f] != hexv:
            raise ValueError("static init %s 与 Dark 分支 %s 不一致" % (f, hexv))

    order = []
    values = {}
    for field, token in fields.items():
        order.append(token)
        values[token] = {}
        for theme in THEMES:
            if field in themes.get(theme, {}):
                values[token][theme] = themes[theme][field]
            elif theme == "Dark" and field in init:
                values[token][theme] = init[field]
        if not values[token]:
            raise ValueError("token %s 无值（初始值与主题分支都没找到 %s）" % (token, field))
    return order, values


def shared_tokens(values):
    out = {}
    for token, tv in values.items():
        vals = set(tv.values())
        if len(tv) == 3 and len(vals) == 1:
            # 仅当三主题分支都显式给了同一个值，才是"共享"（区别于只落在 Dark 键的缺省）
            if "Dark" in tv and len(tv) == 3:
                out[token] = tv["Dark"]
    # 上面只覆盖"三分支都有"的 token；缺主题键的（只出现在静态初始值）也是共享
    for token, tv in values.items():
        if token not in out and set(tv.keys()) <= {"Dark"}:
            out[token] = tv["Dark"]
    return out


def render_markdown(order, values):
    shared = shared_tokens(values)
    lines = [
        BEGIN_MARK,
        "",
        "| Token | Dark | Darker | OLED | 语义 |",
        "|---|---|---|---|---|",
    ]
    for token in order:
        tv = values[token]
        if token in shared:
            lines.append("| `%s` | `%s` | 同左 | 同左 | %s |"
                         % (token, shared[token], SEMANTICS.get(token, "—")))
        else:
            lines.append("| `%s` | `%s` | `%s` | `%s` | %s |"
                         % (token, tv.get("Dark", "—"), tv.get("Darker", "—"),
                            tv.get("OLED", "—"), SEMANTICS.get(token, "—")))
    lines.append("")
    lines.append("> 三列同值 = 该 token 三主题共享（代码里只写一次）。")
    lines.append("> 本表由 `tools/gen-tokens.py` 生成，改色请改 `GdtermColorTable.cs` 后重跑；")
    lines.append("> CI 用 `--check` 校验一致性（见 docs/UI-UX-VERIFICATION.md）。")
    lines.append("")
    lines.append(END_MARK)
    return "\n".join(lines)


BEGIN_MARK = BEGIN + " — generated by tools/gen-tokens.py, do not edit by hand -->"
END_MARK = END + "-->"


def build():
    with open(SRC, encoding="utf-8-sig") as f:
        src = f.read()
    order, values = parse_source(src)
    shared = shared_tokens(values)
    data = {
        "source": "src/Gdterm.UI/Diagnostics/GdtermColorTable.cs",
        "themes": THEMES,
        "shared_tokens": {t: shared[t] for t in order if t in shared},
        "theme_tokens": {t: values[t] for t in order if t not in shared},
    }
    return data, render_markdown(order, values)


def main():
    data, table = build()
    if "--check" in sys.argv:
        ok = True
        try:
            with open(JSON_OUT, encoding="utf-8") as f:
                cur = json.load(f)
            if cur != data:
                print("FAIL: docs/tokens.json 与 GdtermColorTable.cs 不一致（跑 gen-tokens.py 重新生成）")
                ok = False
        except FileNotFoundError:
            print("FAIL: docs/tokens.json 不存在")
            ok = False
        try:
            with open(DOC, encoding="utf-8") as f:
                doc = f.read()
            i, j = doc.find(BEGIN), doc.find(END)
            if i < 0 or j < 0 or j <= i:
                print("FAIL: DESIGN-LANGUAGE.md 缺 TOKENS 生成块标记")
                ok = False
            else:
                cur_table = doc[i:doc.find("\n", doc.find(END, i) + len(END_MARK))]
                if cur_table != table:
                    print("FAIL: DESIGN-LANGUAGE.md §1 生成表过期（跑 gen-tokens.py 重新生成）")
                    ok = False
        except FileNotFoundError:
            print("FAIL: DESIGN-LANGUAGE.md 不存在")
            ok = False
        if not ok:
            print("→ 修复：python3 tools/gen-tokens.py")
            return 1
        print("tokens: docs/tokens.json 与 DESIGN-LANGUAGE.md §1 均与代码一致（%d 个 token）"
              % (len(data["shared_tokens"]) + len(data["theme_tokens"])))
        return 0

    with open(JSON_OUT, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    with open(DOC, encoding="utf-8") as f:
        doc = f.read()
    i = doc.find(BEGIN)
    j = doc.find(END)
    if i >= 0 and j > i:
        e = doc.find("\n", doc.find(END, i) + len(END_MARK))
        doc = doc[:i] + table + doc[e:]
    else:
        anchor = "## 1. 色彩 Token\n"
        if anchor not in doc:
            print("FAIL: 找不到 §1 锚点，也未找到生成块标记")
            return 1
        doc = doc.replace(anchor, anchor + "\n" + table + "\n", 1)
    with open(DOC, "w", encoding="utf-8") as f:
        f.write(doc)
    print("generated: %s (%d shared + %d theme tokens)"
          % (os.path.relpath(JSON_OUT, ROOT), len(data["shared_tokens"]),
             len(data["theme_tokens"])))
    print("updated:   %s §1 生成表" % os.path.relpath(DOC, ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
