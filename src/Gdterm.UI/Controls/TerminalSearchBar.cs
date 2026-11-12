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
            var chk = new AntdUI.Checkbox {
                Text = text,
                AutoSize = true,
                // 等宽语义例外：Aa/.*/W 为正则记号，字号相对当前字体缩小一号（规范规则③）,
                Font = new Font(Font.FontFamily, Font.Size - 0.5f),
                ForeColor = GdtermColorTable.Muted,
                // 触击目标 ≥32 逻辑 px（AutoSize 实测只给 31：ui-tree-check 触击规则实证）
                MinimumSize = new Size(DpiScale.V(this, 34), DpiScale.V(this, 24)),
                Margin = new Padding(0, 4, DpiScale.V(this, 6), 0)
            };
            var t = new ToolTip();
            t.SetToolTip(chk, tip);
            return chk;
        }

        private AntdUI.Button CreateBtn(string text, string tip)
        {
            var btn = new AntdUI.Button {
                Text = text,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 1), 0, DpiScale.V(this, 1), 0),
                MinimumSize = new Size(DpiScale.V(this, 26), DpiScale.V(this, 24)),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
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
