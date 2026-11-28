using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;
using Gdterm.UI.Services;

namespace Gdterm.Tests.Ui
{
    internal static class PortForwardUiSmoke
    {
        internal static IList<ConnectionConfig> Connections() => new List<ConnectionConfig>
        {
            new ConnectionConfig { Id = "ssh-a", Name = "Database A", Host = "db.example.test" },
            new ConnectionConfig { Id = "ssh-b", Name = "Server B", Host = "other.example.test" }
        };
        internal static PortForwardRule Rule(string id = "rule-a", PortForwardType type = PortForwardType.Local) => new PortForwardRule
        {
            Id = id, Name = "Database / " + id, Type = type, ConnectionId = "ssh-a", ConnectionName = "Database A",
            LocalHost = "127.0.0.1", LocalPort = 15432, RemoteHost = "db.internal", RemotePort = 5432, Description = "Database access through SSH"
        };
        internal static PortForwardPanel Populated(UiTempScope scope)
        {
            var workspace = scope.Track(new PortForwardWorkspace(null, new PortForwardManager(), null, Connections, null));
            workspace.Save(Rule()); workspace.Save(Rule("socks", PortForwardType.Dynamic));
            return new PortForwardPanel(workspace);
        }
        internal static PortForwardPanel Statuses(UiTempScope scope)
        {
            var workspace = scope.Track(new PortForwardWorkspace(null, new PortForwardManager(), null, Connections, null));
            workspace.Save(Rule("running")); workspace.Save(Rule("starting", PortForwardType.Remote));
            workspace.Save(Rule("failed", PortForwardType.Dynamic));
            // Display fixtures only. Real network states are verified by port-forward-smoke.
            var statuses = (Dictionary<string, PortForwardStatus>)typeof(PortForwardManager)
                .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(workspace.Manager);
            statuses["running"] = new PortForwardStatus(PortForwardState.Running);
            statuses["starting"] = new PortForwardStatus(PortForwardState.Starting);
            statuses["failed"] = new PortForwardStatus(PortForwardState.Failed, "Address already in use: 127.0.0.1:15432");
            return new PortForwardPanel(workspace);
        }
        internal static PortForwardRuleForm Editor(PortForwardType type)
        {
            return new PortForwardRuleForm(Connections(), Rule("edit", type));
        }
        public static void Run(string temp, string directory, Action<bool, string> check)
        {
            UiSmokeRunner.Progress("port-forward:forms");
            using (var scope = new UiTempScope("forward-states"))
            using (var host = new Form { ClientSize = new Size(820, 500) })
            using (var panel = Statuses(scope))
            {
                host.Controls.Add(panel); host.Show(); Application.DoEvents();
                var list = panel.Controls.Find("ForwardRules", true).OfType<ListView>().Single();
                list.Items[0].Selected = true;
                check(!Button(panel, "ForwardEdit").Enabled && !Button(panel, "ForwardStart").Enabled && Button(panel, "ForwardStop").Enabled,
                    "running rule exposes stop and prevents edits/repeated start");
                list.Items[0].Selected = false; list.Items[1].Selected = true;
                check(!Button(panel, "ForwardEdit").Enabled && !Button(panel, "ForwardStart").Enabled && Button(panel, "ForwardStop").Enabled,
                    "pending start exposes cancellation and prevents edits");
                list.Items[1].Selected = false; list.Items[2].Selected = true;
                check(Button(panel, "ForwardEdit").Enabled && Button(panel, "ForwardStart").Enabled && !Button(panel, "ForwardStop").Enabled,
                    "failed rule exposes edit and retry with reason");
                Capture(host, directory, "port-forward-statuses");
            }
            var store = new PortForwardRuleStore(Path.Combine(temp, "port-forwards.json"));
            var trash = new TrashStore();
            var resolved = new List<string>();
            using (var workspace = new PortForwardWorkspace(store, new PortForwardManager(), id => { resolved.Add(id); return null; }, Connections, trash))
            {
                using (var form = new PortForwardRuleForm(Connections(), null, workspace.Save))
                {
                    form.Show(); Application.DoEvents();
                    Button(form, "ForwardSave").PerformClick();
                    check(form.Visible && form.Result == null && workspace.Rules.Count == 0 && Label(form, "ForwardValidation").Text.Length > 0, "invalid save retains form and shows validation");
                    Input(form, "ForwardName").Text = "Real form submit";
                    Input(form, "ForwardLocalPort").Text = "not-a-port";
                    Input(form, "ForwardRemotePort").Text = "5432";
                    Select(form, "ForwardConnection").SelectedIndex = 1;
                    Input(form, "ForwardDescription").Text = "saved description";
                    Button(form, "ForwardSave").PerformClick();
                    check(form.Visible && Input(form, "ForwardLocalPort").Text == "not-a-port" && workspace.Rules.Count == 0, "bad numeric input is not silently saved as zero");
                    Capture(form, directory, "forward-validation");
                    Input(form, "ForwardLocalPort").Text = "15432";
                    Button(form, "ForwardSave").PerformClick();
                    check(form.Result != null && store.Load().Single().ConnectionId == "ssh-a" && store.Load().Single().Description == "saved description", "actual save button persists connection and description");
                }
                var original = workspace.Rules.Single();
                using (var form = new PortForwardRuleForm(Connections(), original, workspace.Save))
                {
                    form.Show(); Application.DoEvents(); Input(form, "ForwardName").Text = "cancelled";
                    ((AntdUI.Button)form.CancelButton).PerformClick();
                    check(store.Load().Single().Name == original.Name && form.Result == null, "cancel leaves disk and original rule unchanged");
                }
                using (var form = new PortForwardRuleForm(Connections(), original, workspace.Save))
                {
                    form.Show(); Application.DoEvents(); Select(form, "ForwardType").SelectedIndex = 2;
                    check(!Input(form, "ForwardRemoteHost").Visible && !Input(form, "ForwardRemotePort").Visible, "SOCKS5 hides fixed destination fields");
                    Input(form, "ForwardDescription").Text = "edited description";
                    Capture(form, directory, "forward-socks-editor");
                    Button(form, "ForwardSave").PerformClick();
                    check(store.Load().Single().Id == original.Id && store.Load().Single().Type == PortForwardType.Dynamic && store.Load().Single().Description == "edited description", "edit preserves ID and changes type/description");
                }
                using (var form = new PortForwardRuleForm(Connections(), original, r => { throw new IOException("fixture write failure"); }))
                {
                    form.Show(); Application.DoEvents(); Button(form, "ForwardSave").PerformClick();
                    check(form.Visible && form.Result == null && Label(form, "ForwardValidation").Text.Contains("fixture write failure"), "persistence failure remains visible and keeps editor open");
                    form.Close();
                }
                workspace.Save(Rule("second"));
                UiSmokeRunner.Progress("port-forward:panel");
                using (var host = new Form { ClientSize = new Size(820, 500) })
                {
                    var panel = new PortForwardPanel(workspace); host.Controls.Add(panel); host.Show(); Application.DoEvents();
                    var list = panel.Controls.Find("ForwardRules", true).OfType<ListView>().Single();
                    check(list.Items.Count == 2 && !Button(panel, "ForwardDelete").Enabled && !Button(panel, "ForwardStart").Enabled, "populated list renders and no selection disables destructive actions");
                    list.Items[0].Selected = true; list.Items[1].Checked = true;
                    var targets = (List<PortForwardRule>)Call(panel, "Targets");
                    check(targets.Count == 1 && targets[0].Id == "second", "checked rows override unrelated selected row");
                    var task = (Task)Call(panel, "RunSelected", "start"); Pump(task);
                    check(resolved.SequenceEqual(new[] { "ssh-a" }) && workspace.Error("second").Length > 0, "batch start targets checked rule and displays offline reason");
                    check(list.Items[1].Checked && list.Items[0].Selected, "state refresh preserves checked and selected rows");
                    Capture(host, directory, "port-forward-offline");
                    var shell = (ListShell)typeof(PortForwardPanel).GetField("_shell", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                    shell.FilterText = "Real form submit"; Application.DoEvents();
                    check(((List<PortForwardRule>)Call(panel, "Targets")).All(r => r.Id != "second"), "filtered-out checked rule is not acted on invisibly");
                    shell.FilterText = ""; Application.DoEvents();
                    list.Items[1].Checked = false; list.Items[0].Selected = false;
                    check(((List<PortForwardRule>)Call(panel, "Targets")).Count == 0, "no selection never falls back to all rules");
                    list.Items[1].Selected = true;
                    // Exercise real editor entry; one activation must produce exactly one dialog.
                    int editors = 0;
                    using (var timer = new Timer { Interval = 100 })
                    {
                        timer.Tick += (s, e) =>
                        {
                            var form = Application.OpenForms.OfType<PortForwardRuleForm>().FirstOrDefault();
                            if (form == null) return;
                            editors++; ((AntdUI.Button)form.CancelButton).PerformClick();
                        };
                        timer.Start();
                        typeof(Control).GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, new object[] { EventArgs.Empty });
                        timer.Stop();
                    }
                    check(editors == 1, "double-click dispatch opens exactly one editor");
                    panel.Dispose();
                    var replacement = new PortForwardPanel(workspace); host.Controls.Add(replacement); Application.DoEvents();
                    check(replacement.Controls.Find("ForwardRules", true).OfType<ListView>().Single().Items.Count == 2, "closing and replacing panel preserves rules");
                    replacement.Dispose();
                    using (var locked = new FileStream(Path.Combine(temp, "port-forwards.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var changed = Rule("second"); changed.Name = "must not publish";
                        bool failed = false;
                        try { workspace.Save(changed); } catch (IOException) { failed = true; }
                        check(failed && workspace.Rules.Single(r => r.Id == "second").Name != "must not publish", "real disk sharing failure does not publish unsaved edit");
                        var deletion = workspace.DeleteAsync("second"); Pump(deletion);
                        check(!deletion.Result && workspace.Rules.Count == 2 && trash.Count == 0, "failed durable deletion keeps rule and creates no trash item");
                    }
                    Pump(workspace.DeleteAsync("second"));
                    string error; var entry = trash.List().Single();
                    using (var locked = new FileStream(Path.Combine(temp, "port-forwards.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                        check(!trash.TryRestore(entry.Id, out error) && trash.Count == 1 && workspace.Rules.Count == 1, "failed durable restore keeps recoverable trash entry");
                    check(trash.TryRestore(entry.Id, out error) && store.Load().Any(r => r.Id == "second"), "trash restores into persistent workspace after all panels were disposed");
                    using (var reopened = new PortForwardPanel(workspace))
                    {
                        host.Controls.Add(reopened); Application.DoEvents();
                        check(reopened.Controls.Find("ForwardRules", true).OfType<ListView>().Single().Items.Count == 2, "reopened panel renders restored rule");
                        Capture(host, directory, "port-forward-restored");
                    }
                }
                check(store.Load().Count == 2, "rules survive store reload");
            }
        }
        private static void Pump(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            if (!task.IsCompleted) throw new TimeoutException("forward UI operation timeout");
            task.GetAwaiter().GetResult(); Application.DoEvents();
        }
        private static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, args);
        private static AntdUI.Button Button(Control c, string n) => c.Controls.Find(n, true).OfType<AntdUI.Button>().Single();
        private static AntdUI.Input Input(Control c, string n) => c.Controls.Find(n, true).OfType<AntdUI.Input>().Single();
        private static AntdUI.Select Select(Control c, string n) => c.Controls.Find(n, true).OfType<AntdUI.Select>().Single();
        private static AntdUI.Label Label(Control c, string n) => c.Controls.Find(n, true).OfType<AntdUI.Label>().Single();
        private static void Capture(Form form, string directory, string name)
        {
            Application.DoEvents();
            File.WriteAllText(Path.Combine(directory, name + ".json"), UiTreeDumper.Dump(form), System.Text.Encoding.UTF8);
            UiSmokeRunner.Shot(form, Path.Combine(directory, name + ".png"));
        }
    }
}
