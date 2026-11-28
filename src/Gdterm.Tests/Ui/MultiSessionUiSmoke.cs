using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.Tests.Terminal;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
namespace Gdterm.Tests.Ui
{
    internal static class MultiSessionUiSmoke
    {
        internal static BatchCommandPanel Batch(UiTempScope scope)
        {
            Console.WriteLine("[batch-trace] fixture construct");
            var panel = new BatchCommandPanel();
            Console.WriteLine("[batch-trace] fixture constructed");
            panel.SetSessions(new Dictionary<string, ITerminalSession> {
                ["server-a"] = new MultiSessionFake { Hostname = "Production / 数据库 A" },
                ["server-b"] = new MultiSessionFake { Hostname = "Production / 数据库 B" }
            });
            return panel;
        }
        internal static MultiChannelPanel Broadcast(UiTempScope scope)
        {
            var manager = scope.Track(new MultiChannelManager());
            manager.Register("server-a", new MultiSessionFake(), "Production / 数据库 A");
            manager.Register("server-b", new MultiSessionFake { Prompt = "Password:" }, "Production / 数据库 B");
            manager.Select("server-a");
            return new MultiChannelPanel(manager, scope.Track(new MultiChannelRecorder()));
        }
        internal static void Run(string temp, string output, Action<bool, string> check)
        {
            UiSmokeRunner.Progress("multi-session:interaction");
            using (var host = new Form { ClientSize = new Size(440, 760) })
            using (var manager = new MultiChannelManager())
            using (var recorder = new MultiChannelRecorder())
            using (var panel = new MultiChannelPanel(manager, recorder))
            {
                var a = new MultiSessionFake(); var b = new MultiSessionFake();
                manager.Register("a", a, "same"); manager.Register("b", b, "same");
                host.Controls.Add(panel); host.Show(); panel.RefreshList(); Application.DoEvents();
                var list = Find<ListView>(panel, "BroadcastSessions");
                list.Items[0].Checked = true; Application.DoEvents();
                check(manager.SelectedCount == 1 && Find<AntdUI.Button>(panel, "BroadcastSend").Enabled, "single broadcast target enables send");
                Find<AntdUI.Input>(panel, "BroadcastCommand").Text = "echo 中文";
                panel.ConfirmForTest = text => false;
                Pump(panel.SendAsync());
                check(a.Inputs.Count == 0 && Find<AntdUI.Input>(panel, "BroadcastCommand").Text == "echo 中文", "denied broadcast keeps input and sends nothing");
                panel.ConfirmForTest = text => true;
                Find<AntdUI.Button>(panel, "BroadcastRecord").PerformClick();
                check(a.OutputHandlers == 1 && b.OutputHandlers == 0, "recording subscribes only selected target");
                a.Emit("test output");
                Pump(panel.SendAsync());
                check(a.Inputs.Count == 1 && b.Inputs.Count == 0, "broadcast sends only chosen target");
                check(recorder.EntryCount == 2, "records actual target output and accepted input");
                Find<AntdUI.Button>(panel, "BroadcastRecord").PerformClick();
                check(a.OutputHandlers == 0 && !recorder.IsRecording, "stop recording unsubscribes output");
                var shell = UiSmokeRunner.AllControls(panel).OfType<ListShell>().Single();
                shell.FilterText = "b"; Application.DoEvents();
                Find<AntdUI.Button>(panel, "BroadcastDeselect").PerformClick();
                check(manager.SelectedCount == 1, "filtered clear preserves hidden selection");
                shell.FilterText = ""; panel.RefreshList();
                manager.Unregister("a"); panel.RefreshList(); Application.DoEvents();
                check(manager.SelectedCount == 0 && list.Items.Count == 1, "disconnect refresh drops selected target");
                Capture(host, output, "multi-channel-interaction");
            }
            using (var host = new Form { ClientSize = new Size(440, 760) })
            using (var panel = new BatchCommandPanel())
            {
                var a = new MultiSessionFake(); var b = new MultiSessionFake();
                var source = new Dictionary<string, ITerminalSession> { ["a"] = a };
                panel.SessionProvider = () => source;
                panel.SetSessions(source);
                host.Controls.Add(panel); host.Show(); Application.DoEvents();
                var list = Find<ListView>(panel, "BatchSessions");
                list.Items[0].Checked = true;
                source = new Dictionary<string, ITerminalSession> { ["a"] = a, ["b"] = b };
                panel.SetSessions(source); Application.DoEvents();
                check(list.Items.Count == 2 && list.Items.Cast<ListViewItem>().Single(r => r.Name == "a").Checked, "new session appears and original selection survives");
                Find<AntdUI.Input>(panel, "BatchCommand").Text = "echo test\npwd";
                panel.ConfirmForTest = text => true;
                var finish = new TaskCompletionSource<List<BatchCommandResult>>();
                int calls = 0;
                panel.ExecuteForTest = (targets, cmd, timeout, ct) => {
                    calls++;
                    check(targets.Count == 1 && ReferenceEquals(targets["a"], a), "batch freezes actual target");
                    ct.Register(() => finish.TrySetResult(new List<BatchCommandResult> {
                        new BatchCommandResult { SessionId = "a", HostName = "same", Status = BatchCommandStatus.Cancelled }
                    }));
                    return finish.Task;
                };
                var running = panel.ExecuteCommandAsync(); Application.DoEvents();
                Pump(panel.ExecuteCommandAsync());
                check(calls == 1 && Find<AntdUI.Button>(panel, "BatchCancel").Enabled, "duplicate submit blocked and cancel available");
                Find<AntdUI.Button>(panel, "BatchCancel").PerformClick(); Pump(running);
                check(Find<AntdUI.Button>(panel, "BatchExecute").Enabled && !Find<AntdUI.Button>(panel, "BatchCancel").Enabled, "cancel restores actions");
                check(Find<RichTextBox>(panel, "BatchResults").Text.Contains("same"), "per-host result visible");
                panel.SetSessions(new Dictionary<string, ITerminalSession> { ["a"] = new MultiSessionFake() });
                check(!list.Items[0].Checked, "replacement object cannot inherit check");
                Capture(host, output, "batch-command-interaction");
                panel.SetSessions(source); list.Items[0].Checked = true;
                panel.ExecuteForTest = (targets, cmd, timeout, ct) => Task.FromException<List<BatchCommandResult>>(new IOException("fixture failed"));
                Pump(panel.ExecuteCommandAsync());
                check(Find<AntdUI.Button>(panel, "BatchExecute").Enabled, "failed execution restores controls");
            }
        }
        private static T Find<T>(Control panel, string name) where T : Control => panel.Controls.Find(name, true).OfType<T>().Single();
        private static void Pump(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(6);
            while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
            if (!task.IsCompleted) throw new TimeoutException("multi-session UI operation");
            task.GetAwaiter().GetResult(); Application.DoEvents();
        }
        private static void Capture(Form host, string output, string name)
        {
            Application.DoEvents();
            File.WriteAllText(Path.Combine(output, name + ".json"), UiTreeDumper.Dump(host), System.Text.Encoding.UTF8);
            UiSmokeRunner.Shot(host, Path.Combine(output, name + ".png"));
        }
    }
}
