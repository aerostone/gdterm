using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 端口转发管理面板——可视化管理本地/远程/动态端口转发
    /// </summary>
    public class PortForwardPanel : UserControl
    {
        private ListView _lvRules;
        private EmptyStateView _empty;
        private readonly PortForwardManager _manager;
        private readonly List<PortForwardRule> _rules = new List<PortForwardRule>();
        private ISshPortForwardHost _host;

        public PortForwardPanel(PortForwardManager manager)
        {
            _manager = manager;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            BuildUI();

            // 审计 C1 收尾（CI 0.1.348 实证）：构造后首刷一次。此前首次打开面板时
            // RefreshList 无人调用，EmptyStateView 初值 Visible=false 永不翻亮。
            try { RefreshList(); }
            catch (Exception exSwallowed) { try { DiagLog.Swallowed("PortForwardPanel.Ctor.Refresh", exSwallowed); } catch { } }
        }

        /// <summary>绑定端口转发宿主（由 Terminal 层适配，UI 不碰 SshClient）</summary>
        public void SetPortForwardHost(ISshPortForwardHost host)
        {
            _host = host;
            try { _manager.Bind(host); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("PortForwardPanel", exSwallowed); } catch { } }
        }

        /// <summary>兼容旧名：内部仍转 Bind</summary>
        public void SetSshClient(ISshPortForwardHost host) { SetPortForwardHost(host); }

        /// <summary>当前是否已绑定可用的 SSH 宿主</summary>
        public bool HasSshClient => _host != null && _host.IsConnected;

        private void BuildUI()
        {
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 5), DpiScale.V(this, 8), DpiScale.V(this, 5))
            };
            var btnAdd = CreateBtn("添加规则");
            var btnStart = CreateBtn("启动");
            var btnStop = CreateBtn("停止");
            var btnDelete = CreateBtn("删除");

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
                Font = Services.FormFontPolicy.MonoFont()
            };
            _lvRules.Columns.Add("类型", 60);
            _lvRules.Columns.Add("名称", 120);
            _lvRules.Columns.Add("本地地址", 140);
            _lvRules.Columns.Add("远程地址", 140);
            _lvRules.Columns.Add("状态", 60);
            _lvRules.Columns.Add("说明", 150);

            // 激活路径：双击 = 启动/停止切换；回车同双击；Delete 删除（此前列表只能靠下面四个按钮）
            _lvRules.DoubleClick += (s2, e2) => ToggleSelected();
            _lvRules.KeyDown += (s2, e2) =>
            {
                if (e2.KeyCode == Keys.Enter) { ToggleSelected(); e2.Handled = true; }
                else if (e2.KeyCode == Keys.Delete) { DeleteSelected(); e2.Handled = true; }
            };

            Controls.Add(_lvRules);
            Controls.Add(toolbar);

            // 审计 C1：空隧道列表给出下一步动作（此前只有一排禁用按钮，用户不知道要干什么）
            _empty = EmptyStateView.Attach(this, "还没有端口转发规则",
                "把本地端口映射到远端服务（ssh -L），用于内网数据库/Web 面板",
                "新建规则", (s2, e2) => AddRule());
        }

        private AntdUI.Button CreateBtn(string text)
        {
            return new AntdUI.Button
            {
                Text = text, AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 4), DpiScale.V(this, 10), DpiScale.V(this, 4)),
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0),
                BackColor = GdtermColorTable.Surface,
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
            if (_empty != null) _empty.SetEmpty(_rules.Count == 0);
        }

        private void AddRule()
        {
            var form = new Form
            {
                Text = "添加端口转发规则", // ClientSize 在按钮行后按行高动态计算（见下方）
                StartPosition = FormStartPosition.CenterParent,
                BackColor = GdtermColorTable.Background, ForeColor = GdtermColorTable.Foreground,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
            };
            var font = Services.FormFontPolicy.UiFont();
            // P0-1：固定 y 步进→字体驱动行高（原先 32/50 在大字号下重叠）
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = fieldH + DpiScale.V(this, 6);
            int y = 15;
            var cmbType = new AntdUI.Select { Location = DpiScale.P(this, 110, y), Size = new Size(DpiScale.V(this, 250), fieldH), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = font };
            cmbType.Items.AddRange(new object[] { "Local（本地转发）", "Remote（远程转发）", "Dynamic（SOCKS5）" });
            cmbType.SelectedIndex = 0;
            Lbl(form, "类型:", 15, y); form.Controls.Add(cmbType); y += rowH;
            var txtName = Txt(form, 110, y, 250, fieldH); Lbl(form, "名称:", 15, y); y += rowH;
            var txtLocalHost = Txt(form, 110, y, 120, fieldH); txtLocalHost.Text = "127.0.0.1"; Lbl(form, "本地地址:", 15, y);
            var txtLocalPort = Txt(form, 280, y, 80, fieldH); Lbl(form, "端口:", 245, y); y += rowH;
            var txtRemoteHost = Txt(form, 110, y, 120, fieldH); txtRemoteHost.Text = "127.0.0.1"; Lbl(form, "远程地址:", 15, y);
            var txtRemotePort = Txt(form, 280, y, 80, fieldH); Lbl(form, "端口:", 245, y); y += rowH + DpiScale.V(this, 12);

            int btnH = Math.Max(DpiScale.V(this, 30), fieldH);
            var btnOk = new AntdUI.Button { Text = "保存", Size = new Size(DpiScale.V(this, 80), btnH), Location = DpiScale.P(this, 200, y), DialogResult = DialogResult.OK, Type = AntdUI.TTypeMini.Primary, };
            var btnCancel = new AntdUI.Button { Text = "取消", Size = new Size(DpiScale.V(this, 80), btnH), Location = DpiScale.P(this, 290, y), DialogResult = DialogResult.Cancel, BackColor = GdtermColorTable.Surface2, ForeColor = GdtermColorTable.Foreground };
            form.Controls.AddRange(new Control[] { btnOk, btnCancel });
            form.AcceptButton = btnOk; form.CancelButton = btnCancel;
            // 客户区高度随行高自适应（原先固定 320 在大字号下裁剪）
            form.ClientSize = new Size(DpiScale.V(this, 400), y + btnH + DpiScale.V(this, 16));

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

        /// <summary>双击/回车：已运行则停，未运行则启。</summary>
        private void ToggleSelected()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            var rule = _lvRules.SelectedItems[0].Tag as PortForwardRule;
            if (rule == null) return;
            if (IsRunning(rule)) StopSelected();
            else StartSelected();
        }

        private bool IsRunning(PortForwardRule rule)
        {
            try { return rule != null && _manager != null && _manager.IsActive(rule.Id); }
            catch (Exception) { return false; }
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
            try { _manager.Bind(_host); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("PortForwardPanel", exSwallowed); } catch { } }
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
            if (rule != null) { try { _manager.Bind(_host); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("PortForwardPanel", exSwallowed); } catch { } } _manager.Stop(rule.Id); RefreshList(); }
        }

        private void DeleteSelected()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            var rule = _lvRules.SelectedItems[0].Tag as PortForwardRule;
            if (rule != null) { try { _manager.Bind(_host); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("PortForwardPanel", exSwallowed); } catch { } } _manager.Stop(rule.Id); _rules.Remove(rule); RefreshList(); }
        }

        private static void Lbl(Form f, string t, int x, int y) { f.Controls.Add(new AntdUI.Label { Text = t, Location = DpiScale.P(f, x, y + 3), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = GdtermColorTable.Foreground }); }
        private static AntdUI.Input Txt(Form f, int x, int y, int w, int fieldH) { var t = new AntdUI.Input { Location = DpiScale.P(f, x, y), Size = new Size(DpiScale.V(f, w), fieldH), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = Services.FormFontPolicy.MonoFont()}; f.Controls.Add(t); return t; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _manager?.Dispose(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("PortForwardPanel", exSwallowed); } catch { } }
                _host = null;
                try { _manager?.Unbind(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("PortForwardPanel", exSwallowed); } catch { } }
            }
            base.Dispose(disposing);
        }
    }
}
