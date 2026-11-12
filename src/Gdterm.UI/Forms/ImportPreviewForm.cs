using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 导入连接预览/裁定（import 审计补齐：原来「选完文件就直接写库，结束后弹一个统计」，
    /// 用户没有看到将写入什么、也不能取消；键盘路径也缺失）。
    ///
    /// 现在：选中文件 → 解析 → 本对话框列出「新增 / 重复跳过」→ 回车确认、Esc 取消。
    /// 不放宽任何写库逻辑，只是把原有的写库动作从「自动」变成「确认后」。
    /// </summary>
    public sealed class ImportPreviewForm : AntdUI.Window
    {
        /// <summary>用户是否确认执行导入。</summary>
        public bool Confirmed { get; private set; }

        private readonly AntdUI.Input _filter;
        private readonly ListBox _list;
        private readonly List<string> _all = new List<string>();
        private EmptyStateView _empty;

        public ImportPreviewForm(IList<ConnectionConfig> add, IList<ConnectionConfig> skipped)
        {
            int addCount = add != null ? add.Count : 0;
            int skipCount = skipped != null ? skipped.Count : 0;

            Text = L10n.T("导入连接 · 预览");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false;
            AutoHandDpi = false;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont();
            ClientSize = DpiScale.S(this, 560, 420);

            var title = FormFontPolicy.FitSingleLineLabel(new AntdUI.Label
            {
                Dock = DockStyle.Top,
                Text = L10n.T("将新增 ") + addCount + L10n.T(" 条连接") + (skipCount > 0 ? L10n.T("，跳过 ") + skipCount + L10n.T(" 条重复") : ""),
                Font = FormFontPolicy.UiFont(+1f, FontStyle.Bold),
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 10), DpiScale.V(this, 12), 0)
            });

            _filter = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this)),
                PlaceholderText = L10n.T("在将要导入的条目里过滤（名称 / 主机）…"),
                AllowClear = true,
                Margin = new Padding(0)
            };
            _filter.TextChanged += (s, e) => ApplyFilter();
            _filter.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down) { _list.Focus(); if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0; e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.MonoFont(),
                IntegralHeight = false
            };

            if (add != null)
            {
                foreach (var c in add)
                    _all.Add(Format(c, "+ "));
            }
            if (skipped != null)
            {
                foreach (var c in skipped)
                    _all.Add(Format(c, L10n.T("= 已存在，跳过 ")));
            }

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 6), DpiScale.V(this, 12), DpiScale.V(this, 8))
            };
            var ok = new AntdUI.Button {
                Text = L10n.T("导入"),
                Type = AntdUI.TTypeMini.Primary,
                Margin = new Padding(0),
                // 没有任何可新增条目时禁用：确定键在这里等于「白导一次」
                Enabled = addCount > 0
            };
            FormFontPolicy.SizeActionButton(this, ok, 14, 4);
            ok.Click += (s, e) => { Confirmed = true; DialogResult = DialogResult.OK; Close(); };
            var cancel = new AntdUI.Button {
                Text = L10n.T("取消"),
                Type = AntdUI.TTypeMini.Default,
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0)
            };
            FormFontPolicy.SizeActionButton(this, cancel, 14, 4);
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            // 过滤后为空要给引导（列表型 UI 规则 R09）
            _empty = EmptyStateView.Attach(this, L10n.T("没有匹配的连接"),
                L10n.T("清空过滤词可看到全部待导入条目"));
            Controls.Add(_list);
            Controls.Add(_filter);
            Controls.Add(bottom);
            Controls.Add(title);

            AcceptButton = ok;
            CancelButton = cancel;
            FormFontPolicy.Apply(this);
            ApplyFilter();
            Shown += (s, e) => { try { _filter.Focus(); } catch (Exception) { } };
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private static string Format(ConnectionConfig c, string prefix)
        {
            if (c == null) return prefix + L10n.T("(空)");
            return prefix + (c.Name ?? L10n.T("(无名称)")) + "  " + c.Protocol + "  " + (c.Host ?? "") + ":" + c.Port;
        }

        private void ApplyFilter()
        {
            var q = (_filter.Text ?? "").Trim();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var line in _all)
            {
                if (q.Length == 0 || line.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    _list.Items.Add(line);
            }
            _list.EndUpdate();
            if (_empty != null) _empty.SetEmpty(_list.Items.Count == 0);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }

        /// <summary>解析阶段失败（格式不支持/文件损坏）的统一提示：键盘可关。</summary>
        public static void WarnParseFailure(IWin32Window owner, Exception ex, IList<string> files)
        {
            var names = files == null ? "" : string.Join("、", files.Take(3).ToArray());
            try { DiagLog.Error("ConnectionImport.Parse", ex); } catch (Exception) { }
            MessageBox.Show(owner,
                L10n.T("解析失败") + (string.IsNullOrEmpty(names) ? "" : "：" + names) + "\n" + (ex != null ? ex.Message : L10n.T("未知错误"))
                + L10n.T("\n\n支持格式：Xshell(.xsh)、MobaXterm(.ini)、WindTerm(user.sessions)、JSON、CSV、mRemoteNG(.xml)"),
                L10n.T("导入连接"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
