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
using Gdterm.Core;

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
            Text = L10n.T("OTP 动态码管理");
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
            _list.Columns.Add(L10n.T("名称"), 240);
            _list.Columns.Add(L10n.T("动态码"), 140);
            _list.Columns.Add(L10n.T("剩余"), 80);
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

            var btnClose = new AntdUI.Button {
                Text = L10n.T("关闭"),
                Cursor = Cursors.Hand,
                Name = "btnClose",
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(DpiScale.V(this, 4))
            };
            FormFontPolicy.SizeActionButton(this, btnClose, 12, 5);
            btnClose.Click += (s, e) => Close();
            CancelButton = btnClose;

            var btnCopy = new AntdUI.Button {
                Text = L10n.T("复制代码"),
                Type = AntdUI.TTypeMini.Primary,
                Cursor = Cursors.Hand,
                Name = "btnCopy",
                Margin = new Padding(DpiScale.V(this, 4))
            };
            FormFontPolicy.SizeActionButton(this, btnCopy, 12, 5);
            btnCopy.Click += (s, e) => CopySelectedCode();

            // RightToLeft 流：先加者在右 → 视觉上 [添加][删除]，删除居右（与旧设计一致）
            var btnAdd = new AntdUI.Button {
                Text = L10n.T("添加"),
                Cursor = Cursors.Hand,
                Name = "btnAdd",
                Margin = new Padding(DpiScale.V(this, 4))
            };
            FormFontPolicy.SizeActionButton(this, btnAdd, 12, 5);
            btnAdd.Click += (s, e) => AddEntry();

            var btnDelete = new AntdUI.Button {
                Text = L10n.T("删除"),
                Cursor = Cursors.Hand,
                Name = "btnDelete",
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(DpiScale.V(this, 4))
            };
            FormFontPolicy.SizeActionButton(this, btnDelete, 12, 5);
            btnDelete.Click += (s, e) => DeleteSelected();

            leftFlow.Controls.Add(btnClose);
            leftFlow.Controls.Add(btnCopy);
            rightFlow.Controls.Add(btnAdd);
            rightFlow.Controls.Add(btnDelete);
            bottom.Controls.Add(leftFlow);
            bottom.Controls.Add(rightFlow);

            // Fill 控件先加、边缘 Dock 后加（WinForms 反向 Z 序布局）；
            // 空态与列表互斥可见（都 Dock Fill，不可见者不占布局）。
            _shell = new ListShell(_list, L10n.T("过滤名称…"));
            _shell.AddAction(L10n.T("导出"), (s2, e2) => _shell.ExportCsv(this, "otp-codes")).Name = "OtpExportButton";

            Controls.Add(_shell);
            Controls.Add(bottom);
            _list.AccessibleName = L10n.T("OTP 条目列表");
            _list.AccessibleDescription = L10n.T("名称/动态码/剩余秒数");
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件，
            // 列表名单独设（读屏软件念"宏列表"而不是"列表"）
            try { UiA11y.AutoName(this); } catch (Exception) { }

            // F16 回收站：删除的 OTP 条目可经「工具 → 回收站」恢复（恢复写 store，表单关闭后仍有效）
            TrashStore.Default.RegisterRestorer("otp", entry =>
            {
                var en = entry.Payload as OtpEntry;
                if (en != null) _store.Add(en);
            });


            // 空态引导（UX 审计 C1）：改用全仓统一的 EmptyStateView，和另外 15 个列表面板长得一致，
            // 并带上「添加」主操作（此前只有一段居中文字，用户还得自己去底下找按钮）
            _empty = EmptyStateView.Attach(this, L10n.T("暂无 OTP 条目"),
                L10n.T("点「添加」粘贴 otpauth:// URI 或 Base32 密钥；TOTP 用于多数 Web 门户/云平台的二次验证"),
                L10n.T("添加条目"), (s2, e2) => AddEntry());
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
            using (BusyScope.Begin(this, L10n.T("正在读取 OTP 条目…"), false))
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
                    ? L10n.T("已锁定")
                    : L10n.T("密钥无效");
            }
            return code;
        }

        private static string TimeText(OtpEntry en)
        {
            if (!en.IsTotp) return L10n.T("手动");
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
                MessageBox.Show(this, L10n.T("该条目的动态码当前不可用（应用未解锁或密钥无效）。"),
                    "OTP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Clipboard.SetText(code);
                ToastNotifier.Success(L10n.T("已复制 ") + entry.Title + L10n.T(" 的动态码"));
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
                    ? L10n.T("删除 OTP 条目「") + first + L10n.T("」？\n删除后可在「工具 → 回收站」恢复（应用重启后清空）。")
                    : L10n.T("删除选中的 ") + ids.Count + L10n.T(" 个 OTP 条目？\n删除后可在「工具 → 回收站」恢复（应用重启后清空）。"),
                L10n.T("删除确认"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (res != DialogResult.OK) return;
            int ok = 0;
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        _store.Delete(id); ok++;
                        var del = all.FirstOrDefault(e => Equals(e.Id, id));
                        if (del != null) TrashStore.Default.Add("otp", L10n.T("OTP 条目"), del.Title, del);
                    }
                    catch (Exception ex) { try { DiagLog.Swallowed("OtpManager.Delete", ex); } catch { } }
                }
                RefreshList();
                if (ok == ids.Count) ToastNotifier.Success(L10n.T("已删除 ") + ok + L10n.T(" 个条目"));
                else ToastNotifier.Warning(L10n.T("删除完成：") + ok + L10n.T(" 成功，") + (ids.Count - ok) + L10n.T(" 失败"));
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("OtpManager.Delete", ex); } catch { }
            }
        }
    }
}
