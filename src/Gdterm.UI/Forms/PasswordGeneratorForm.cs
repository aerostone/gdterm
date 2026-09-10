using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 密码生成器——独立工具（AntdUI 版）。
    /// 支持：长度调节、字符集选择、一键复制、批量生成。
    /// </summary>
    public class PasswordGeneratorForm : AntdUI.Window
    {
        private AntdUI.InputNumber _lengthSpinner;
        private AntdUI.Checkbox _upperCheck;
        private AntdUI.Checkbox _lowerCheck;
        private AntdUI.Checkbox _digitCheck;
        private AntdUI.Checkbox _specialCheck;
        private AntdUI.Checkbox _ambiguousCheck;
        private AntdUI.Input _resultBox;
        private AntdUI.Input _historyBox; // 只读多行：历史列表（AntdUI 暂无 ListBox，用只读多行文本承载）
        private AntdUI.Label _strengthLabel;
        private AntdUI.Button _copyBtn;

        private static readonly string UppercaseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private static readonly string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
        private static readonly string DigitChars = "0123456789";
        private static readonly string SpecialChars = "!@#$%^&*()-_=+[]{}|;:',.<>?/";
        private static readonly string AmbiguousChars = "Il1O0";

        /// <summary>当前生成框中的密码（供 KeePass 编辑器调用）。</summary>
        public string GeneratedPassword
        {
            get { return _resultBox != null ? _resultBox.Text : null; }
        }

        public PasswordGeneratorForm()
        {
            InitializeComponent();
            GeneratePassword();
            Services.FormFontPolicy.Apply(this); // AntdUI 控件继承 Form.Font，恢复用户配置 UI 字号传导
        }

        private void InitializeComponent()
        {
            Text = "密码生成器";
            Size = DpiScale.S(this, 520, 600); // 初始基准，构造末尾按内容自适应重设
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
            ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定 520x600 下历史框底部被裁剪）
            int clientW = DpiScale.V(this, 520);
            int pad = DpiScale.V(this, 20);
            int fieldH = Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this));
            int rowH = fieldH + DpiScale.V(this, 8);
            int y = DpiScale.V(this, 22);

            // 标题
            var titleLabel = new AntdUI.Label {
                Text = "🔑 密码生成器",
                Font = new Font("Segoe UI Emoji", 15F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(titleLabel);
            y += Math.Max(DpiScale.V(this, 52), FormFontPolicy.RowStep(this) + DpiScale.V(this, 14));

            // 密码长度
            var lengthLabel = new AntdUI.Label { Text = "密码长度", AutoSize = true, Location = new Point(pad, y + DpiScale.V(this, 10)) };
            Controls.Add(lengthLabel);

            _lengthSpinner = new AntdUI.InputNumber {
                Location = new Point(pad + DpiScale.V(this, 90), y),
                Size = new Size(DpiScale.V(this, 90), fieldH),
                Minimum = 8,
                Maximum = 128,
                Value = 16,
                Increment = 1
            };
            _lengthSpinner.ValueChanged += (s, e) => GeneratePassword();
            Controls.Add(_lengthSpinner);

            // 快捷长度按钮
            int[] quickLengths = { 12, 16, 20, 24, 32 };
            int btnX = pad + DpiScale.V(this, 200);
            foreach (var len in quickLengths)
            {
                var lenCaptured = len;
                var btn = new AntdUI.Button {
                    Text = len.ToString(),
                    Location = new Point(btnX, y),
                    Size = new Size(DpiScale.V(this, 48), fieldH)
                };
                btn.Click += (s, e) => { _lengthSpinner.Value = lenCaptured; };
                Controls.Add(btn);
                btnX += DpiScale.V(this, 56);
            }
            y += rowH + DpiScale.V(this, 4);

            // 字符集选项
            var charsetTitle = new AntdUI.Label { Text = "字符集", AutoSize = true, Location = new Point(pad, y) };
            Controls.Add(charsetTitle);
            y += Math.Max(DpiScale.V(this, 32), FormFontPolicy.RowStep(this));

            int col2X = pad + DpiScale.V(this, 220);
            _upperCheck = CreateCheck("大写字母 (A-Z)", pad, y);
            _digitCheck = CreateCheck("数字 (0-9)", col2X, y);
            y += Math.Max(DpiScale.V(this, 32), FormFontPolicy.RowStep(this));
            _lowerCheck = CreateCheck("小写字母 (a-z)", pad, y);
            _specialCheck = CreateCheck("特殊字符 (!@#$...)", col2X, y);
            y += Math.Max(DpiScale.V(this, 32), FormFontPolicy.RowStep(this));
            _ambiguousCheck = CreateCheck("排除易混淆字符 (Il1O0)", pad, y);
            _ambiguousCheck.CheckedChanged += (s, e) => GeneratePassword();
            y += Math.Max(DpiScale.V(this, 44), FormFontPolicy.RowStep(this) + DpiScale.V(this, 12));

            // 生成结果
            var resultLabel = new AntdUI.Label { Text = "生成结果", AutoSize = true, Location = new Point(pad, y) };
            Controls.Add(resultLabel);
            y += DpiScale.V(this, 28);

            int resultH = fieldH + DpiScale.V(this, 6);
            _resultBox = new AntdUI.Input {
                Location = new Point(pad, y),
                Size = new Size(DpiScale.V(this, 300), resultH),
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 14f, FontStyle.Bold),
                ReadOnly = true
            };
            Controls.Add(_resultBox);

            _strengthLabel = new AntdUI.Label {
                Text = "强度：—",
                AutoSize = true,
                Location = new Point(pad, y + resultH + DpiScale.V(this, 8))
            };
            Controls.Add(_strengthLabel);

            var generateBtn = new AntdUI.Button {
                Text = "🔄 重新生成",
                Location = new Point(pad + DpiScale.V(this, 312), y),
                Size = new Size(DpiScale.V(this, 150), resultH),
                Type = AntdUI.TTypeMini.Primary
            };
            generateBtn.Click += (s, e) => GeneratePassword();
            Controls.Add(generateBtn);

            _copyBtn = new AntdUI.Button {
                Text = "📋 复制",
                Location = new Point(pad + DpiScale.V(this, 312), y + resultH + DpiScale.V(this, 8)),
                Size = new Size(DpiScale.V(this, 150), fieldH)
            };
            _copyBtn.Click += OnCopyClick;
            Controls.Add(_copyBtn);
            y += resultH + fieldH + DpiScale.V(this, 16);

            var generate10Btn = new AntdUI.Button {
                Text = "批量生成 10 个",
                Location = new Point(pad, y),
                Size = new Size(DpiScale.V(this, 130), fieldH)
            };
            generate10Btn.Click += OnBatchGenerate;
            Controls.Add(generate10Btn);
            y += rowH + DpiScale.V(this, 4);

            // 历史记录（只读多行 Input，双击复制整行由 KeyDown/MouseUp 简化为一键复制全部）
            var historyLabel = new AntdUI.Label {
                Text = "本次生成记录（双击复制）",
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(historyLabel);
            y += DpiScale.V(this, 28);

            int historyH = DpiScale.V(this, 110);
            _historyBox = new AntdUI.Input {
                Location = new Point(pad, y),
                Size = new Size(clientW - pad * 2, historyH),
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 10f),
                ReadOnly = true,
                Multiline = true
            };
            _historyBox.MouseDoubleClick += OnHistoryDoubleClick;
            Controls.Add(_historyBox);
            y += historyH;

            // 客户区高度随内容自适应（修复：固定高度下历史框底部被裁剪）
            ClientSize = new Size(clientW, y + DpiScale.V(this, 20));
        }

        private AntdUI.Checkbox CreateCheck(string text, int x, int y)
        {
            var cb = new AntdUI.Checkbox {
                Text = text,
                AutoSize = true,
                Location = new Point(x, y),
                Checked = true
            };
            cb.CheckedChanged += (s, e) => GeneratePassword();
            return cb;
        }

        private void GeneratePassword()
        {
            var charset = BuildCharset();
            if (charset.Length == 0)
            {
                _resultBox.Text = "";
                _strengthLabel.Text = "强度：请至少选择一种字符集";
                return;
            }

            int length = (int)_lengthSpinner.Value;
            _resultBox.Text = GenerateRandomString(charset, length);
            UpdateStrength(length, charset.Length);
        }

        private string BuildCharset()
        {
            var sb = new StringBuilder();
            if (_upperCheck.Checked) sb.Append(UppercaseChars);
            if (_lowerCheck.Checked) sb.Append(LowercaseChars);
            if (_digitCheck.Checked) sb.Append(DigitChars);
            if (_specialCheck.Checked) sb.Append(SpecialChars);

            var charset = sb.ToString();

            // 排除易混淆字符
            if (_ambiguousCheck.Checked)
            {
                foreach (var c in AmbiguousChars)
                    charset = charset.Replace(c.ToString(), "");
            }

            return charset;
        }

        private static string GenerateRandomString(string charset, int length)
        {
            var result = new char[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                var buffer = new byte[4];
                for (int i = 0; i < length; i++)
                {
                    rng.GetBytes(buffer);
                    var index = (int)(BitConverter.ToUInt32(buffer, 0) % (uint)charset.Length);
                    result[i] = charset[index];
                }
            }
            return new string(result);
        }

        private void UpdateStrength(int length, int charsetSize)
        {
            // 简单强度评估：熵位数
            double entropy = length * (Math.Log(charsetSize) / Math.Log(2.0));

            string strength;
            Color color;
            if (entropy < 40) { strength = "弱"; color = GdtermColorTable.Danger; }
            else if (entropy < 60) { strength = "中"; color = GdtermColorTable.Warning; }
            else if (entropy < 80) { strength = "强"; color = GdtermColorTable.Success; }
            else { strength = "极强"; color = GdtermColorTable.Success; }

            _strengthLabel.Text = $"强度：{strength}（{entropy:F0} bit 熵）";
            _strengthLabel.ForeColor = color;
        }

        private void OnCopyClick(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_resultBox.Text))
            {
                try
                {
                    ClipboardProtector.SetTextWithTtl(_resultBox.Text);
                    AddToHistory(_resultBox.Text);

                    // 短暂提示
                    var btn = (AntdUI.Button)sender;
                    var originalText = btn.Text;
                    btn.Text = "✓ 已复制(30s清空)";
                    var timer = new Timer { Interval = 1500 };
                    timer.Tick += (s, ev) => { btn.Text = originalText; timer.Stop(); timer.Dispose(); };
                    timer.Start();
                }
                catch { }
            }
        }

        private void OnBatchGenerate(object sender, EventArgs e)
        {
            var charset = BuildCharset();
            if (charset.Length == 0) return;

            int length = (int)_lengthSpinner.Value;
            var lines = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 10; i++)
            {
                lines.Add(GenerateRandomString(charset, length));
            }
            _historyBox.Text = string.Join(Environment.NewLine, lines);

            // 用第一个作为当前结果
            if (lines.Count > 0)
                _resultBox.Text = lines[0];
        }

        private void OnHistoryDoubleClick(object sender, EventArgs e)
        {
            // 复制光标所在行
            try
            {
                var text = _historyBox.Text ?? "";
                if (text.Length == 0) return;
                int pos = Math.Min(_historyBox.SelectionStart, text.Length);
                int start = text.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
                int end = text.IndexOf('\n', pos);
                if (end < 0) end = text.Length;
                var line = text.Substring(start, end - start).Trim();
                if (line.Length > 0)
                    ClipboardProtector.SetTextWithTtl(line);
            }
            catch { }
        }

        private void AddToHistory(string password)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(_historyBox.Text))
                lines.AddRange(_historyBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries));
            lines.Insert(0, password);
            while (lines.Count > 50) lines.RemoveAt(lines.Count - 1);
            _historyBox.Text = string.Join(Environment.NewLine, lines);
        }
    }
}
