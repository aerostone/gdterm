using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Security.Otp;
using Gdterm.UI.Controls;
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
        private ListShell _shell;
        private EmptyStateView _empty;
        private readonly Timer _timer;

        public OtpManagerForm(IOtpStore store)
        {
            _store = store;
            Text = "OTP 动态码管理";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            // Font 先于 ClientSize（字体变更触发 AutoScaleMode=Font 缩放，顺序反了
            // 构造期绝对坐标会拿到缩放后的宽，CI 0.1.359 实测 640→1080 页脚按钮飞出面板）
            Font = FormFontPolicy.UiFont();
            ClientSize = DpiScale.S(this, 640, 420);
            MinimumSize = DpiScale.S(this, 480, 320);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                CheckBoxes = true,       // 批量删除
                Font = FormFontPolicy.MonoFont()
            };
            _list.Columns.Add("名称", 240);
            _list.Columns.Add("动态码", 140);
            _list.Columns.Add("剩余", 80);
            _list.DoubleClick += (s, e) => CopySelectedCode();

            // 底部按钮栏——左右两个 FlowLayoutPanel（SetupWizardForm/PasswordHealthForm 流式惯例）。
            // 禁用绝对坐标 Location=ClientSize.Width-N：构造期字体缩放后 ClientSize 已变，
            // 绝对坐标推入面板外（CI 0.1.359 实测）；流式布局 resize 自动重排。
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FormFontPolicy.FieldHeight(this) + 24
            };
            var leftFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(DpiScale.V(this, 8))
            };
            var rightFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(DpiScale.V(this, 8))
            };

            var btnClose = new AntdUI.Button
            {
                Text = "关闭",
                AutoSize = true,
                Cursor = Cursors.Hand,
                Name = "btnClose",
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5)),
                Margin = new Padding(DpiScale.V(this, 4))
            };
            btnClose.Click += (s, e) => Close();
            CancelButton = btnClose;

            var btnCopy = new AntdUI.Button
            {
                Text = "复制代码",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Primary,
                Cursor = Cursors.Hand,
                Name = "btnCopy",
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5)),
                Margin = new Padding(DpiScale.V(this, 4))
            };
            btnCopy.Click += (s, e) => CopySelectedCode();

            // RightToLeft 流：先加者在右 → 视觉上 [添加][删除]，删除居右（与旧设计一致）
            var btnAdd = new AntdUI.Button
            {
                Text = "添加",
                AutoSize = true,
                Cursor = Cursors.Hand,
                Name = "btnAdd",
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5)),
                Margin = new Padding(DpiScale.V(this, 4))
            };
            btnAdd.Click += (s, e) => AddEntry();

            var btnDelete = new AntdUI.Button
            {
                Text = "删除",
                AutoSize = true,
                Cursor = Cursors.Hand,
                Name = "btnDelete",
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5)),
                Margin = new Padding(DpiScale.V(this, 4))
            };
            btnDelete.Click += (s, e) => DeleteSelected();

            leftFlow.Controls.Add(btnClose);
            leftFlow.Controls.Add(btnCopy);
            rightFlow.Controls.Add(btnAdd);
            rightFlow.Controls.Add(btnDelete);
            bottom.Controls.Add(leftFlow);
            bottom.Controls.Add(rightFlow);

            // Fill 控件先加、边缘 Dock 后加（WinForms 反向 Z 序布局）；
            // 空态与列表互斥可见（都 Dock Fill，不可见者不占布局）。
            _shell = new ListShell(_list, "过滤名称…");
            _shell.AddAction("导出", (s2, e2) => _shell.ExportCsv(this, "otp-codes")).Name = "OtpExportButton";

            Controls.Add(_shell);
            Controls.Add(bottom);
            _list.AccessibleName = "OTP 条目列表";
            _list.AccessibleDescription = "名称/动态码/剩余秒数";
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件，
            // 列表名单独设（读屏软件念"宏列表"而不是"列表"）
            try { UiA11y.AutoName(this); } catch (Exception) { }


            // 空态引导（UX 审计 C1）：改用全仓统一的 EmptyStateView，和另外 15 个列表面板长得一致，
            // 并带上「添加」主操作（此前只有一段居中文字，用户还得自己去底下找按钮）
            _empty = EmptyStateView.Attach(this, "暂无 OTP 条目",
                "点「添加」粘贴 otpauth:// URI 或 Base32 密钥；TOTP 用于多数 Web 门户/云平台的二次验证",
                "添加条目", (s2, e2) => AddEntry());
            _list.Visible = false;
            _empty.SetEmpty(true);
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

            var entries = _store.LoadAll();
            using (BusyScope.Begin(this, "正在读取 OTP 条目…", false))
            {
            var rows = new List<ListViewItem>(entries.Count);
            foreach (var en in entries)
            {
                var item = new ListViewItem(new[]
                {
                    en.Title,
                    CodeText(en),
                    TimeText(en)
                });
                item.Tag = en.Id;
                rows.Add(item);
            }
            _shell.SetItems(rows);
            }

            _shell.Visible = entries.Count > 0;
            if (_empty != null) _empty.SetEmpty(entries.Count == 0);

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
            var ids = new List<string>();
            foreach (var it in _shell.SelectedOrChecked())
            {
                var id = it.Tag as string;
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            if (ids.Count == 0) return;

            var all = _store.LoadAll();
            string first = ids.Count == 1
                ? (all.FirstOrDefault(e => Equals(e.Id, ids[0]))?.Title ?? ids[0])
                : null;
            var res = MessageBox.Show(this, ids.Count == 1
                    ? "删除 OTP 条目「" + first + "」？\n删除后无法恢复。"
                    : "删除选中的 " + ids.Count + " 个 OTP 条目？\n删除后无法恢复。",
                "删除确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (res != DialogResult.OK) return;
            int ok = 0;
            try
            {
                foreach (var id in ids)
                {
                    try { _store.Delete(id); ok++; }
                    catch (Exception ex) { try { DiagLog.Swallowed("OtpManager.Delete", ex); } catch { } }
                }
                RefreshList();
                if (ok == ids.Count) ToastNotifier.Success("已删除 " + ok + " 个条目");
                else ToastNotifier.Warning("删除完成：" + ok + " 成功，" + (ids.Count - ok) + " 失败");
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("OtpManager.Delete", ex); } catch { }
            }
        }
    }
}
