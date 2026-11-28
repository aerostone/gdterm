using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    public sealed class PortForwardRuleForm : AntdUI.Window
    {
        private readonly PortForwardRule _original;
        private readonly Action<PortForwardRule> _save;
        private readonly IList<ConnectionConfig> _connections;
        private readonly AntdUI.Input _name, _localHost, _localPort, _remoteHost, _remotePort, _description;
        private readonly AntdUI.Select _type, _connection;
        private readonly AntdUI.Label _localLabel, _remoteLabel, _hint, _error;
        public PortForwardRule Result { get; private set; }

        public PortForwardRuleForm(IList<ConnectionConfig> connections, PortForwardRule existing = null,
            Action<PortForwardRule> save = null)
        {
            _original = existing == null ? new PortForwardRule { Id = Guid.NewGuid().ToString("N") } : PortForwardManager.Copy(existing);
            _save = save;
            _connections = new List<ConnectionConfig>(connections ?? new List<ConnectionConfig>());
            if (!string.IsNullOrEmpty(_original.ConnectionId) && !_connections.Any(c => c.Id == _original.ConnectionId))
                _connections.Add(new ConnectionConfig { Id = _original.ConnectionId, Name = _original.ConnectionName ?? _original.ConnectionId });
            Text = existing == null ? L10n.T("添加端口转发规则") : L10n.T("编辑端口转发规则");
            Font = FormFontPolicy.UiFont();
            BackColor = GdtermColorTable.Background; ForeColor = GdtermColorTable.Foreground;
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            int pad = DpiScale.V(this, 16), gap = DpiScale.V(this, 8);
            int labelWidth = FormFontPolicy.LabelColumn(this, 100, L10n.T("所属 SSH 连接"), L10n.T("本地监听"), L10n.T("远程监听"), L10n.T("本地目标"), L10n.T("远程目标"));
            int width = Math.Max(DpiScale.V(this, 620), labelWidth + DpiScale.V(this, 420));
            int x = pad + labelWidth, inputWidth = width - x - pad;
            int fieldHeight = FormFontPolicy.FieldHeight(this), step = fieldHeight + gap, y = pad;
            _type = new AntdUI.Select { Name = "ForwardType", Location = new Point(x, y), Size = new Size(inputWidth, fieldHeight) };
            _type.Items.AddRange(new object[] { L10n.T("Local（本地转发）"), L10n.T("Remote（远程转发）"), "SOCKS5" });
            AddLabel(L10n.T("类型"), pad, y, labelWidth, fieldHeight); Controls.Add(_type); y += step;
            _name = Input("ForwardName", x, y, inputWidth, fieldHeight, _original.Name);
            AddLabel(L10n.T("名称"), pad, y, labelWidth, fieldHeight); y += step;
            _connection = new AntdUI.Select { Name = "ForwardConnection", Location = new Point(x, y), Size = new Size(inputWidth, fieldHeight) };
            _connection.Items.Add(L10n.T("稍后选择 SSH 连接"));
            foreach (var c in _connections) _connection.Items.Add((c.Name ?? c.Id) + (string.IsNullOrEmpty(c.Host) ? "" : " — " + c.Host));
            AddLabel(L10n.T("所属 SSH 连接"), pad, y, labelWidth, fieldHeight); Controls.Add(_connection); y += step;
            int portWidth = DpiScale.V(this, 95), hostWidth = inputWidth - portWidth - gap;
            _localLabel = AddLabel(L10n.T("本地监听"), pad, y, labelWidth, fieldHeight);
            _localHost = Input("ForwardLocalHost", x, y, hostWidth, fieldHeight, _original.LocalHost);
            _localPort = Input("ForwardLocalPort", x + hostWidth + gap, y, portWidth, fieldHeight, _original.LocalPort == 0 ? "" : _original.LocalPort.ToString());
            _localPort.PlaceholderText = L10n.T("端口"); _localPort.AccessibleName = L10n.T("本地端口"); y += step;
            _remoteLabel = AddLabel(L10n.T("远程目标"), pad, y, labelWidth, fieldHeight);
            _remoteHost = Input("ForwardRemoteHost", x, y, hostWidth, fieldHeight, _original.RemoteHost);
            _remotePort = Input("ForwardRemotePort", x + hostWidth + gap, y, portWidth, fieldHeight, _original.RemotePort == 0 ? "" : _original.RemotePort.ToString());
            _remotePort.PlaceholderText = L10n.T("端口"); _remotePort.AccessibleName = L10n.T("远程端口"); y += step;
            _description = Input("ForwardDescription", x, y, inputWidth, fieldHeight, _original.Description);
            AddLabel(L10n.T("说明"), pad, y, labelWidth, fieldHeight); y += step;
            int line = FormFontPolicy.LineBox(this);
            _hint = AddLabel("", pad, y, width - 2 * pad, line * 3);
            _hint.Name = "ForwardHint"; _hint.ForeColor = GdtermColorTable.Muted; y += line * 3 + gap;
            _error = AddLabel("", pad, y, width - 2 * pad, line * 2);
            _error.Name = "ForwardValidation"; _error.ForeColor = GdtermColorTable.Danger; y += line * 2 + gap;
            var ok = new AntdUI.Button { Name = "ForwardSave", Text = L10n.T("保存"), Type = AntdUI.TTypeMini.Primary, Location = new Point(x, y) };
            var cancel = new AntdUI.Button { Text = L10n.T("取消"), DialogResult = DialogResult.Cancel, Location = new Point(x, y) };
            ok.Click += (s, e) => SaveRule();
            Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(width, y + fieldHeight + pad);
            FormFontPolicy.MountDialogActions(this, ok, cancel);
            _type.SelectedIndexChanged += (s, e) => UpdateType();
            _type.SelectedIndex = (int)_original.Type;
            _connection.SelectedIndex = string.IsNullOrEmpty(_original.ConnectionId) ? 0 :
                _connections.ToList().FindIndex(c => c.Id == _original.ConnectionId) + 1;
            UpdateType();
            UiA11y.AutoName(this);
        }
        private void UpdateType()
        {
            var type = (PortForwardType)_type.SelectedIndex;
            _localLabel.Text = type == PortForwardType.Remote ? L10n.T("本地目标") : L10n.T("本地监听");
            _remoteLabel.Text = type == PortForwardType.Remote ? L10n.T("远程监听") : L10n.T("远程目标");
            _remoteLabel.Visible = _remoteHost.Visible = _remotePort.Visible = type != PortForwardType.Dynamic;
            _hint.Text = type == PortForwardType.Dynamic ? L10n.T("SOCKS5：应用连接本地代理，由 SSH 服务器访问目标。\n监听 127.0.0.1 仅允许本机使用。") :
                type == PortForwardType.Remote ? L10n.T("远程监听 → 本地目标（从本机访问）。\n远程非回环监听需要服务器允许 GatewayPorts。") :
                L10n.T("本地监听 → 远程目标（从 SSH 服务器访问）。\n监听 127.0.0.1 仅允许本机使用。");
            _error.Text = "";
        }
        private void SaveRule()
        {
            var rule = PortForwardManager.Copy(_original);
            rule.Type = (PortForwardType)_type.SelectedIndex; rule.Name = _name.Text.Trim();
            rule.LocalHost = _localHost.Text.Trim(); rule.RemoteHost = _remoteHost.Text.Trim();
            int local, remote;
            int.TryParse(_localPort.Text.Trim(), out local); int.TryParse(_remotePort.Text.Trim(), out remote);
            rule.LocalPort = local; rule.RemotePort = rule.Type == PortForwardType.Dynamic ? 0 : remote;
            rule.Description = _description.Text.Trim();
            int index = _connection.SelectedIndex - 1;
            var connection = index >= 0 && index < _connections.Count ? _connections[index] : null;
            rule.ConnectionId = connection?.Id; rule.ConnectionName = connection?.Name ?? connection?.Host;
            var error = rule.EndpointError();
            if (string.IsNullOrWhiteSpace(rule.Name)) error = L10n.T("请填写规则名称");
            if (error != null) { _error.Text = error; return; }
            try { _save?.Invoke(rule); }
            catch (Exception ex) { _error.Text = L10n.T("保存失败：") + ex.Message; return; }
            Result = rule; DialogResult = DialogResult.OK; Close();
        }
        private AntdUI.Label AddLabel(string text, int x, int y, int w, int h)
        {
            var label = new AntdUI.Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = Font, ForeColor = ForeColor };
            Controls.Add(label); return label;
        }
        private AntdUI.Input Input(string name, int x, int y, int w, int h, string value)
        {
            var input = new AntdUI.Input { Name = name, Location = new Point(x, y), Size = new Size(w, h), Text = value ?? "", Font = Font,
                BackColor = GdtermColorTable.Surface, ForeColor = ForeColor };
            Controls.Add(input); return input;
        }
    }
}
