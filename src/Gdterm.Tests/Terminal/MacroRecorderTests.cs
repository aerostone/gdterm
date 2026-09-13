using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using Gdterm.Terminal.Models;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// MacroRecorder 单测：录制步数/Delay/Save-Load roundtrip/回放序列（Fake 会话，零连接）。
    /// </summary>
    public static class MacroRecorderTests
    {
        private sealed class FakeSession : ITerminalSession
        {
            public readonly List<string> Inputs = new List<string>();
            public string ConnectionId { get { return "fake"; } }
            public string Hostname { get { return "fake"; } }
            public string OsType { get { return "Linux"; } }
            public bool IsConnected { get { return true; } }
            public void Connect(ConnectionConfig c, CredentialPayload p, int rows, int cols) { }
            public void ConnectViaTunnel(ConnectionConfig c, CredentialPayload p, TunnelEndpoint t, int rows, int cols) { }
            public IList<string> GetRecentOutput(int n) { return new List<string>(); }
            public string GetSelection() { return ""; }
            public void SendInput(string text) { Inputs.Add(text); }
            public void SendBytes(byte[] data) { Inputs.Add(System.Text.Encoding.UTF8.GetString(data)); }
            public void Resize(int cols, int rows) { }
            public bool IsZmodemReceiving { get { return false; } }
            public void StartZmodemReceive(string d) { throw new NotSupportedException(); }
            public event EventHandler<TerminalOutputEventArgs> OutputReceived { add { } remove { } }
            public event EventHandler Disconnected { add { } remove { } }
            public object TryGetSshClient() { return null; }
            public void Dispose() { }
        }

        public static void Run()
        {
            RecordSteps();
            SaveLoadRoundtrip();
            ReplaySequence();
        }

        private static void RecordSteps()
        {
            var rec = new MacroRecorder();
            Assert.Equal(false, rec.IsRecording, "macro-not-rec");
            rec.StartRecording();
            Assert.Equal(true, rec.IsRecording, "macro-is-rec");
            rec.RecordInput("l");
            rec.RecordInput("s");
            Assert.Equal(2, rec.StepCount, "macro-steps");
            rec.StopRecording();
            Assert.Equal(false, rec.IsRecording, "macro-stopped");
            Assert.True(rec.Duration.TotalMilliseconds >= 0, "macro-dur");
        }

        private static void SaveLoadRoundtrip()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "gdterm_macrotest_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var rec = new MacroRecorder();
                rec.StartRecording();
                rec.RecordInput("ls -la\n");
                rec.RecordInput("\x1b[A");
                rec.StopRecording();
                rec.SaveToFile(tmp);
                var back = MacroRecorder.FromFile(tmp);
                Assert.Equal(2, back.StepCount, "macro-rt-steps");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static void ReplaySequence()
        {
            var rec = new MacroRecorder();
            rec.StartRecording();
            rec.RecordInput("echo hi");
            rec.RecordInput("\r");
            rec.StopRecording();
            var fake = new FakeSession();
            rec.ReplayAsync(fake, 1000.0, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(2, fake.Inputs.Count, "macro-replay-count");
            Assert.Equal("echo hi", fake.Inputs[0], "macro-replay-0");
            Assert.Equal("\r", fake.Inputs[1], "macro-replay-1");
        }
    }
}
