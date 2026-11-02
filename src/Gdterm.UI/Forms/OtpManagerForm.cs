using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Security.Otp;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// OTP 动态码管理器（nyaterm 对比吸收 B1）：TOTP 条目列表 + 实时倒计时码 + 复制/添加/删除。
    /// 密钥落盘为主密码 AES（gdo1:）；应用未解锁时条目显示锁定态（码不可用）。
    /// </summary>
    public sealed class OtpManagerForm : AntdUI.Window
    {
        private readonly IOtpStore _store;
        private readonly ListView _list;
        private readonly Label _emptyLabel;
        private readonly Timer _timer;

        public OtpManagerForm(IOtpStore store)
        {
            _store = store;
            Text = "OTP 动态码管理";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = DpiScale.S(this, 640, 420);
            MinimumSize = DpiScale.S(this, 480, 320);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont();
            ShowInTaskbar = false;

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.MonoFont()
            };
            _list.Columns.Add("名称", 240);
            _list.Columns.Add("动态码", 140);
            _list.Columns.Add("剩余", 80);
            _list.DoubleClick += (s, e) => CopySelectedCode();

            // 空态引导（UX 审计 C1 惯例，R09）
            _emptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = GdtermColorTable.Foreground,
                BackColor = GdtermColorTable.Background,
                Font = FormFontPolicy.UiFont(+1f),
                Text = "暂无 OTP 条目\n\n点「添加」粘贴 otpauth:// URI 或 Base32 密钥\n（TOTP：多数 Web 门户/云平台的二次验证）"
            };

            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FormFontPolicy.FieldHeight(this) + 24
            };
            var btnClose = new AntdUI.Button
            {
                Text = "关闭",
                Location = DpiScale.P(this, 8, 8),
                Size = DpiScale.S(this, 88, FormFontPolicy.FieldHeight(this))
            };
            btnClose.Click += (s, e) => Close();
            CancelButton = btnClose;

            var btnCopy = new AntdUI.Button
            {
                Text = "复制代码",
                Type = AntdUI.TTypeMini.Primary,
                Location = DpiScale.P(this, 104, 8),
                Size = DpiScale.S(this, 108, FormFontPolicy.FieldHeight(this))
            };
            btnCopy.Click += (s, e) => CopySelectedCode();

            var btnDelete = new AntdUI.Button
            {
                Text = "删除",
                Location = DpiScale.P(this, ClientSize.Width - 96, 8),
                Size = DpiScale.S(this, 88, FormFontPolicy.FieldHeight(this)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnDelete.Click += (s, e) => DeleteSelected();

            var btnAdd = new AntdUI.Button
            {
                Text = "添加",
                Location = DpiScale.P(this, ClientSize.Width - 192, 8),
                Size = DpiScale.S(this, 88, FormFontPolicy.FieldHeight(this)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnAdd.Click += (s, e) => AddEntry();

            bottom.Controls.Add(btnClose);
            bottom.Controls.Add(btnCopy);
            bottom.Controls.Add(btnAdd);
            bottom.Controls.Add(btnDelete);

            // Fill 控件先加、边缘 Dock 后加（WinForms 反向 Z 序布局）；
            // 空态与列表互斥可见（都 Dock Fill，不可见者不占布局）。
            Controls.Add(_list);
            Controls.Add(_emptyLabel);
            Controls.Add(bottom);
            _list.Visible = false;
            _emptyLabel.Visible = true;
            FormFontPolicy.Apply(this);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => RefreshCodes();
            Load += (s, e) =>
            {
                RefreshList();
                _timer.Start();
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _timer?.Stop(); } catch { }
            base.OnFormClosed(e);
        }

        /// <summary>重载条目（增删后 / 解锁后调用）。</summary>
        public void RefreshList()
        {
            string selectedId = null;
            if (_list.SelectedItems.Count > 0)
                selectedId = _list.SelectedItems[0].Tag as string;

            _list.BeginUpdate();
            _list.Items.Clear();
            var entries = _store.LoadAll();
            foreach (var en in entries)
            {
                var item = new ListViewItem(new[]
                {
                    en.Title,
                    CodeText(en),
                    TimeText(en)
                });
                item.Tag = en.Id;
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            _list.Visible = entries.Count > 0;
            _emptyLabel.Visible = entries.Count == 0;

            if (selectedId != null)
            {
                foreach (ListViewItem it in _list.Items)
                {
                    if (Equals(it.Tag as string, selectedId))
                    {
                        it.Selected = true;
                        break;
                    }
                }
            }
            else if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
            }
        }

        private void RefreshCodes()
        {
            foreach (var en in _store.LoadAll())
            {
                foreach (ListViewItem it in _list.Items)
                {
                    if (!Equals(it.Tag as string, en.Id)) continue;
                    it.SubItems[1].Text = CodeText(en);
                    it.SubItems[2].Text = TimeText(en);
                    break;
                }
            }
        }

        private static string CodeText(OtpEntry en)
        {
            var code = en.CodeAt();
            if (code == null)
            {
                // gdo1: 前缀 = 未解锁；其余 = 密钥损坏
                return en.Secret != null && en.Secret.StartsWith("gdo1:", StringComparison.Ordinal)
                    ? "已锁定"
                    : "密钥无效";
            }
            return code;
        }

        private static string TimeText(OtpEntry en)
        {
            if (!en.IsTotp) return "手动";
            try { return OtpGenerator.SecondsRemaining(en.Period) + "s"; }
            catch { return ""; }
        }

        private void CopySelectedCode()
        {
            if (_list.SelectedItems.Count == 0) return;
            var id = _list.SelectedItems[0].Tag as string;
            var entry = _store.LoadAll().FirstOrDefault(e => Equals(e.Id, id));
            if (entry == null) return;
            var code = entry.CodeAt();
            if (code == null)
            {
                MessageBox.Show(this, "该条目的动态码当前不可用（应用未解锁或密钥无效）。",
                    "OTP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Clipboard.SetText(code);
                ToastNotifier.Success("已复制 " + entry.Title + " 的动态码");
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("OtpManager.Copy", ex); } catch { }
            }
        }

        private void AddEntry()
        {
            try
            {
                using (var dlg = new AddOtpForm())
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    _store.Add(dlg.Entry);
                    RefreshList();
                }
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("OtpManager.Add", ex); } catch { }
            }
        }

        private void DeleteSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var id = _list.SelectedItems[0].Tag as string;
            var entry = _store.LoadAll().FirstOrDefault(e => Equals(e.Id, id));
            var title = entry != null ? entry.Title : id;
            var res = MessageBox.Show(this, "删除 OTP 条目「" + title + "」？\n删除后无法恢复。",
                "删除确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (res != DialogResult.OK) return;
            try
            {
                _store.Delete(id);
                RefreshList();
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("OtpManager.Delete", ex); } catch { }
            }
        }
    }
}
