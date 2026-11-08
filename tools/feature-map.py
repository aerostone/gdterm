#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""feature-map — 功能大纲（L1/L2 入口树）× 页面契约完备性矩阵。

回答的问题不是「布局对不对」（那是 ui-ux-lint / ui-tree-check 的事），而是
**「这个功能页真的做完了吗」**：入口可达、四种状态齐、能查能筛能批、危险操作有闸门、
数据能持久化、键盘能用、失败有话说。

设计依据（as-search 2026-11-06 检索结论，见 docs/FEATURE-MAP.md §0）：
- 内容/页面清单先行（IA audit：先列全页面，再判组织与完备）
- 状态清单（empty / zero / loading / partial / error / denied / irreversible）
  ——「只画了 happy path 的设计只是三分之一的设计」
- 列表页 DoD（空态/加载/错误重试/分页/多选批量/过滤后空态）
- RTM 双向追溯（需求 ↔ 页面 ↔ 证据）：既防「有入口没页面」，也防「有页面没入口」

两条腿：
1. **大纲树**（自动抽取，不可漂移）：MainFormMenuBuilder 的菜单 L1/L2 叶 →
   MainForm.Callbacks 的 lambda → SidePanelFactory / ToolsDialogsLauncher 的工厂方法 →
   页面类 → 源文件。任何新菜单项都必须能落到一个登记过的页面契约，否则门红。
2. **契约矩阵**（半自动）：「能力格」逐项判定 —— `auto` 由源码特征判定（真空态组件、
   错误提示、BusyScope、确认框、Store 持久化…），`dump` 由 ui-smoke 的控件树 dump 判定，
   `manual` 必须人工签字（与 docs/UI-UX-VERIFICATION.md §5 覆盖登记表同源）。

用法：
  python3 tools/feature-map.py                # 矩阵 + 缺口清单（人看）
  python3 tools/feature-map.py --scan         # 只打印入口树解析结果（调试用）
  python3 tools/feature-map.py --write        # 重写 docs/FEATURE-MAP.md
  python3 tools/feature-map.py --check        # 门禁：未登记入口 / 文档漂移 / 空契约 => exit 1
  python3 tools/feature-map.py --json out.json
  python3 tools/feature-map.py --only 工具    # 只看某一级菜单

零依赖（stdlib）。
"""

import argparse
import io
import json
import os
import re
import sys

LB, RB = chr(123), chr(125)   # 大括号在部分管道里会被吃掉，统一用码点构造
LP, RP = chr(40), chr(41)
LS, RS = chr(91), chr(93)
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UI = os.path.join(ROOT, "src", "Gdterm.UI")
CONTRACTS = os.path.join(ROOT, "docs", "feature-contracts.json")
DOCTARGET = os.path.join(ROOT, "docs", "FEATURE-MAP.md")


def read(path):
    try:
        with io.open(path, encoding="utf-8-sig", errors="replace") as f:
            return f.read()
    except IOError:
        return ""


def clean_leaf(text):
    """菜单文本归一：去助记符 (&N)、省略号、行尾快捷键。"""
    t = re.sub(r'\(&\w\)', '', text or '')
    t = t.replace('...', '').replace('\u2026', '')
    t = re.sub(r'\s*(Ctrl\+Shift\+\w|Ctrl\+\w|Alt\+\w|F\d+)\s*$', '', t)
    return t.strip()


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def brace_body(text, start):
    """从 start（指向' 之后）开始按大括号配平取出一段代码。"""
    depth = 1
    i = start
    n = len(text)
    while i < n and depth > 0:
        ch = text[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
        i += 1
    return text[start:i]


# --------------------------------------------------------------------------
# 1. 入口树：菜单 → 回调 → 工厂方法 → 页面类 → 文件
# --------------------------------------------------------------------------

# i18n 感知（P2-10）：字面量可能被 L10n.T()/Tf() 包装——剥壳提取，保持菜单大纲不漂移
_TW = r'(?:L10n\.T(?:f)?\(\s*)?'   # 可选包装前缀
_TE = r'(?:\s*\))*'                 # 可选包装闭括号（0-2 个）
MENU_MK = re.compile(r'Mk\(\s*' + _TW + r'"([^"]+)"' + _TE + r'\s*,\s*cb\.(\w+)\s*,\s*' + _TW + r'"([^"]*)"' + _TE + r'\s*\)')
MENU_NEW = re.compile(r'var\s+(\w+)\s*=\s*new\s+ToolStripMenuItem\(\s*' + _TW + r'"([^"]+)"' + _TE)
MENU_ONLY_NEW = re.compile(r'DropDownItems\.Add\(\s*new\s+ToolStripMenuItem\(\s*' + _TW + r'"([^"]+)"' + _TE)
MENU_L1 = re.compile(r'var\s+(\w+)\s*=\s*new\s+ToolStripMenuItem\(\s*' + _TW + r'"([^"]+)"' + _TE + r'\s*\)\s*;')
CB_PROP = re.compile(r'^\s{8,}(\w+)\s*=', re.M)

SHORTCUT = re.compile(r'\s*(Ctrl\+Shift\+\w|Ctrl\+\w|Alt\+\w|F\d+)\s*$')
CB_ICON = re.compile(r'^\s*//.*$', re.M)

# lambda 体里能指认页面的写法
CREATE_REF = re.compile(r'Create(\w+)Panel\s*\(')
OPEN_REF = re.compile(r'Open(\w+)\s*\(\s*\)')
NEW_PAGE = re.compile(r'new\s+(?:[\w.]+\.)?(\w+(?:Form|Dialog|Panel))\b')
SHOW_PANEL = re.compile(r'Show(\w+)Panel\s*\(')


def parse_menu():
    """返回 [(L1标题, [(叶文本, 回调名, 图标)])]，保持源码顺序。"""
    text = read(os.path.join(UI, "Services", "MainFormMenuBuilder.cs"))
    # L1：按 var xxx = new ToolStripMenuItem("文件(&F)"); 出现的顺序，配合 menu.Items.Add(xxx) 定位块
    l1 = []
    tops = re.findall(r'menu\.Items\.Add\((\w+)\)', text)
    for m in MENU_L1.finditer(text):
        var, title = m.group(1), m.group(2)
        if var not in tops:
            continue  # 下拉里的勾选项，不是一级菜单
        l1.append((var, title, m.start()))
    out = []
    for idx, (var, title, pos) in enumerate(l1):
        end = l1[idx + 1][2] if idx + 1 < len(l1) else len(text)
        block = text[pos:end]
        leaves = []
        for m in MENU_MK.finditer(block):
            leaves.append((m.group(1), m.group(2), m.group(3)))
        # 手写的 ToolStripMenuItem（勾选项、带 ShortcutKeys 的项）
        for m in MENU_NEW.finditer(block):
            local, t = m.group(1), m.group(2)
            if t == title:
                continue
            # 找这个变量后面挂的 cb.X
            tail = block[m.end():m.end() + 900]
            cb = re.search(r'\b' + re.escape(local) + r'\.Click\s*\+=\s*cb\.(\w+)', tail)
            if cb:
                leaves.append((t, cb.group(1), ""))
        for m in MENU_ONLY_NEW.finditer(block):
            leaves.append((m.group(1), None, ""))
        out.append((title, leaves))
    return out


def parse_callbacks():
    """MainForm.cs 里 new MainFormMenuBuilder.Callbacks { X = ..., ... } 的每个 lambda 体。

    要按 ()、[]、{} 三层配平找逗号 —— lambda 的形参列表 (s, e) 里就有逗号，
    只数大括号会把 body 切成 " (s"。
    """
    text = read(os.path.join(UI, "Forms", "MainForm.cs"))
    anchor = text.find("new MainFormMenuBuilder.Callbacks")
    if anchor < 0:
        return {}
    start = text.find(LB, anchor)
    body = brace_body(text, start + 1)
    out = {}
    depth = 0
    buf = 0
    pending = None
    i = 0
    n = len(body)
    while i < n:
        ch = body[i]
        if ch in (LB, LP, LS):
            depth += 1
        elif ch in (RB, RP, RS):
            depth -= 1
        elif ch == '"':
            j = i + 1
            while j < n and body[j] != '"':
                j += 2 if body[j] == "\\" else 1
            i = j
        elif depth == 0 and ch == '=' and pending is None:
            m = re.search(r'(\w+)\s*$', body[buf:i])
            if m:
                pending = m.group(1)
                buf = i + 1
        elif depth == 0 and ch == ',':
            if pending:
                out[pending] = body[buf:i].strip()
            pending = None
            buf = i + 1
        i += 1
    if pending:
        out[pending] = body[buf:].strip()
    return out


def class_index():
    """类名 -> 源文件（UI 工程内）。"""
    idx = {}
    for base, dirs, files in os.walk(UI):
        dirs.sort()   # 确定性顺序（ext4/NTFS 目录序不同，排序后跨平台可复现）
        for fn in sorted(files):
            if not fn.endswith(".cs"):
                continue
            path = os.path.join(base, fn)
            for m in re.finditer(r'^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+)*'
                                 r'(?:class|interface)\s+(\w+)', read(path), re.M):
                idx.setdefault(m.group(1), rel(path))
    return idx


def factory_index():
    """SidePanelFactory / ToolsDialogsLauncher 里 Create*/Open* 方法 -> 页面类名。"""
    out = {}
    for name in ("Services/SidePanelFactory.cs", "Services/ToolsDialogsLauncher.cs"):
        text = read(os.path.join(UI, name))
        for m in re.finditer(r'(?:public|private|internal)[^;{]*?\b(Create|Open)(\w+)\s*\(\s*[^)]*\)\s*', text):
            tail = text[m.end():]
            if not tail.lstrip().startswith(LB):
                continue
            body = brace_body(text, text.index(LB, m.end()))
            cls = None
            pm = NEW_PAGE.search(body)
            if pm:
                cls = pm.group(1)
            if cls:
                out.setdefault(m.group(1) + m.group(2), cls)
    return out


def method_body(name, cache={}):
    """在 UI 工程里找 name 方法定义的方法体（用于回调 → 协调器方法 的第二跳）。"""
    if name in cache:
        return cache[name]
    pat = re.compile(r'(?:public|private|internal|protected)[^;{}\(]*\b' + re.escape(name) + r'\s*\(')
    found = ""
    for base, dirs, files in os.walk(UI):
        dirs.sort()   # 同上：确定性顺序
        for fn in sorted(files):
            if not fn.endswith(".cs"):
                continue
            text = read(os.path.join(base, fn))
            m = pat.search(text)
            if not m:
                continue
            brace = text.find(LB, m.end())
            semi = text.find(";", m.end())
            if brace < 0 or (semi >= 0 and semi < brace):
                continue  # 抽象/表达式体，跳过
            found = brace_body(text, brace + 1)
            break
        if found:
            break
    cache[name] = found
    return found


def resolve_pages(leaves, callbacks, classes, factories):
    """给每个菜单叶解析出候选页面类与源文件。"""
    resolved = []
    for title, cb, icon in leaves:
        cands = []
        body = callbacks.get(cb, "") if cb else ""
        if body:
            for pm in CREATE_REF.finditer(body):
                key = "Create" + pm.group(1) + "Panel"
                if key in factories:
                    cands.append(factories[key])
            for pm in OPEN_REF.finditer(body):
                key = "Open" + pm.group(1)
                if key in factories:
                    cands.append(factories[key])
            for pm in NEW_PAGE.finditer(body):
                cands.append(pm.group(1))
            for pm in SHOW_PANEL.finditer(body):
                cands.append(pm.group(1) + "Panel")
            # 第二跳：回调只调了协调器方法（_openCoord.NewConnection()），页面在方法体里
            if not cands:
                for pm in re.finditer(r'\b(\w+)\s*\(\s*\)', body):
                    inner = method_body(pm.group(1))
                    if not inner:
                        continue
                    for q in CREATE_REF.finditer(inner):
                        key = "Create" + q.group(1) + "Panel"
                        if key in factories:
                            cands.append(factories[key])
                    for q in OPEN_REF.finditer(inner):
                        key = "Open" + q.group(1)
                        if key in factories:
                            cands.append(factories[key])
                    for q in NEW_PAGE.finditer(inner):
                        cands.append(q.group(1))
                    if cands:
                        break
        files = []
        for c in cands:
            f = classes.get(c)
            if f and f not in files:
                files.append(f)
        resolved.append({
            "menu": title,
            "callback": cb,
            "icon": icon,
            "classes": [c for c in dict.fromkeys(cands)],
            "files": files,
        })
    return resolved


def scan():
    menu = parse_menu()
    callbacks = parse_callbacks()
    classes = class_index()
    factories = factory_index()
    tree = []
    for l1, leaves in menu:
        tree.append({
            "level1": l1,
            "level2": resolve_pages(leaves, callbacks, classes, factories),
        })
    return tree


# --------------------------------------------------------------------------
# 2. 契约矩阵：能力格的判定
# --------------------------------------------------------------------------

CAP_LABEL = {
    "list": "列表/数据展示",
    "empty": "空态引导",
    "loading": "加载中/忙碌",
    "error": "失败提示",
    "confirm": "危险操作确认",
    "persist": "配置持久化",
    "search": "搜索/过滤",
    "sort": "排序",
    "paging": "分页/虚拟滚动",
    "bulk": "多选/批量",
    "export": "导出",
    "import": "导入",
    "undo": "撤销/回收站",
    "keyboard": "键盘/快捷键",
    "activation": "回车/双击激活",
    "a11y": "无障碍名称",
    "import": "导入",
    "validation": "输入校验",
}

# type -> 必需的「完备性能力」（缺了就是真缺口）；加分项不计分但决定"简陋"感
TYPE_CAPS = {
    # 列表页：数据 + 空态 + 失败反馈 + 激活路径
    "list": {"required": ["list", "empty", "error", "activation"],
             "optional": ["search", "sort", "paging", "bulk", "export", "undo", "loading", "keyboard", "a11y"]},
    # 常驻面板：容器，空态是硬要求
    "panel": {"required": ["empty"],
              "optional": ["list", "activation", "search", "error", "loading", "persist", "keyboard", "a11y"]},
    # 监控面板：采样有等待也有失败
    "monitor": {"required": ["loading", "error"],
                "optional": ["list", "paging", "keyboard", "export", "empty"]},
    "progress": {"required": ["loading", "error"],
                 "optional": ["keyboard", "undo"]},
    # 设置页：校验 + 落盘 + 键盘
    "settings": {"required": ["validation", "persist", "keyboard"],
                 "optional": ["error", "confirm", "a11y", "undo", "search", "import", "export"]},
    # 录入对话框：校验 + 键盘（落盘由调用方负责）
    "input": {"required": ["validation", "keyboard"],
              "optional": ["persist", "error", "a11y", "confirm", "undo"]},
    # 确认/决策对话框
    "dialog": {"required": ["keyboard"],
               "optional": ["error", "confirm", "validation", "a11y"]},
    "wizard": {"required": ["validation", "keyboard", "persist"],
               "optional": ["error", "confirm", "a11y"]},
    "editor": {"required": ["validation", "persist", "keyboard"],
               "optional": ["error", "list", "undo", "a11y"]},
}

# 能力 -> 源码证据（任一命中即算有证据）
DETECT = {
    # AntdUI.Table/DataSource 也是列表（KeePass 系页面用自绘表格而不是 ListView）；
    # DrawString = 自绘图表/指标展示（监控面板用 GDI 画曲线/进度条，不是列表控件但确实在展示数据）；
    # AddRow(FlowLayoutPanel host = 行构造器（欢迎页把最近连接/收藏渲染成交互行；
    # 连接对话框的 AddRow<T>(TableLayoutPanel) 是表单字段行构造，不匹配）
    "list": [r'ListView|DataGridView|TreeView|ListViewEx|Items\.Add|AntdUI\.Table|DataSource\s*=|DrawString|AddRow\(\s*FlowLayoutPanel'],
    "activation": [r'DoubleClick|KeyDown|ItemActivate|SelectedIndexChanged|Keys\.Enter'],
    "empty": [r'EmptyStateView'],
    # 加载态证据三类：① BusyScope 作用域（用户触发的长任务）② 显式加载标志位
    # （_loading / isLoading / SetLoading——被动采样类面板用它，不能为 5 秒一轮的采样
    # 每次都切 WaitCursor）③ 跑马灯进度条（总大小未知时表示"在动"）。
    "loading": [r'BusyScope|_loading|_busy|Loading|IsBusy|SetBusy|ProgressBarStyle\.Marquee|MarqueeAnimationSpeed'],
    "error": [r'Message\.error|Message\.Error|MessageBox\.Show|DiagLog\.Error|Message\.warn|Message\.Warn|ToastNotifier\.Error|(保存|修改|导入|操作|加载|写入|读取|清空|恢复)失败'],
    # 确认证据：Modal.open 是 AntdUI 确认弹窗（修 2026-11 双反斜杠转义 bug——
    # r'Modal\\.open' 实际匹配带字面反斜杠的文本，永远不中）；
    # MessageBoxButtons.YesNo 是 WinForms 原生确认框的标准写法（代码库统一口径）。
    # 不收裸「确定」——「DPI 下确定」「不加确定按钮」这类布局注释会造成误报。
    "confirm": [r'ConfirmDanger|MessageModal|Modal\.open|确认|MessageBoxButtons\.YesNo'],
    "persist": [r'Store\b|\.Save\(|SaveConfig|WriteAllText|WriteAllLines|FormSettings\.(Set|Get)'],
    "search": [r'PlaceholderText\s*=\s*"[^"]*(?:搜索|过滤|查找)|Filter\(|\.Search\(|搜索|过滤|筛选'],
    "sort": [r'SortOrder|Sorting\s*=|CompareFunc|排序'],
    "paging": [r'PageSize|分页|VirtualMode|VirtualListSize|ScrollTo'],
    # 批量能力的证据：全选入口、勾选列（CheckBoxes）、多选列表（MultiSelect=true，
    # 搭配 SelectedItems 批处理——如 SFTP 文件栏多选批量删/传、命令历史多选批量删）。
    # TableMultiSelect：AntdUI.Table 多选层（控件本身单选，CellClick+Ctrl/Shift 自持选中集，
    # 搭配 GetSelectedRows 批处理——危险命令规则/快捷键绑定/密码健康报告）。
    # 注意别把“多选”三个字当证据——注释里写“多选批量”不算实现过。
    "bulk": [r'SelectAllRows|全选|CheckedItems|CheckedListBox|CheckBoxes|MultiSelect\s*=\s*true|TableMultiSelect|GetSelectedRows'],
    "export": [r'导出|Export\('],
    "import": [r'导入|Import\('],
    "undo": [r'撤销|Undo|回收站|Restore\('],
    "keyboard": [r'Keys\.|ShortcutKeys|TabStop|AcceptButton|CancelButton|KeyDown'],
    "a11y": [r'UiA11y'],
    "validation": [r'Message\.warn|Message\.Warn|MessageBox\.Show|不能为空|请先|Validate|校验'],
}


def detect_caps(files):
    """对页面源文件跑证据正则；返回 {cap: 命中的文件列表}。"""
    texts = {f: read(os.path.join(ROOT, f)) for f in files}
    hits = {}
    for cap, pats in DETECT.items():
        where = []
        for f, t in texts.items():
            if not t:
                continue
            for p in pats:
                if re.search(p, t):
                    where.append(f)
                    break
        if where:
            hits[cap] = where
    return hits


REGISTRY = os.path.join(ROOT, ".codestable", "tools", "ui-coverage-registry.txt")


def dump_index():
    """覆盖登记表：UI 相对路径 -> dump 名 / manual / none。"""
    idx = {}
    for line in read(REGISTRY).splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "|" not in line:
            continue
        path, rest = line.split("|", 1)
        idx[path.strip()] = rest.strip()
    return idx


def dump_for(files, reg):
    for f in files:
        short = f.split("Gdterm.UI/", 1)[1] if "Gdterm.UI/" in f else f
        v = reg.get(short)
        if v and v.startswith("dump:"):
            return v.split(":", 1)[1].strip()
    return None


def load_contracts():
    if not os.path.exists(CONTRACTS):
        return {"pages": [], "baseline": {"red": 0}}
    with io.open(CONTRACTS, encoding="utf-8") as f:
        return json.load(f)


def audit(tree, contracts):
    pages = {p["id"]: p for p in contracts.get("pages", [])}
    by_file = {}
    for pid, p in pages.items():
        for src in p.get("src", []):
            by_file.setdefault(src, pid)

    rows = []
    unregistered = []
    for l1 in tree:
        for leaf in l1["level2"]:
            pid = None
            for f in leaf["files"]:
                if f in by_file:
                    pid = by_file[f]
                    break
            page = pages.get(pid) if pid else None
            if page is None and leaf["files"]:
                unregistered.append({"menu": l1["level1"], "leaf": leaf["menu"],
                                     "files": leaf["files"]})
            elif page is None:
                # 动作类菜单（退出/重连/关闭/分割…）：允许无页面契约，但必须显式登记 kind=action
                act = [p for p in pages.values() if p.get("kind") == "action"
                       and clean_leaf(p.get("menu_leaf", "")) == clean_leaf(leaf["menu"])]
                if not act:
                    unregistered.append({"menu": l1["level1"], "leaf": leaf["menu"], "files": []})
            rows.append({"level1": l1["level1"], "leaf": leaf["menu"], "page": page,
                         "files": leaf["files"]})

    # 每个页面：适用能力、证据、缺口
    reg = dump_index()
    detail = []
    for pid, p in pages.items():
        if p.get("kind") == "action":
            continue
        ptype = p.get("type", "panel")
        spec = TYPE_CAPS.get(ptype, TYPE_CAPS["panel"])
        drop = set(p.get("drop_caps", []))
        caps = [c for c in spec["required"] if c not in drop]
        extras = [c for c in spec["optional"] if c not in drop]
        hits = detect_caps(p.get("src", []))
        known = set(p.get("known_gaps", []))
        judged = []
        for c in caps:
            if c in hits:
                state = "ok"
            elif c in known:
                state = "debt"
            elif c in p.get("manual_ok", []):
                state = "manual"
            else:
                state = "gap"
            judged.append({"cap": c, "label": CAP_LABEL.get(c, c), "state": state,
                           "evidence": hits.get(c, [])})
        done = sum(1 for j in judged if j["state"] in ("ok", "manual"))
        bonus = [{"cap": c, "label": CAP_LABEL.get(c, c), "state": "ok" if c in hits else "todo"}
                 for c in extras]
        detail.append({
            "id": pid, "name": p.get("name", pid), "menu": p.get("menu", ""),
            "type": ptype, "src": p.get("src", []),
            "dump": p.get("dump") or dump_for(p.get("src", []), reg),
            "score": (100 * done // len(caps)) if caps else 100,
            "done": done, "total": len(caps),
            "gaps": [j for j in judged if j["state"] == "gap"],
            "debt": [j for j in judged if j["state"] == "debt"],
            "bonus": bonus, "judged": judged, "note": p.get("note", ""),
        })
    detail.sort(key=lambda d: (d["score"], d["id"]))
    return rows, unregistered, detail


# --------------------------------------------------------------------------
# 3. 渲染
# --------------------------------------------------------------------------

def render(tree, rows, unregistered, detail):
    out = []
    w = out.append
    total = len(detail)
    gap_n = sum(1 for d in detail if d["gaps"])
    w("# 功能大纲 × 页面契约矩阵")
    w("")
    w("> 生成物：`python3 tools/feature-map.py --write`（勿手改）。契约数据在 `docs/feature-contracts.json`。")
    w("> 机制说明见 §0；判定口径见 §2。")
    w("")
    w("## 0. 这套机制在判什么")
    w("")
    w("布局门（ui-ux-lint / ui-tree-check）判「画得对不对」；**这张表判「做没做完」**。")
    w("三条来自业界实践的判据：")
    w("")
    w("1. **先列全页面**（IA audit）：入口树从代码自动抽取（菜单 → 回调 → 工厂方法 → 页面类 → 源文件），")
    w("   不靠人回忆，所以「有页面没入口」和「有入口没页面」都会被点名。")
    w("2. **状态清单**：一个页面最少有 空 / 零匹配 / 加载中 / 失败 / 无权限 / 不可逆确认 六态。")
    w("   只画 happy path 的页面，设计只完成了三分之一。")
    w("3. **列表页 DoD**：能看（列表）、能找（搜索/过滤）、能挑（多选批量）、能走（分页/虚拟滚动）、")
    w("   能带出（导出）、能回来（撤销/回收站）。缺哪个就写哪个。")
    w("")
    w("能力格的三档判定——**判不了的必须显式标人工，不许假装有门**：")
    w("")
    w("| 档 | 含义 | 谁来判 |")
    w("|---|---|---|")
    w("| `ok` | 源码里有该类能力的证据（组件/API 调用） | 正则证据（本工具） |")
    w("| `manual` | 有实现但证据不在源码特征里 | 人工在 `manual_ok` 签字 |")
    w("| `debt` | 已知缺失，登记在 `known_gaps`（棘轮：只减不增） | 人工 |")
    w("| `gap` | **未发现证据且未登记** —— 这就是待办 | 本工具 |")
    w("")
    w("## 1. 入口大纲（L1 → L2）")
    w("")
    w("共 %d 个一级菜单 / %d 个二级入口。" % (len(tree), sum(len(t["level2"]) for t in tree)))
    w("")
    for l1 in tree:
        w("### %s" % l1["level1"])
        w("")
        w("| L2 入口 | 页面/类 | 源文件 | 契约 |")
        w("|---|---|---|---|")
        for leaf in l1["level2"]:
            page = next((r["page"] for r in rows
                         if r["level1"] == l1["level1"] and r["leaf"] == leaf["menu"]), None)
            cls = ", ".join(leaf["classes"]) if leaf["classes"] else "—"
            fil = "<br>".join("`%s`" % f for f in leaf["files"]) if leaf["files"] else "—"
            mark = ("✅ `%s`" % page["id"]) if page else "（动作，无需页面）" if not leaf["files"] else "❌ 未登记契约"
            w("| %s | %s | %s | %s |" % (leaf["menu"], cls, fil, mark))
        w("")

    w("## 2. 完备性矩阵（按缺口排序，最烂的在最上面）")
    w("")
    w("%d 个页面，其中 %d 个存在未登记缺口。" % (total, gap_n))
    w("")
    w("| 页面 | 一级菜单 | 类型 | 完备度 | 缺口 | dump |")
    w("|---|---|---|---|---|---|")
    for d in detail:
        gaps = "、".join(g["label"] for g in d["gaps"]) or "—"
        w("| %s `%s` | %s | %s | %d%% (%d/%d) | %s | %s |" % (
            d["name"], d["id"], d["menu"], d["type"], d["score"], d["done"], d["total"], gaps,
            d["dump"] or "—"))
    w("")

    w("## 3. 待补清单（按页面展开）")
    w("")
    hot = [d for d in detail if d["gaps"]]
    if not hot:
        w("无。")
    for d in hot:
        w("### %s（%s，%d%%）" % (d["name"], d["id"], d["score"]))
        w("")
        w("- 源文件：%s" % "、".join("`%s`" % s for s in d["src"]))
        if d["dump"]:
            w("- 结构 dump：`%s`" % d["dump"])
        for g in d["gaps"]:
            w("- [ ] **%s**（%s）" % (g["label"], g["cap"]))
        if d["debt"]:
            w("- 已知债：%s" % "、".join(g["label"] for g in d["debt"]))
        if d["note"]:
            w("- 备注：%s" % d["note"])
        w("")

    w("## 4. 加分项缺口（非必需，但决定「简陋感」）")
    w("")
    w("必需项是「不做就是功能残缺」；这一节是不做也能用、但同行都有的东西——")
    w("搜索/排序/分页/多选批量/导出/撤销/加载态。按缺得最多排序。")
    w("")
    rate = [d for d in detail if d["bonus"] and any(b["state"] == "todo" for b in d["bonus"])]
    rate.sort(key=lambda d: (-sum(1 for b in d["bonus"] if b["state"] == "todo"), d["name"]))
    w("| 页面 | 已有/总数 | 缺口 |")
    w("|---|---|---|")
    for d in rate:
        miss = [b["label"] for b in d["bonus"] if b["state"] == "todo"]
        have = sum(1 for b in d["bonus"] if b["state"] == "ok")
        w("| %s `%s` | %d/%d | %s |" % (d["name"], d["id"], have, len(d["bonus"]), "、".join(miss)))
    w("")

    if unregistered:
        w("## 5. 未登记入口（必须处理）")
        w("")
        w("| L1 | L2 入口 | 解析到的文件 |")
        w("|---|---|---|")
        for u in unregistered:
            w("| %s | %s | %s |" % (u["menu"], u["leaf"],
                                    "、".join("`%s`" % f for f in u["files"]) or "（无法解析）"))
        w("")
    return "\n".join(out) + "\n"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scan", action="store_true")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--json", metavar="PATH")
    ap.add_argument("--only")
    ap.add_argument("--ascii", action="store_true",
                    help="CI 控制台是 GBK，中文会变乱码——只输出 ASCII 摘要")
    a = ap.parse_args()

    tree = scan()
    if a.only:
        tree = [t for t in tree if a.only in t["level1"]]
    if a.scan:
        for t in tree:
            print("== %s" % t["level1"])
            for leaf in t["level2"]:
                print("   - %-42s %-28s %s" % (leaf["menu"], leaf["callback"] or "",
                                               ",".join(leaf["files"]) or "(未解析)"))
        return 0

    contracts = load_contracts()
    rows, unregistered, detail = audit(tree, contracts)
    doc = render(tree, rows, unregistered, detail)

    if a.json:
        with io.open(a.json, "w", encoding="utf-8") as f:
            json.dump({"tree": tree, "unregistered": unregistered, "pages": detail},
                      f, ensure_ascii=False, indent=2)

    if a.write:
        old = read(DOCTARGET)
        if old != doc:
            with io.open(DOCTARGET, "w", encoding="utf-8") as f:
                f.write(doc)
            print("feature-map: 已写入 %s" % rel(DOCTARGET))
        else:
            print("feature-map: %s 无变化" % rel(DOCTARGET))
        return 0

    if a.check:
        ok = True
        old = read(DOCTARGET)
        if old != doc:
            print("feature-map: docs/FEATURE-MAP.md inconsistent with code/contracts -- run --write")
            ok = False
            # 诊断（2026-07 跨平台排查）：定位第一个差异行，输出两侧 repr（纯 ASCII）
            try:
                print("DIAG len(old)=%d len(doc)=%d" % (len(old), len(doc)))
                print("DIAG old_head=%r" % old[:120])
                print("DIAG doc_head=%r" % doc[:120])
                o_lines, d_lines = old.splitlines(), doc.splitlines()
                print("DIAG old_lines=%d doc_lines=%d" % (len(o_lines), len(d_lines)))
                shown = 0
                for i in range(max(len(o_lines), len(d_lines))):
                    ov = o_lines[i] if i < len(o_lines) else "<EOF>"
                    dv = d_lines[i] if i < len(d_lines) else "<EOF>"
                    if ov != dv:
                        print("DIAG line %d OLD: %r" % (i + 1, ov[:160]))
                        print("DIAG line %d NEW: %r" % (i + 1, dv[:160]))
                        shown += 1
                        if shown >= 5:
                            break
            except Exception as ex:
                print("DIAG (diag failed: %s)" % ex)
        if unregistered:
            print("feature-map: %d 个一级菜单入口没有登记页面契约：" % len(unregistered))
            for u in unregistered:
                print("   %s > %s  %s" % (u["menu"], u["leaf"],
                                          ",".join(u["files"]) or "(无法解析)"))
            ok = False
        gaps = sum(len(d["gaps"]) for d in detail)
        debt = sum(len(d["debt"]) for d in detail)
        floor = contracts.get("baseline", {}).get("gaps", 999)
        ascii_only = a.ascii or os.environ.get("APPVEYOR") == "True"
        if ascii_only:
            print("feature-map: pages=%d gaps=%d baseline=%d debt=%d unregistered=%d"
                  % (len(detail), gaps, floor, debt, len(unregistered)))
        else:
            print("feature-map: 页面 %d；缺口 %d（基线 %d）；已知债 %d；未登记入口 %d"
                  % (len(detail), gaps, floor, debt, len(unregistered)))
        if gaps > floor:
            print("feature-map: gap count exceeds baseline (ratchet: only decrease)" if ascii_only
                  else "feature-map: 缺口数超过基线（棘轮只降不升）")
            ok = False
        print("FEATURE-MAP-CHECK %s" % ("ALL OK" if ok else "FAILED"))
        return 0 if ok else 1

    sys.stdout.write(doc)
    print("feature-map: 页面 %d；有缺口 %d；未登记入口 %d"
          % (len(detail), sum(1 for d in detail if d["gaps"]), len(unregistered)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
