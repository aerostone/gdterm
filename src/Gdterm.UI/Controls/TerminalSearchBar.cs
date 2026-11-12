using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 终端搜索栏——Ctrl+Shift+F 弹出，正则/大小写/全字匹配，上下跳转
    /// </summary>
    public class TerminalSearchBar : Panel
    {
        private FlowLayoutPanel _flow;
        private AntdUI.Input _txtSearch;
        private AntdUI.Checkbox _chkRegex, _chkCase, _chkWholeWord;
        private AntdUI.Button _btnPrev, _btnNext, _btnClose;
        private AntdUI.Label _lblCount;

        public event Action<string, bool, bool, bool> SearchRequested; // pattern, caseSensitive, regex, wholeWord
        public event Action<bool> NavigateRequested; // true=next, false=prev
        public event Action CloseRequested;

        public TerminalSearchBar()
        {
            Dock = DockStyle.Top;
            Height = DpiScale.V(this, 36);
            BackColor = GdtermColorTable.Surface;
            Visible = false;
            // 构造期就吃全局 UI 字号：本控件是 UserControl，Font 此时还是系统默认值，
            // 后面所有 MeasureTextWidth/LineBox 都按 9pt 量（字号轴 1.00 实证）
            Font = Services.FormFontPolicy.UiFont();
            Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 4), DpiScale.V(this, 8), DpiScale.V(this, 4));
            BuildUI();
            Relayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Relayout();
        }

        /// <summary>
        /// 单行排不下就换行（不该横向溢出）。
        ///
        /// 此前是「固定 250 的输入框 + 3 个复选 + 3 个图标钮 + 计数」硬排一行：
        /// 360px 宿主里 Checkbox 落在 x=352、三个按钮在 395/435/475 —— 全部越出父容器
        /// （ui-tree-check 越界规则实证）。搜索栏是 Dock=Top 的常驻条，
        /// 窄栏/大字号下必须自己长高而不是把按钮推到屏幕外。
        /// </summary>
        private void Relayout()
        {
            RefitControls();
            if (_flow == null) return;
            int avail = Math.Max(0, ClientSize.Width - Padding.Horizontal);
            if (avail <= 0) return;

            // 输入框吃掉剩余宽度但保底 120（窄到 120 以下就交给换行）
            int inputWant = Math.Min(DpiScale.V(this, 250), avail);
            inputWant = Math.Max(DpiScale.V(this, 120), inputWant);
            if (_txtSearch != null && _txtSearch.Width != inputWant) _txtSearch.Width = inputWant;

            _flow.Width = avail;
            int prefH = _flow.GetPreferredSize(new Size(avail, 0)).Height;
            if (_flow.Height != prefH) _flow.Height = prefH;

            int want = prefH + Padding.Vertical;
            if (Height != want) Height = want;
        }

        /// <summary>
        /// 按当前字号重算复选/按钮/输入框的盒（字号一变就作废）。
        ///
        /// 控件是构造期一次性显式定尺寸的，字号后来变大（全局 UI 字号 9→14pt）时
        /// 位置由流式布局重排、但盒还是旧字号的：字号轴 ratio 1.00 实证。
        /// </summary>
        private void RefitControls()
        {
            if (_chkCase == null || _flow == null) return;
            var f = Font ?? FormFontPolicy.UiFont();
            var small = new Font(f.FontFamily, Math.Max(6f, f.Size - 0.5f));
            foreach (var c in new Control[] { _chkCase, _chkRegex, _chkWholeWord })
            {
                if (c == null) continue;
                c.Font = small;
                SizeChk((AntdUI.Checkbox)c, small);
            }
            foreach (var b in new Control[] { _btnPrev, _btnNext, _btnClose })
            {
                if (b == null) continue;
                SizeBtn((AntdUI.Button)b, f);
            }
            if (_txtSearch != null) _txtSearch.Height = FormFontPolicy.FieldHeight(this);
        }

        private void SizeChk(AntdUI.Checkbox chk, Font font)
        {
            if (chk == null || font == null) return;
            // 高度取「设计下限」与「行盒 × 2」的较大者：只留 DpiScale 固定下限时，
            // 150% + 9pt/14pt 两格都是同一个高度（矩阵门字号轴 1.14 实证）；
            // 系数取 2 而不是 1.35，是因为 150% 的下限 36 已经高于 13.5pt 的行盒×1.35
            int h = Math.Max(DpiScale.V(this, 24),
                (int)Math.Round(FormFontPolicy.LineBox(font, this, 1f) * 2.0));
            int w = Math.Max(DpiScale.V(this, 34),
                FormFontPolicy.MeasureTextWidth(chk.Text, font, this) + DpiScale.V(this, 32));
            chk.AutoSize = false;
            chk.Size = new Size(w, h);
            chk.Margin = new Padding(0, DpiScale.V(this, 2), DpiScale.V(this, 6), 0);
        }

        private void SizeBtn(AntdUI.Button btn, Font font)
        {
            if (btn == null) return;
            var f = font ?? Font;
            int h = Math.Max(DpiScale.V(this, 24),
                (int)Math.Round(FormFontPolicy.LineBox(f, this, 1f) * 2.0));
            int w = Math.Max(DpiScale.V(this, 34),
                FormFontPolicy.MeasureTextWidth(btn.Text, f, this) + DpiScale.V(this, 18));
            btn.AutoSize = false;
            btn.Size = new Size(w, h);
            btn.Margin = new Padding(0, DpiScale.V(this, 2), DpiScale.V(this, 4), 0);
        }

        private void BuildUI()
        {
            // 规范规则②③：改用 FlowLayoutPanel 流式布局，控件继承面板字体；搜索框/匹配符为等宽语义例外
            // WrapContents=true：窄宿主/大字号时换行而不是溢出（见 Relayout 注释）
            _flow = new FlowLayoutPanel
            {
                Location = new Point(Padding.Left, Padding.Top),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = false,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            var flow = _flow;

            _txtSearch = new AntdUI.Input {
                Width = DpiScale.V(this, 250),
                // AutoSize 的 AntdUI.Input 高度是 0（dump 实证「零尺寸 AntdUI.Input」）：
                // 不给高就只画一条边，用户看不到自己输的字
                Height = FormFontPolicy.FieldHeight(this),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                Margin = new Padding(0, 2, DpiScale.V(this, 6), 0)
            };
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DoSearch(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.Escape) { Hide(); CloseRequested?.Invoke(); }
                if (e.KeyCode == Keys.F3) { NavigateRequested?.Invoke(!e.Shift); }
            };

            _chkCase = CreateChk("Aa", L10n.T("区分大小写"));
            _chkRegex = CreateChk(".*", L10n.T("正则表达式"));
            _chkWholeWord = CreateChk("W", L10n.T("全字匹配"));

            _btnPrev = CreateBtn("▲", L10n.T("上一个 (Shift+F3)"));
            _btnNext = CreateBtn("▼", L10n.T("下一个 (F3)"));
            _btnClose = CreateBtn("✕", L10n.T("关闭 (Esc)"));
            _btnClose.ForeColor = GdtermColorTable.Muted;

            _lblCount = new AntdUI.Label {
                Text = "",
                Margin = new Padding(DpiScale.V(this, 8), 6, 3, 0),
                ForeColor = GdtermColorTable.Muted
            };
            FormFontPolicy.FitSingleLineLabel(_lblCount);

            _btnPrev.Click += (s, e) => NavigateRequested?.Invoke(false);
            _btnNext.Click += (s, e) => NavigateRequested?.Invoke(true);
            _btnClose.Click += (s, e) => { Hide(); CloseRequested?.Invoke(); };

            flow.Controls.AddRange(new Control[] { _txtSearch, _chkCase, _chkRegex, _chkWholeWord, _btnPrev, _btnNext, _btnClose, _lblCount });
            Controls.Add(flow);
        }

        private AntdUI.Checkbox CreateChk(string text, string tip)
        {
            // 显式尺寸（不用 AutoSize + MinimumSize）：MinimumSize 是在 AutoSize 量完之后
            // 才把控件撑大，而 FlowLayoutPanel 已经按撑大前的宽度排好了位置 → 相邻控件重叠
            // （矩阵门 d150 实证 3 处重叠，交叠 180–396px²）。显式尺寸一次给对，流不会错位。
            var font = new Font(Font.FontFamily, Math.Max(6f, Font.Size - 0.5f));
            var chk = new AntdUI.Checkbox {
                Text = text,
                AutoSize = false,
                // 等宽语义例外：Aa/.*/W 为正则记号，字号相对当前字体缩小一号（规范规则③）,
                Font = font,
                ForeColor = GdtermColorTable.Muted,
                Margin = new Padding(0, 4, DpiScale.V(this, 6), 0)
            };
            SizeChk(chk, font);
            var t = new ToolTip();
            t.SetToolTip(chk, tip);
            return chk;
        }

        private AntdUI.Button CreateBtn(string text, string tip)
        {
            // 图标钮显式尺寸：正文只有「▲」一个字形，AutoSize 后宽 26 逻辑 px，
            // 过不了「触击目标 ≥32 逻辑 px」的门禁（d150 实证 39x36 设备 px）
            var btn = new AntdUI.Button {
                Text = text,
                AutoSize = false,
                Padding = new Padding(DpiScale.V(this, 1), 0, DpiScale.V(this, 1), 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            SizeBtn(btn, Font);
            var t = new ToolTip();
            t.SetToolTip(btn, tip);
            return btn;
        }

        private void DoSearch()
        {
            if (string.IsNullOrEmpty(_txtSearch.Text)) return;
            SearchRequested?.Invoke(_txtSearch.Text, _chkCase.Checked, _chkRegex.Checked, _chkWholeWord.Checked);
        }

        /// <summary>显示搜索栏并聚焦输入框</summary>
        public void ShowAndFocus()
        {
            Visible = true;
            _txtSearch.Focus();
            _txtSearch.SelectAll();
        }

        /// <summary>更新匹配计数</summary>
        public void UpdateMatchCount(int current, int total)
        {
            _lblCount.Text = total > 0 ? string.Format("{0}/{1}", current + 1, total) : L10n.T("无匹配");
            _lblCount.ForeColor = total > 0 ? GdtermColorTable.Success : GdtermColorTable.Danger;
        }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
