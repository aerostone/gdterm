using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using Gdterm.Terminal.Models;

namespace Gdterm.Tests.Terminal
{
    public sealed class MultiSessionFake : ITerminalSession
    {
        public string ConnectionId { get; set; } = "fake";
        public string Hostname { get; set; } = "server";
        public string OsType => "Linux";
        public bool Connected = true;
        public bool IsConnected => Connected;
        public string Prompt = "user@server:~$ ";
        public object Client;
        public Action<string> OnSend;
        public List<string> Inputs = new List<string>();
        public int OutputHandlers;
        private EventHandler<TerminalOutputEventArgs> _output;
        public event EventHandler<TerminalOutputEventArgs> OutputReceived
        { add { _output += value; OutputHandlers++; } remove { _output -= value; OutputHandlers--; } }
        public event EventHandler Disconnected;
        public void Emit(string text) => _output?.Invoke(this, new TerminalOutputEventArgs { Text = text });
        public void Disconnect() { Connected = false; Disconnected?.Invoke(this, EventArgs.Empty); }
        public void Connect(ConnectionConfig c, CredentialPayload p, int rows, int cols) { }
        public void ConnectViaTunnel(ConnectionConfig c, CredentialPayload p, TunnelEndpoint t, int rows, int cols) { }
        public IList<string> GetRecentOutput(int n) => new[] { Prompt };
        public string GetSelection() => "";
        public void SendInput(string text) { OnSend?.Invoke(text); lock (Inputs) Inputs.Add(text); }
        public void SendBytes(byte[] b) { }
        public void Resize(int cols, int rows) { }
        public bool IsZmodemReceiving => false;
        public void StartZmodemReceive(string d) { throw new NotSupportedException(); }
        public object TryGetSshClient() => Client;
        public void Dispose() { }
    }

    public static class MultiSessionTests
    {
        public static void Run()
        {
            foreach (var prompt in new[] { "user@server:~$ ", "\x1b[32muser@server:~/dir$ \x1b[0m", "PS C:\\Users\\x>", "C:\\Users\\x>", "[root@host ~]#", "$ " })
                Assert.True(TerminalReadyDetector.IsReady(new[] { prompt }), "ready prompt: " + prompt);
            foreach (var prompt in new[] { "Password:", "[sudo] password for user:", ">>> ", "> ", ":", "mysql>", "[Y/n]", "-- INSERT --" })
                Assert.True(!TerminalReadyDetector.IsReady(new[] { prompt }), "busy prompt: " + prompt);
            Broadcast().GetAwaiter().GetResult();
            Recordings().GetAwaiter().GetResult();
        }

        private static async Task Broadcast()
        {
            using (var manager = new MultiChannelManager())
            {
                var a = new MultiSessionFake(); var b = new MultiSessionFake();
                manager.Register("a", a, "same"); manager.Register("b", b, "same");
                Assert.True(manager.Select("a").Success, "single target selectable");
                Assert.True(manager.IsBroadcastMode, "single target send enabled");
                var snapshot = manager.GetSelectedTargets();
                manager.Select("b");
                var sent = await manager.BroadcastCommandAsync("echo 中文\r\npwd\r\n", snapshot);
                Assert.Equal(1, sent.Count, "frozen targets exclude later selection");
                Assert.Equal("echo 中文\rpwd\r", a.Inputs.Single(), "newlines normalized and single submit");
                Assert.Equal(0, b.Inputs.Count, "unconfirmed target untouched");
                manager.Register("a", b, "replacement");
                sent = await manager.BroadcastCommandAsync("danger", snapshot);
                Assert.True(!sent.Single().Success && b.Inputs.Count == 0, "snapshot never follows reconnect");
                Assert.Equal(1, manager.SelectedCount, "replaced session selection cleared");
                manager.Unregister("b");
                Assert.True(!manager.IsBroadcastMode, "unregister clears broadcast state");
                manager.Register("a", a, "same"); manager.Select("a");
                a.Disconnect();
                sent = await manager.BroadcastCommandAsync("ls", manager.GetSelectedTargets());
                Assert.True(!sent.Single().Success && a.Inputs.Count == 1, "disconnected target rejected");
                a.Connected = true; a.OnSend = cmd => { throw new IOException("test write failed"); };
                sent = await manager.BroadcastCommandAsync("ls", manager.GetSelectedTargets());
                Assert.True(!sent.Single().Success, "send errors returned per target");
                a.OnSend = null;
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    sent = await manager.BroadcastCommandAsync("ls", manager.GetSelectedTargets(), cancel.Token);
                    Assert.True(!sent.Single().Success && a.Inputs.Count == 1, "cancelled send writes nothing");
                }
                using (var started = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    a.OnSend = cmd => { started.Set(); release.Wait(3000); };
                    var task = manager.BroadcastCommandAsync("slow", manager.GetSelectedTargets());
                    Assert.True(started.Wait(1000) && !task.IsCompleted, "slow send awaits actual completion asynchronously");
                    release.Set(); sent = await task;
                    Assert.True(sent.Single().Success, "slow send reports completion once");
                }
            }
        }

        private static async Task Recordings()
        {
            var path = Path.Combine(Path.GetTempPath(), "multi-record-" + Guid.NewGuid().ToString("N") + ".gdrec");
            try
            {
                using (var rec = new MultiChannelRecorder())
                {
                    rec.StartRecording(); rec.RegisterSession("a", "<script>\" \\ [中文]");
                    rec.RecordInput("a", "echo \"[]{}\\\" 中文\"\r");
                    await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => rec.RecordOutput("a", "output ] " + i))));
                    rec.StopRecording(); rec.SaveToFile(path);
                    var original = rec.ExportAsText();
                    rec.StartRecording(); rec.StopRecording(); rec.LoadFromFile(path);
                    Assert.Equal(21, rec.EntryCount, "concurrent output retained");
                    Assert.Equal(original, rec.ExportAsText(), "recording special chars names and duration roundtrip");
                    Assert.NotContains(rec.ExportAsHtml(), "<script>", "html escapes session name");
                    var entries = new List<ReplayEvent>();
                    await rec.ReplayAsync(1000, ev => entries.Add(ev));
                    Assert.Equal(21, entries.Count, "view playback emits all events");
                    Assert.True(entries.Last().Progress == 1, "playback reaches 100 percent");
                    File.WriteAllText(path, "{}");
                    bool rejected = false; try { rec.LoadFromFile(path); } catch { rejected = true; }
                    Assert.True(rejected && rec.EntryCount == 21, "bad load preserves recording");
                    using (var ct = new CancellationTokenSource())
                    {
                        ct.Cancel(); bool cancelled = false;
                        try { await rec.ReplayAsync(1, null, ct.Token); } catch (OperationCanceledException) { cancelled = true; }
                        Assert.True(cancelled, "playback cancellable");
                    }
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
