using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 无障碍名称统一入口（UI/UX 审计 2026-10-07 C5）。
    ///
    /// 背景：全仓 <c>AccessibleName</c>/<c>AccessibleDescription</c> 命中数为 0 ——
    /// AntdUI 自绘控件与纯符号按钮（“×”“+”“−”“↑”“↓”“…”）对 UI Automation
    /// 是空节点，NVDA/讲述人读不出用途，键盘用户也拿不到可读标签。
    ///
    /// 三条路径：
    ///   1. 显式：<c>new AntdUI.Button{...}.A11y("关闭标签")</c>（图标/符号钮必须显式）；
    ///   2. 自动：<see cref="AutoName"/> 由可见 Text 回填（表单/文本类控件一次覆盖）；
    ///   3. 符号表：<see cref="SymbolName"/> 把常见字形映射成中文语义，供路径 2 使用。
    ///
    /// AutoName 在 FormFontPolicy.Apply / DialogStyle.ApplyChrome 末尾自动调用，
    /// 因此新窗体不需要逐个手写也能拿到基础名称。
    /// </summary>
    public static class UiA11y
    {
        /// <summary>常见符号的语义名（含全角/半角与几何图形变体）。</summary>
        private static readonly Dictionary<string, string> Symbols = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "×", "关闭" }, { "✕", "关闭" }, { "✖", "关闭" }, { "╳", "关闭" },
            { "+", "添加" }, { "＋", "添加" },
            { "−", "移除" }, { "-", "移除" }, { "－", "移除" },
            { "↑", "上移" }, { "↓", "下移" },
            { "←", "后退" }, { "→", "前进" },
            { "…", "更多" }, { "...", "更多" },
            { "▸", "展开" }, { "▾", "折叠" },
            { "⚙", "设置" }, { "🔒", "锁定" }, { "🔓", "解锁" },
            { "× ", "关闭" }
        };

        /// <summary>字形 → 语义名；非符号返回 null。</summary>
        public static string SymbolName(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var t = text.Trim();
            string hit;
            return Symbols.TryGetValue(t, out hit) ? hit : null;
        }

        /// <summary>显式命名（链式）：设置 AccessibleName，未填 Description 时一并补上。</summary>
        public static T A11y<T>(this T control, string name, string description = null) where T : Control
        {
            if (control == null) return control;
            if (!string.IsNullOrWhiteSpace(name)) control.AccessibleName = name;
            control.AccessibleDescription = string.IsNullOrWhiteSpace(description) ? name : description;
            return control;
        }

        /// <summary>
        /// 递归回填名称：只填“空名”的控件，不覆盖显式命名。
        /// 取值顺序：可见 Text → 符号语义名。纯装饰（空 Text）控件跳过。
        /// </summary>
        public static void AutoName(Control root)
        {
            if (root == null) return;
            var stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Control c;
                try { c = stack.Pop(); } catch { break; }
                if (c == null) continue;
                try
                {
                    if (string.IsNullOrWhiteSpace(c.AccessibleName))
                    {
                        var text = (c.Text ?? string.Empty).Trim();
                        string name = null;
                        if (text.Length > 0)
                        {
                            // 纯符号 → 语义名；普通文本 → 文本本身（截断到 60 字，避免整段说明进名称）
                            name = SymbolName(text) ?? (text.Length > 60 ? text.Substring(0, 60) : text);
                        }
                        if (!string.IsNullOrEmpty(name))
                        {
                            c.AccessibleName = name;
                            if (string.IsNullOrWhiteSpace(c.AccessibleDescription))
                                c.AccessibleDescription = name;
                        }
                    }
                }
                catch { }
                try
                {
                    foreach (Control child in c.Controls) stack.Push(child);
                }
                catch { }
            }
        }

        /// <summary>
        /// 图标/符号按钮工厂：一次设齐 Text + AccessibleName + tooltip + 手型光标。
        /// 这三件事以前散落在每个调用点，且“漏设 AccessibleName”不会有人发现。
        /// </summary>
        public static AntdUI.Button Icon(string glyph, string accessibleName, string tooltip = null)
        {
            var b = new AntdUI.Button
            {
                Text = glyph,
                AutoSize = true,
                Cursor = Cursors.Hand,
                AccessibleName = accessibleName,
                AccessibleDescription = string.IsNullOrEmpty(tooltip) ? accessibleName : tooltip
            };
            if (!string.IsNullOrEmpty(tooltip))
            {
                try { b.ToolTipText2(tooltip); } catch { }
            }
            return b;
        }
    }
}
