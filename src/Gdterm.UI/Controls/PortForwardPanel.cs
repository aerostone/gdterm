using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 端口转发管理面板——可视化管理本地/远程/动态端口转发
    /// </summary>
    public class PortForwardPanel : UserControl
    {
        private ListView _lvRules;
        private readonly PortForwardManager _manager;
        private readonly List<PortForwardRule> _rules = new List<PortForwardRule>();
        private ISshPortForwardHost _host;

        public PortForwardPanel(PortForwardManager manager)
        {
            _manager = manager;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            BuildUI();
        }

        /// <summary>绑定端口转发宿主（由 Terminal 层适配，UI 不碰 SshClient）</summary>
        public void SetPortForwardHost(ISshPortForwardHost host)
        {
            _host = host;
            try { _manager.Bind(host); } catch { }
        }

        /// <summary>兼容旧名：内部仍转 Bind</summary>
        public void SetSshClient(ISshPortForwardHost host) { SetPortForwardHost(host); }

        /// <summary>当前是否已绑定可用的 SSH 宿主</summary>
        public bool HasSshClient => _host != null && _host.IsConnected;

        private void BuildUI()
        {
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = GdtermColorTable.Surface };
            var btnAdd = CreateBtn("添加规则", 8);
            var btnStart = CreateBtn("启动", 100);
            var btnStop = CreateBtn("停止", 180);
            var btnDelete = CreateBtn("删除", 260);

            btnAdd.Click += (s, e) => AddRule();
            btnStart.Click += (s, e) => StartSelected();
            btnStop.Click += (s, e) => StopSelected();
            btnDelete.Click += (s, e) => DeleteSelected();

            toolbar.Controls.AddRange(new Control[] { btnAdd, btnStart, btnStop, btnDelete });

            _lvRules = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None
            };
            _lvRules.Columns.Add("类型", 60);
            _lvRules.Columns.Add("名称", 120);
            _lvRules.Columns.Add("本地地址", 140);
            _lvRules.Columns.Add("远程地址", 140);
            _lvRules.Columns.Add("状态", 60);
            _lvRules.Columns.Add("说明", 150);

            Controls.Add(_lvRules);
            Controls.Add(toolbar);
        }

        private Button CreateBtn(string text, int x)
        {
            return new Button
            {
                Text = text, Size = DpiScale.S(this, 75, 28), Location = DpiScale.P(this, x, 6),
                FlatStyle = FlatStyle.Flat, BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground, Font = Services.FormFontPolicy.UiFont(-0.5f)
            };
        }

        private void RefreshList()
        {
            _lvRules.Items.Clear();
            foreach (var r in _rules)
            {
                var item = new ListViewItem(r.Type.ToString());
                item.SubItems.Add(r.Name ?? "");
                item.SubItems.Add(string.Format("{0}:{1}", r.LocalHost, r.LocalPort));
                item.SubItems.Add(string.Format("{0}:{1}", r.RemoteHost, r.RemotePort));
                item.SubItems.Add(_manager.IsActive(r.Id) ? "● 运行" : "○ 停止");
                item.SubItems.Add(r.Description ?? "");
                item.Tag = r;
                if (_manager.IsActive(r.Id)) item.ForeColor = GdtermColorTable.Success;
                _lvRules.Items.Add(item);
            }
        }

        private void AddRule()
        {
            var form = new Form
            {
                Text = "添加端口转发规则", Size = DpiScale.S(this, 400, 320),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = GdtermColorTable.Background, ForeColor = GdtermColorTable.Foreground,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
            };
            var font = Services.FormFontPolicy.UiFont(); int y = 15;
            var cmbType = new AntdUI.Select { Location = DpiScale.P(this, 110, y - 3), Size = DpiScale.S(this, 250, 25), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, FlatStyle = FlatStyle.Flat, Font = font };
            cmbType.Items.AddRange(new object[] { "Local（本地转发）", "Remote（远程转发）", "Dynamic（SOCKS5）" });
            cmbType.SelectedIndex = 0;
            Lbl(form, "类型:", 15, y); form.Controls.Add(cmbType); y += 32;
            var txtName = Txt(form, 110, y, 250); Lbl(form, "名称:", 15, y); y += 32;
            var txtLocalHost = Txt(form, 110, y, 120); txtLocalHost.Text = "127.0.0.1"; Lbl(form, "本地地址:", 15, y);
            var txtLocalPort = Txt(form, 280, y, 80); Lbl(form, "端口:", 245, y); y += 32;
            var txtRemoteHost = Txt(form, 110, y, 120); txtRemoteHost.Text = "127.0.0.1"; Lbl(form, "远程地址:", 15, y);
            var txtRemotePort = Txt(form, 280, y, 80); Lbl(form, "端口:", 245, y); y += 50;

            var btnOk = new AntdUI.Button { Text = "确定", Size = DpiScale.S(this, 80, 28), Location = DpiScale.P(this, 200, y), DialogResult = DialogResult.OK, BackColor = GdtermColorTable.Accent, ForeColor = Color.White };
            var btnCancel = new AntdUI.Button { Text = "取消", Size = DpiScale.S(this, 80, 28), Location = DpiScale.P(this, 290, y), DialogResult = DialogResult.Cancel, BackColor = GdtermColorTable.Hover, ForeColor = GdtermColorTable.Foreground };
            form.Controls.AddRange(new Control[] { btnOk, btnCancel });
            form.AcceptButton = btnOk; form.CancelButton = btnCancel;

            if (form.ShowDialog(this) == DialogResult.OK)
            {
                int lp, rp;
                int.TryParse(txtLocalPort.Text, out lp);
                int.TryParse(txtRemotePort.Text, out rp);
                _rules.Add(new PortForwardRule
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = txtName.Text.Trim(),
                    Type = (PortForwardType)cmbType.SelectedIndex,
                    LocalHost = txtLocalHost.Text.Trim(), LocalPort = lp,
                    RemoteHost = txtRemoteHost.Text.Trim(), RemotePort = rp
                });
                RefreshList();
            }
        }

        private void StartSelected()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            if (_host == null || !_host.IsConnected)
            {
                MessageBox.Show("请先打开并连接一个 SSH 终端标签，再启动端口转发。", "端口转发",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { _manager.Bind(_host); } catch { }
            var rule = _lvRules.SelectedItems[0].Tag as PortForwardRule;
            if (rule == null) return;
            bool ok = rule.Type == PortForwardType.Local
                ? _manager.StartLocal(rule)
                : rule.Type == PortForwardType.Remote ? _manager.StartRemote(rule) : false;
            if (!ok) MessageBox.Show("启动失败", "gdterm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshList();
        }

        private void StopSelected()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            var rule = _lvRules.SelectedItems[0].Tag as PortForwardRule;
            if (rule != null) { try { _manager.Bind(_host); } catch { } _manager.Stop(rule.Id); RefreshList(); }
        }

        private void DeleteSelected()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            var rule = _lvRules.SelectedItems[0].Tag as PortForwardRule;
            if (rule != null) { try { _manager.Bind(_host); } catch { } _manager.Stop(rule.Id); _rules.Remove(rule); RefreshList(); }
        }

        private static void Lbl(Form f, string t, int x, int y) { f.Controls.Add(new Label { Text = t, Location = DpiScale.P(f, x, y + 3), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = GdtermColorTable.Foreground }); }
        private static TextBox Txt(Form f, int x, int y, int w) { var t = new AntdUI.Input { Location = DpiScale.P(f, x, y), Size = DpiScale.S(f, w, 24), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = new Font("Consolas", 9f)}; f.Controls.Add(t); return t; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _manager?.Dispose(); } catch { }
                _host = null;
                try { _manager?.Unbind(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
