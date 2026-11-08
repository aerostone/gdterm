using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 回收站（F16 加分项「撤销/回收站」的入口页）：各页面删除的条目先入 <see cref="TrashStore"/>，
    /// 本页可 恢复 / 删除选中 / 清空。
    ///
    /// 会话级范围（v1 裁决）：应用重启后清空——页面文案已如实标注，不做假承诺。
    /// 布局走既有合规模式：AntdUI.Window + Font 先于 ClientSize（CI 0.1.359 顺序教训）+
    /// DpiScale 尺寸 + 流式按钮行（禁绝对坐标）+ ListShell（过滤/排序/分页/导出免费）+
    /// EmptyStateView（回收站空 ≠ 出错，文案要说明这是正常态）。
    /// </summary>
    public sealed class TrashForm : AntdUI.Window
    {
        private readonly ListView _list;
        private ListShell _shell;
        private EmptyStateView _empty;
        private AntdUI.Button _btnRestoreSel;
        private AntdUI.Button _btnDeleteSel;
        private AntdUI.Button _btnClear;
        private bool _unsubscribed;

        public TrashForm()
        {
            Text = L10n.T("回收站");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            // Font 先于 ClientSize（字体缩放顺序教训，同 OtpManagerForm）
            Font = FormFontPolicy.UiFont();
            ClientSize = DpiScale.S(this, 620, 400);
            MinimumSize = DpiScale.S(this, 460, 300);
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
                CheckBoxes = true,   // 批量恢复/删除
                Font = FormFontPolicy.MonoFont()
            };
            _list.Columns.Add(L10n.T("分类"), 120);
            _list.Columns.Add(L10n.T("内容"), 300);
            _list.Columns.Add(L10n.T("删除时间"), 140);
            _list.DoubleClick += (s, e) => RestoreSelected();

            _shell = new ListShell(_list, L10n.T("过滤分类/内容…"));
            _btnRestoreSel = _shell.AddAction(L10n.T("恢复选中"), (s, e) => RestoreSelected(), "TrashRestoreButton");
            _btnDeleteSel = _shell.AddAction(L10n.T("删除选中"), (s, e) => DiscardSelected(), "TrashDeleteButton");
            _btnClear = _shell.AddAction(L10n.T("清空"), (s, e) => ClearAll(), "TrashClearButton");
            // 列表 Del 键 = 删除选中（恢复走双击/按钮；回收站里 Del 的直觉是"彻底删"）
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Delete) return;
                DiscardSelected();
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            _list.ItemSelectionChanged += (s, e) => UpdateButtons();

            _empty = EmptyStateView.Attach(this,
                L10n.T("回收站是空的"),
                L10n.T("删除书签/模板/规则等条目会先放在这里，可随时恢复。注意：内容在应用重启后清空。"),
                L10n.T("关闭"), (s, e) => Close());
            _empty.Name = "TrashEmptyState";

            // 工具条按钮行（流式惯例，右对齐关闭钮）
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FormFontPolicy.FieldHeight(this) + 24,
                BackColor = GdtermColorTable.Background
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
                Name = "TrashCloseButton",
                Text = L10n.T("关闭"),
                AutoSize = true,
                Height = FormFontPolicy.FieldHeight(this),
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 4), DpiScale.V(this, 12), DpiScale.V(this, 4)),
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0)
            };
            btnClose.Click += (s, e) => Close();
            rightFlow.Controls.Add(btnClose);
            bottom.Controls.Add(rightFlow);

            Controls.Add(_shell);
            Controls.Add(bottom);
            try { UiA11y.AutoName(this); } catch (Exception) { }

            TrashStore.Default.ItemAdded += OnItemAdded;
            Reload();
        }

        private void OnItemAdded(object sender, TrashStore.Entry entry)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new MethodInvoker(Reload)); } catch (Exception) { } return; }
            Reload();
        }

        private void Reload()
        {
            if (IsDisposed) return;
            var entries = TrashStore.Default.List();
            var items = new List<ListViewItem>(entries.Count);
            foreach (var en in entries)
            {
                var it = new ListViewItem(en.CategoryLabel);
                it.SubItems.Add(en.Description);
                it.SubItems.Add(en.DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                it.Tag = en.Id;
                items.Add(it);
            }
            _shell.SetItems(items);
            _empty.SetEmpty(items.Count == 0);
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            int n = 0;
            foreach (ListViewItem it in _list.SelectedItems) if (it.Checked || it.Selected) n++;
            // ListShell 的勾选批量语义：勾选数 > 0 时按钮亮（与宏/端口转发页一致）
            int checkedCount = 0;
            foreach (ListViewItem it in _list.Items) if (it.Checked) checkedCount++;
            bool any = checkedCount > 0 || _list.SelectedItems.Count > 0;
            if (_btnRestoreSel != null) _btnRestoreSel.Enabled = any;
            if (_btnDeleteSel != null) _btnDeleteSel.Enabled = any;
            if (_btnClear != null) _btnClear.Enabled = TrashStore.Default.Count > 0;
        }

        private IList<ListViewItem> Picked()
        {
            var picked = _shell.SelectedOrChecked();
            return picked;
        }

        private void RestoreSelected()
        {
            var picked = Picked();
            if (picked.Count == 0)
            {
                ToastNotifier.Info(L10n.T("先勾选或选中要恢复的条目"));
                return;
            }
            int ok = 0, fail = 0;
            var firstError = string.Empty;
            foreach (var it in picked)
            {
                var id = it.Tag as string;
                if (string.IsNullOrEmpty(id)) continue;
                string err;
                if (TrashStore.Default.TryRestore(id, out err)) ok++;
                else { fail++; if (firstError.Length == 0) firstError = err; }
            }
            if (fail == 0) ToastNotifier.Success(L10n.Tf("已恢复 {0} 项", ok));
            else if (ok == 0) ToastNotifier.Error(L10n.T("恢复失败：") + firstError);
            else ToastNotifier.Warning(L10n.Tf("恢复 {0} 项，{1} 项失败（{2}）", ok, fail, firstError));
            Reload();
        }

        private void DiscardSelected()
        {
            var picked = Picked();
            if (picked.Count == 0)
            {
                ToastNotifier.Info(L10n.T("先勾选或选中要删除的条目"));
                return;
            }
            if (MessageBox.Show(FindForm(), L10n.Tf("彻底删除选中的 {0} 项？删除后不可恢复。", picked.Count),
                    L10n.T("回收站"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var ids = new List<string>();
            foreach (var it in picked)
            {
                var id = it.Tag as string;
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            TrashStore.Default.RemoveMany(ids);
            ToastNotifier.Success(L10n.Tf("已删除 {0} 项", ids.Count));
            Reload();
        }

        private void ClearAll()
        {
            int n = TrashStore.Default.Count;
            if (n == 0)
            {
                ToastNotifier.Info(L10n.T("回收站已经是空的"));
                return;
            }
            var ask = L10n.Tf("彻底清空回收站（{0} 项）？清空后不可恢复。", n);
            if (MessageBox.Show(FindForm(), ask, L10n.T("回收站"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            TrashStore.Default.Clear();
            Reload();
            ToastNotifier.Success(L10n.T("回收站已清空"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_unsubscribed)
            {
                _unsubscribed = true;
                TrashStore.Default.ItemAdded -= OnItemAdded;
            }
            base.Dispose(disposing);
        }
    }
}
