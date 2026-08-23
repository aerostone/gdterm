using System;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 终端搜索栏——Ctrl+Shift+F 弹出，正则/大小写/全字匹配，上下跳转
    /// </summary>
    public class TerminalSearchBar : Panel
    {
        private TextBox _txtSearch;
        private CheckBox _chkRegex, _chkCase, _chkWholeWord;
        private Button _btnPrev, _btnNext, _btnClose;
        private Label _lblCount;

        public event Action<string, bool, bool, bool> SearchRequested; // pattern, caseSensitive, regex, wholeWord
        public event Action<bool> NavigateRequested; // true=next, false=prev
        public event Action CloseRequested;

        public TerminalSearchBar()
        {
            Dock = DockStyle.Top;
            Height = 36;
            BackColor = Color.FromArgb(37, 37, 38);
            Visible = false;
            Padding = new Padding(8, 4, 8, 4);
            BuildUI();
        }

        private void BuildUI()
        {
            var font = new Font("Microsoft YaHei", 9f);

            _txtSearch = new TextBox
            {
                Location = new Point(8, 6),
                Size = new Size(250, 24),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DoSearch(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.Escape) { Hide(); CloseRequested?.Invoke(); }
                if (e.KeyCode == Keys.F3) { NavigateRequested?.Invoke(!e.Shift); }
            };

            _chkCase = CreateChk("Aa", 270, "区分大小写");
            _chkRegex = CreateChk(".*", 305, "正则表达式");
            _chkWholeWord = CreateChk("W", 340, "全字匹配");

            _btnPrev = CreateBtn("▲", 380, "上一个 (Shift+F3)");
            _btnNext = CreateBtn("▼", 408, "下一个 (F3)");
            _btnClose = CreateBtn("✕", 440, "关闭 (Esc)");
            _btnClose.ForeColor = Color.FromArgb(180, 180, 180);

            _lblCount = new Label
            {
                Text = "",
                Location = new Point(468, 8),
                AutoSize = true,
                Font = font,
                ForeColor = Color.FromArgb(130, 130, 130)
            };

            _btnPrev.Click += (s, e) => NavigateRequested?.Invoke(false);
            _btnNext.Click += (s, e) => NavigateRequested?.Invoke(true);
            _btnClose.Click += (s, e) => { Hide(); CloseRequested?.Invoke(); };

            Controls.AddRange(new Control[] { _txtSearch, _chkCase, _chkRegex, _chkWholeWord, _btnPrev, _btnNext, _btnClose, _lblCount });
        }

        private CheckBox CreateChk(string text, int x, string tip)
        {
            var chk = new CheckBox
            {
                Text = text,
                Location = new Point(x, 7),
                AutoSize = true,
                Font = new Font("Consolas", 8.5f),
                ForeColor = Color.FromArgb(150, 150, 150)
            };
            var t = new ToolTip();
            t.SetToolTip(chk, tip);
            return chk;
        }

        private Button CreateBtn(string text, int x, string tip)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(24, 24),
                Location = new Point(x, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 9f)
            };
            btn.FlatAppearance.BorderSize = 0;
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
            _lblCount.ForeColor = total > 0 ? Color.FromArgb(78, 201, 176) : Color.FromArgb(255, 100, 100);
        }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
