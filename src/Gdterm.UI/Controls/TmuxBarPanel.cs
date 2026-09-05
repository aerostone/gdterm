using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// tmux 快捷面板——参考 webtmux 移动端工具栏的分组设计，为 PC 优化：
    ///   1. 两行分组布局（窗口/面板/复制/会话 | Ctrl 键/翻页），代替手机单行横滚
    ///   2. 前缀选择器（C-b 默认 / C-a screen 兼容），一次点击完成 prefix+key
    ///   3. 去掉移动端拐杖（方向键/Esc/Tab 行）——PC 有键盘，保留高频 tmux 动作
    ///   4. 分组标签 + 组分隔线，视觉对齐 GitHub Dark 主题（GdtermColorTable）
    /// 发送路径：TerminalControl.TrySendInput(raw)——字符直通，绕过危险命令闸门
    /// （tmux 命令是控制序列，不是 shell 命令行，不应触发确认弹窗）。
    /// </summary>
    public class TmuxBarPanel : UserControl
    {
        private readonly Action<string> _send;
        private string _prefix = "\u0002"; // C-b 默认
        private FlowLayoutPanel _row1;
        private FlowLayoutPanel _row2;
        private AntdUI.Select _prefixBox;

        /// <summary>面板发送的原始字节（含前缀）已进入终端时触发（用于审计/调试）。</summary>
        public event Action<string> RawSent;

        public TmuxBarPanel(Action<string> send)
        {
            _send = send ?? (s => { });
            BuildUI();
        }

        // ── 键定义 ──────────────────────────────────────────────
        // key: tmux 前缀后的键（原样字节）；label: 按钮文案；tip: 工具提示
        private sealed class KeyDef
        {
            public string Label;
            public string Key;      // null = 原始串直发（无前缀）
            public string Raw;      // 直接发送（无前缀），如 ^C
            public string Tip;
        }

        private sealed class GroupDef
        {
            public string Name;
            public KeyDef[] Keys;
            public bool Row2;       // 放第二行
        }

        private static readonly GroupDef[] Groups = new[]
        {
            new GroupDef
            {
                Name = "窗口",
                Keys = new[]
                {
                    new KeyDef { Label = "◀ 窗口", Key = "p", Tip = "上一个窗口 (prefix+p)" },
                    new KeyDef { Label = "窗口 ▶", Key = "n", Tip = "下一个窗口 (prefix+n)" },
                    new KeyDef { Label = "新建", Key = "c", Tip = "新建窗口 (prefix+c)" },
                    new KeyDef { Label = "列表", Key = "w", Tip = "窗口列表 (prefix+w)" },
                    new KeyDef { Label = "关闭", Key = "&", Tip = "关闭当前窗口 (prefix+&)" },
                }
            },
            new GroupDef
            {
                Name = "面板",
                Keys = new[]
                {
                    new KeyDef { Label = "│ 左右分", Key = "%", Tip = "垂直分割 (prefix+%)" },
                    new KeyDef { Label = "─ 上下分", Key = "\"", Tip = "水平分割 (prefix+\")" },
                    new KeyDef { Label = "◀ 切换", Key = "o", Tip = "切换到下一面板 (prefix+o)" },
                    new KeyDef { Label = "放大", Key = "z", Tip = "面板全屏切换 (prefix+z)" },
                    new KeyDef { Label = "关闭面板", Key = "x", Tip = "关闭当前面板 (prefix+x)" },
                }
            },
            new GroupDef
            {
                Name = "复制",
                Keys = new[]
                {
                    new KeyDef { Label = "复制模式", Key = "[", Tip = "进入 copy-mode (prefix+[)" },
                    new KeyDef { Label = "粘贴", Key = "]", Tip = "粘贴缓冲区 (prefix+])" },
                    new KeyDef { Label = "翻页 ▲", Raw = "\u001b[5~", Tip = "PgUp 翻页" },
                    new KeyDef { Label = "翻页 ▼", Raw = "\u001b[6~", Tip = "PgDn 翻页" },
                }
            },
            new GroupDef
            {
                Name = "会话",
                Keys = new[]
                {
                    new KeyDef { Label = "会话列表", Key = "s", Tip = "session 列表 (prefix+s)" },
                    new KeyDef { Label = "脱离", Key = "d", Tip = "detach (prefix+d)" },
                }
            },
            // ── 第二行：无前缀的原始控制键（PC 键盘易达但拇指区外的高频键）──
            new GroupDef
            {
                Name = "Ctrl",
                Row2 = true,
                Keys = new[]
                {
                    new KeyDef { Label = "^C", Raw = "\u0003", Tip = "Ctrl+C 中断" },
                    new KeyDef { Label = "^D", Raw = "\u0004", Tip = "Ctrl+D EOF" },
                    new KeyDef { Label = "^Z", Raw = "\u001a", Tip = "Ctrl+Z 挂起" },
                    new KeyDef { Label = "^L", Raw = "\u000c", Tip = "Ctrl+L 清屏" },
                    new KeyDef { Label = "^R", Raw = "\u0012", Tip = "Ctrl+R 反向搜索" },
                }
            },
            new GroupDef
            {
                Name = "编辑",
                Row2 = true,
                Keys = new[]
                {
                    new KeyDef { Label = "Home", Raw = "\u001b[H", Tip = "行首" },
                    new KeyDef { Label = "End", Raw = "\u001b[F", Tip = "行尾" },
                    new KeyDef { Label = "^A", Raw = "\u0001", Tip = "行首 (readline)" },
                    new KeyDef { Label = "^E", Raw = "\u0005", Tip = "行尾 (readline)" },
                    new KeyDef { Label = "^U", Raw = "\u0015", Tip = "删除整行 (readline)" },
                    new KeyDef { Label = "^W", Raw = "\u0017", Tip = "删除单词 (readline)" },
                    new KeyDef { Label = "^K", Raw = "\u000b", Tip = "删除到行尾 (readline)" },
                }
            },
        };

        // ── UI 构建 ──────────────────────────────────────────────
        private void BuildUI()
        {
            Dock = DockStyle.Bottom;
            Height = 64;
            BackColor = GdtermColorTable.Surface;

            // 前缀选择器（左侧竖排：标签 + 下拉）
            var prefixPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 70,
                BackColor = GdtermColorTable.Surface
            };
            var prefixLabel = new AntdUI.Label {
                Text = "前缀",
                AutoSize = true,
                Location = new Point(8, 6),
                ForeColor = GdtermColorTable.Muted
            };
            _prefixBox = new AntdUI.Select {
                Location = new Point(6, 26),
                Size = new Size(56, 21),
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", 9f)
            };
            _prefixBox.Items.Add("C-b");
            _prefixBox.Items.Add("C-a");
            _prefixBox.SelectedIndex = 0;
            _prefixBox.SelectedIndexChanged += (s, e) =>
                _prefix = _prefixBox.SelectedIndex == 1 ? "\u0001" : "\u0002";
            prefixPanel.Controls.Add(prefixLabel);
            prefixPanel.Controls.Add(_prefixBox);

            // 两行按钮区
            var rows = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = GdtermColorTable.Surface,
                Margin = new Padding(0)
            };
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            _row1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Margin = new Padding(0)
            };
            _row2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Margin = new Padding(0)
            };
            rows.Controls.Add(_row1, 0, 0);
            rows.Controls.Add(_row2, 0, 1);

            foreach (var g in Groups)
                AddGroup(g);

            Controls.Add(rows);
            Controls.Add(prefixPanel);
        }

        private void AddGroup(GroupDef g)
        {
            var row = g.Row2 ? _row2 : _row1;
            if (row.Controls.Count > 0)
                row.Controls.Add(MakeSeparator());

            // 分组标签（竖排文字太挤，用小号灰色标签）
            var tag = new AntdUI.Label {
                Text = g.Name,
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Padding = new Padding(2, 4, 2, 0),
                Margin = new Padding(1, 0, 0, 0)
            };
            if (g.Row2) tag.Padding = new Padding(2, 2, 2, 0);
            row.Controls.Add(tag);

            foreach (var k in g.Keys)
            {
                var b = new AntdUI.Button {
                    Text = k.Label,
                    AutoSize = true,
                    BackColor = GdtermColorTable.Background,
                    ForeColor = GdtermColorTable.Foreground,
                    Font = Services.FormFontPolicy.UiFont(-0.75f),
                    Margin = new Padding(1, 2, 1, 2),
                    TabStop = false,
                    Tag = k
                };
                if (k.Tip != null) b.ToolTipText2(k.Tip);
                b.Click += OnKeyClick;
                row.Controls.Add(b);
            }
        }

        private void OnKeyClick(object sender, EventArgs e)
        {
            var k = (sender as Button)?.Tag as KeyDef;
            if (k == null) return;

            string payload;
            if (k.Raw != null)
                payload = k.Raw;                       // 无前缀直发（^C/PgUp…）
            else
                payload = _prefix + k.Key;             // prefix + key

            try { RawSent?.Invoke(payload); } catch { }
            _send(payload);
        }

        private Control MakeSeparator()
        {
            return new Panel
            {
                Size = new Size(1, 22),
                Margin = new Padding(2, 6, 2, 6),
                BackColor = GdtermColorTable.Border
            };
        }
    }

    /// <summary>Button 的 ToolTip 扩展（避免每个按钮创建独立 ToolTip 组件）。</summary>
    internal static class ButtonTipExtension
    {
        private static readonly ToolTip Tip = new ToolTip();

        public static void ToolTipText2(this Button b, string text)
        {
            Tip.SetToolTip(b, text);
        }
    }
}
