using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 终端搜索栏——Ctrl+Shift+F 弹出，正则/大小写/全字匹配，上下跳转
    /// </summary>
    public class TerminalSearchBar : Panel
    {
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
        }

        private void BuildUI()
        {
            // 规范规则②③：改用 FlowLayoutPanel 流式布局，控件继承面板字体；搜索框/匹配符为等宽语义例外
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0)
            };

            _txtSearch = new AntdUI.Input {
                Width = DpiScale.V(this, 250),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 9f),
                Margin = new Padding(0, 2, DpiScale.V(this, 6), 0)
            };
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DoSearch(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.Escape) { Hide(); CloseRequested?.Invoke(); }
                if (e.KeyCode == Keys.F3) { NavigateRequested?.Invoke(!e.Shift); }
            };

            _chkCase = CreateChk("Aa", "区分大小写");
            _chkRegex = CreateChk(".*", "正则表达式");
            _chkWholeWord = CreateChk("W", "全字匹配");

            _btnPrev = CreateBtn("▲", "上一个 (Shift+F3)");
            _btnNext = CreateBtn("▼", "下一个 (F3)");
            _btnClose = CreateBtn("✕", "关闭 (Esc)");
            _btnClose.ForeColor = GdtermColorTable.Muted;

            _lblCount = new AntdUI.Label {
                Text = "",
                AutoSize = true,
                Margin = new Padding(DpiScale.V(this, 8), 6, 3, 0),
                ForeColor = GdtermColorTable.Muted
            };

            _btnPrev.Click += (s, e) => NavigateRequested?.Invoke(false);
            _btnNext.Click += (s, e) => NavigateRequested?.Invoke(true);
            _btnClose.Click += (s, e) => { Hide(); CloseRequested?.Invoke(); };

            flow.Controls.AddRange(new Control[] { _txtSearch, _chkCase, _chkRegex, _chkWholeWord, _btnPrev, _btnNext, _btnClose, _lblCount });
            Controls.Add(flow);
        }

        private CheckBox CreateChk(string text, string tip)
        {
            var chk = new AntdUI.Checkbox {
                Text = text,
                AutoSize = true,
                // 等宽语义例外：Aa/.*/W 为正则记号，字号相对当前字体缩小一号（规范规则③）
                Font = new Font(Font.FontFamily, Font.Size - 0.5f),
                ForeColor = GdtermColorTable.Muted,
                Margin = new Padding(0, 4, DpiScale.V(this, 6), 0)
            };
            var t = new ToolTip();
            t.SetToolTip(chk, tip);
            return chk;
        }

        private Button CreateBtn(string text, string tip)
        {
            var btn = new AntdUI.Button {
                Text = text,
                AutoSize = true,
                Padding = new Padding(1, 0, 1, 0),
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
            _lblCount.Text = total > 0 ? string.Format("{0}/{1}", current + 1, total) : "无匹配";
            _lblCount.ForeColor = total > 0 ? GdtermColorTable.Success : GdtermColorTable.Danger;
        }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
