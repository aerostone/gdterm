using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// Ctrl+Shift+K 全局连接快速跳转（普通 Ctrl+K 是 shell kill-line，不占用）。
    /// </summary>
    public sealed class ConnectionQuickJumpForm : AntdUI.Window
    {
        private readonly IConnectionStore _store;
        private readonly AntdUI.Input _filter;
        private readonly ListBox _list;
        private List<ConnectionConfig> _all = new List<ConnectionConfig>();
        private Dictionary<string, int> _recencyRank = new Dictionary<string, int>();
        private EmptyStateView _empty;

        public ConnectionConfig Selected { get; private set; }

        public ConnectionQuickJumpForm(IConnectionStore store)
            : this(store, null)
        {
        }

        /// <param name="bookmarkStore">提供「最近使用」排序（null 时按名称排）。</param>
        public ConnectionQuickJumpForm(IConnectionStore store, IBookmarkStore bookmarkStore)
        {
            _store = store;
            if (bookmarkStore != null)
            {
                try
                {
                    var recent = bookmarkStore.GetRecentConnections(50) ?? new List<RecentConnection>();
                    int rank = 0;
                    foreach (var r in recent)
                    {
                        if (r == null || string.IsNullOrEmpty(r.ConnectionId) || _recencyRank.ContainsKey(r.ConnectionId)) continue;
                        _recencyRank[r.ConnectionId] = rank++;
                    }
                }
                catch (Exception ex)
                {
                    try { DiagLog.Swallowed("QuickJump.Recent", ex); } catch { }
                    // 最近列表读失败 → 退回名称排序
                }
            }
            Text = L10n.T("快速跳转连接 (Ctrl+Shift+K)");
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            AutoHandDpi = false; // 尺寸已按 DpiScale 预缩放，关掉 AntdUI 的自动 DPI 避免二次放大
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = DpiScale.S(this, 520, 360);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            KeyPreview = true;
            ShowInTaskbar = false;

            _filter = new AntdUI.Input {
                Dock = DockStyle.Top,
                MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this)),
                PlaceholderText = L10n.T("输入名称 / 主机 / 分组…"),
                AllowClear = true,
                Font = Services.FormFontPolicy.UiFont(+1f)
            };
            _filter.TextChanged += (s, e) => ApplyFilter();
            _filter.KeyDown += OnFilterKey;

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
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
            _empty = EmptyStateView.Attach(this, L10n.T("没有匹配的连接"),
                L10n.T("改个关键词，或先去「连接 → 新建连接」把主机存下来"), null, null);
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadData();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private void LoadData()
        {
            try { _all = (_store.LoadAll() ?? new List<ConnectionConfig>()).ToList(); }
            catch (Exception ex)
            {
                // 原来这里静默吞异常：连接库读失败时用户只看到「没有匹配的连接」，会以为收藏丢了
                _all = new List<ConnectionConfig>();
                try { DiagLog.Error("QuickJump.LoadAll", ex); } catch (Exception) { }
                if (_empty != null)
                    _empty.SetEmpty(true, L10n.T("连接库读取失败"), L10n.T("检查 ") + ex.Message + L10n.T("；恢复后重开本窗口即可重新加载"));
                ToastNotifier.Error(L10n.T("读取连接列表失败：") + ex.Message);
                ApplyFilter();
                return;
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var q = (_filter.Text ?? "").Trim().ToLowerInvariant();
            var matched = new List<ConnectionConfig>();
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
                    matched.Add(c);
                }
            }
            // 排序（F5）：最近使用优先（rank 越小越近），未记录过的按名称
            matched.Sort((a, b) =>
            {
                int ra, rb;
                _recencyRank.TryGetValue(a.Id ?? "", out ra);
                _recencyRank.TryGetValue(b.Id ?? "", out rb);
                bool ha = _recencyRank.ContainsKey(a.Id ?? "");
                bool hb = _recencyRank.ContainsKey(b.Id ?? "");
                if (ha != hb) return ha ? -1 : 1;
                if (ha && rb != ra) return ra.CompareTo(rb);
                return string.Compare((a.Name ?? ""), (b.Name ?? ""),
                    StringComparison.InvariantCulture);
            });
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in matched)
            {
                var line = string.Format("{0}  [{1}]  {2}:{3}  {4}",
                    c.Name ?? "",
                    c.Protocol,
                    c.Host ?? "",
                    c.Port,
                    c.GroupPath ?? "");
                _list.Items.Add(new Item { Config = c, Text = line });
            }
            _list.EndUpdate();
            if (_empty != null) _empty.SetEmpty(_list.Items.Count == 0);
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
            var item = _list.SelectedValue as Item;
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
