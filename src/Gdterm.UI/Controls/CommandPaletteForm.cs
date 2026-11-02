using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>命令面板的一条命令：展示文本 + 任意动作。</summary>
    public sealed class PaletteCommand
    {
        public string Label { get; set; }
        public string Category { get; set; }    // 分组展示（命令/视图/面板/工具/设置/帮助/标签/连接）
        public string Hotkey { get; set; }      // 可选快捷键展示
        public string Keywords { get; set; }    // 附加搜索词（主机/协议等）
        public Action Action { get; set; }
    }

    /// <summary>
    /// 命令面板（nyaterm 对比吸收 A1）：Ctrl+Shift+O（Ctrl+Shift+P 已被片段搜索占用）。
    /// 把菜单动作、快捷键、已开标签、连接列表统一到一个输入入口。
    /// 结构与 ConnectionQuickJumpForm 一致（AntdUI.Window + Input + ListBox），
    /// 过滤沿用项目既有的大小写不敏感 Contains 策略。
    /// </summary>
    public sealed class CommandPaletteForm : AntdUI.Window
    {
        private readonly AntdUI.Input _filter;
        private readonly ListBox _list;
        private readonly List<PaletteCommand> _all;

        public PaletteCommand Selected { get; private set; }

        public CommandPaletteForm(IList<PaletteCommand> commands)
        {
            _all = commands != null ? new List<PaletteCommand>(commands) : new List<PaletteCommand>();
            Text = "命令面板 (Ctrl+Shift+O)";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            AutoHandDpi = false; // 尺寸已按 DpiScale 预缩放，关掉 AntdUI 的自动 DPI 避免二次放大
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = DpiScale.S(this, 560, 400);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            KeyPreview = true;
            ShowInTaskbar = false;

            _filter = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this)),
                PlaceholderText = "输入命令 / 标签 / 连接…（如：新建、sftp、prod）",
                AllowClear = true,
                Font = Services.FormFontPolicy.UiFont(+1f)
            };
            _filter.TextChanged += (s, e) => ApplyFilter();
            _filter.KeyDown += OnFilterKey;

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                IntegralHeight = false
            };
            _list.DoubleClick += (s, e) => AcceptSelection();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { AcceptSelection(); e.Handled = true; }
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            Controls.Add(_list);
            Controls.Add(_filter);
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            ApplyFilter();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        private void ApplyFilter()
        {
            var q = (_filter.Text ?? "").Trim().ToLowerInvariant();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in _all)
            {
                if (c == null || c.Action == null) continue;
                var line = FormatLine(c);
                if (q.Length == 0
                    || line.ToLowerInvariant().Contains(q)
                    || (c.Label ?? "").ToLowerInvariant().Contains(q)
                    || (c.Category ?? "").ToLowerInvariant().Contains(q)
                    || (c.Keywords ?? "").ToLowerInvariant().Contains(q))
                {
                    _list.Items.Add(new Item { Command = c, Text = line });
                }
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }

        private static string FormatLine(PaletteCommand c)
        {
            var parts = new List<string> { c.Label ?? "" };
            if (!string.IsNullOrEmpty(c.Category)) parts.Add("[" + c.Category + "]");
            if (!string.IsNullOrEmpty(c.Hotkey)) parts.Add("(" + c.Hotkey + ")");
            return string.Join("  ", parts);
        }

        private void OnFilterKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                _list.Focus();
                if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                AcceptSelection();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        private void AcceptSelection()
        {
            var item = _list.SelectedValue as Item;
            if (item == null && _list.Items.Count > 0) item = _list.Items[0] as Item;
            if (item == null) return;
            Selected = item.Command;
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class Item
        {
            public PaletteCommand Command;
            public string Text;
            public override string ToString() { return Text; }
        }
    }
}
