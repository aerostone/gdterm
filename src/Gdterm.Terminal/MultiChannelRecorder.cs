using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;

namespace Gdterm.Terminal
{
    /// <summary>Thread-safe recording data. Playback only emits timeline events and never sends input.</summary>
    public class MultiChannelRecorder : IDisposable
    {
        private readonly object _sync = new object();
        private List<RecordingEntry> _entries = new List<RecordingEntry>();
        private Dictionary<string, string> _names = new Dictionary<string, string>();
        private Stopwatch _timer;
        private bool _recording;
        private int _characters;
        private TimeSpan _duration;
        public bool IsRecording { get { lock (_sync) return _recording; } }
        public int EntryCount { get { lock (_sync) return _entries.Count; } }
        public bool LimitReached { get; private set; }
        public TimeSpan Duration { get { lock (_sync) return _recording ? _timer.Elapsed : _duration; } }
        public void StartRecording()
        {
            lock (_sync)
            {
                _entries.Clear(); _names.Clear(); _characters = 0; LimitReached = false;
                _duration = TimeSpan.Zero; _timer = Stopwatch.StartNew(); _recording = true;
            }
        }
        public void RegisterSession(string id, string name) { lock (_sync) _names[id] = name ?? id; }
        public void RecordInput(string id, string text) { Record(id, text, EntryDirection.Input); }
        public void RecordOutput(string id, string text) { Record(id, text, EntryDirection.Output); }
        private void Record(string id, string text, EntryDirection direction)
        {
            lock (_sync)
            {
                if (!_recording || string.IsNullOrEmpty(text)) return;
                if (_entries.Count >= 50000 || text.Length > 16 * 1024 * 1024 - _characters)
                { LimitReached = true; StopRecording(); return; }
                string name; _names.TryGetValue(id, out name);
                _entries.Add(new RecordingEntry {
                    TimestampMs = _timer.ElapsedMilliseconds, SessionId = id,
                    SessionName = name ?? id, Direction = direction, Content = text
                });
                _characters += text.Length;
            }
        }
        public void StopRecording()
        {
            lock (_sync)
            {
                if (!_recording) return;
                _timer.Stop(); _duration = _timer.Elapsed; _recording = false;
            }
        }
        public async Task ReplayAsync(double speed = 1, Action<ReplayEvent> onEvent = null,
            CancellationToken ct = default(CancellationToken))
        {
            if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
            var entries = Snapshot().Entries;
            long last = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                long delay = (long)Math.Min(int.MaxValue, Math.Max(0, entry.TimestampMs - last) / speed);
                if (delay > 0) await Task.Delay((int)delay, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                last = entry.TimestampMs;
                onEvent?.Invoke(new ReplayEvent { Entry = entry, Progress = (i + 1.0) / entries.Count });
            }
        }
        public string ExportAsText()
        {
            var recording = Snapshot();
            var text = new StringBuilder();
            text.AppendLine(L10n.T("gdterm 多通道录制报告"));
            foreach (var entry in recording.Entries)
                text.AppendLine(string.Format("[{0:0.000}s] {1} {2}: {3}", entry.TimestampMs / 1000.0,
                    entry.SessionName, entry.Direction == EntryDirection.Input ? "←" : "→", entry.Content));
            return text.ToString();
        }
        public string ExportAsHtml()
        {
            var recording = Snapshot();
            var text = new StringBuilder("<!DOCTYPE html><html><head><meta charset='utf-8'></head><body><pre>");
            foreach (var entry in recording.Entries)
                text.AppendFormat("[{0:0.000}s] {1} {2}: {3}\n", entry.TimestampMs / 1000.0,
                    System.Net.WebUtility.HtmlEncode(entry.SessionName), entry.Direction == EntryDirection.Input ? "←" : "→",
                    System.Net.WebUtility.HtmlEncode(entry.Content));
            return text.Append("</pre></body></html>").ToString();
        }

        [DataContract]
        private sealed class Recording
        {
            [DataMember(Name = "version")] public int Version = 1;
            [DataMember(Name = "duration")] public long Duration;
            [DataMember(Name = "sessions")] public Dictionary<string, string> Names;
            [DataMember(Name = "entries")] public List<RecordingEntry> Entries;
        }
        private Recording Snapshot()
        {
            lock (_sync) return new Recording {
                Duration = (long)Duration.TotalMilliseconds, Names = new Dictionary<string, string>(_names),
                Entries = _entries.Select(e => new RecordingEntry {
                    TimestampMs = e.TimestampMs, SessionId = e.SessionId, SessionName = e.SessionName,
                    Direction = e.Direction, Content = e.Content
                }).ToList()
            };
        }
        private static DataContractJsonSerializer Serializer()
        { return new DataContractJsonSerializer(typeof(Recording), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }); }
        public void SaveToFile(string path)
        {
            var recording = Snapshot();
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temp)) Serializer().WriteObject(stream, recording);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void LoadFromFile(string path)
        {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException(L10n.T("录制文件过大"));
            Recording data;
            using (var stream = File.OpenRead(path)) data = (Recording)Serializer().ReadObject(stream);
            if (data == null || data.Version != 1 || data.Names == null || data.Entries == null ||
                data.Duration < 0 || data.Entries.Count > 50000)
                throw new InvalidDataException(L10n.T("录制文件无效"));
            int characters = 0;
            long last = 0;
            foreach (var entry in data.Entries)
            {
                if (entry == null || entry.SessionId == null || entry.Content == null || entry.TimestampMs < last ||
                    (entry.Direction != EntryDirection.Input && entry.Direction != EntryDirection.Output) ||
                    entry.Content.Length > 16 * 1024 * 1024 - characters)
                    throw new InvalidDataException(L10n.T("录制文件无效"));
                characters += entry.Content.Length; last = entry.TimestampMs;
                string name; data.Names.TryGetValue(entry.SessionId, out name); entry.SessionName = name ?? entry.SessionId;
            }
            lock (_sync)
            {
                if (_recording) throw new InvalidOperationException(L10n.T("请先停止录制"));
                _entries = data.Entries; _names = data.Names; _characters = characters;
                _duration = TimeSpan.FromMilliseconds(Math.Max(data.Duration, last)); LimitReached = false;
            }
        }
        public void Dispose() { StopRecording(); }
    }

    [DataContract]
    public class RecordingEntry
    {
        [DataMember(Name = "t")] public long TimestampMs { get; set; }
        [DataMember(Name = "s")] public string SessionId { get; set; }
        public string SessionName { get; set; }
        [DataMember(Name = "d")] public EntryDirection Direction { get; set; }
        [DataMember(Name = "c")] public string Content { get; set; }
    }
    public enum EntryDirection { Input = 0, Output = 1 }
    public class ReplayEvent
    {
        public RecordingEntry Entry { get; set; }
        public double Progress { get; set; }
    }
}
