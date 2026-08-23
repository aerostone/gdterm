using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// Ctrl+Shift+K 全局连接快速跳转（普通 Ctrl+K 是 shell kill-line，不占用）。
    /// </summary>
    public sealed class ConnectionQuickJumpForm : Form
    {
        private readonly IConnectionStore _store;
        private readonly TextBox _filter;
        private readonly ListBox _list;
        private List<ConnectionConfig> _all = new List<ConnectionConfig>();

        public ConnectionConfig Selected { get; private set; }

        public ConnectionQuickJumpForm(IConnectionStore store)
        {
            _store = store;
            Text = "快速跳转连接 (Ctrl+Shift+K)";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 360);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            KeyPreview = true;
            ShowInTaskbar = false;

            _filter = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 10f)
            };
            try { WinFormsCompat.SetCueBanner(_filter, "输入名称 / 主机 / 分组…"); } catch { }
            _filter.TextChanged += (s, e) => ApplyFilter();
            _filter.KeyDown += OnFilterKey;

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 10f),
                IntegralHeight = false
            };
            _list.DoubleClick += (s, e) => AcceptSelection();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { AcceptSelection(); e.Handled = true; }
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            Controls.Add(_list);
            Controls.Add(_filter);
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadData();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        private void LoadData()
        {
            try { _all = (_store.LoadAll() ?? new List<ConnectionConfig>()).ToList(); }
            catch { _all = new List<ConnectionConfig>(); }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var q = (_filter.Text ?? "").Trim().ToLowerInvariant();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in _all)
            {
                if (c == null) continue;
                var line = string.Format("{0}  [{1}]  {2}:{3}  {4}",
                    c.Name ?? "",
                    c.Protocol,
                    c.Host ?? "",
                    c.Port,
                    c.GroupPath ?? "");
                if (q.Length == 0
                    || line.ToLowerInvariant().Contains(q)
                    || (c.Name ?? "").ToLowerInvariant().Contains(q)
                    || (c.Host ?? "").ToLowerInvariant().Contains(q)
                    || (c.GroupPath ?? "").ToLowerInvariant().Contains(q))
                {
                    _list.Items.Add(new Item { Config = c, Text = line });
                }
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }

        private void OnFilterKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                _list.Focus();
                if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                AcceptSelection();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        private void AcceptSelection()
        {
            var item = _list.SelectedItem as Item;
            if (item == null && _list.Items.Count > 0) item = _list.Items[0] as Item;
            if (item == null) return;
            Selected = item.Config;
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class Item
        {
            public ConnectionConfig Config;
            public string Text;
            public override string ToString() { return Text; }
        }
    }
}
