using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端宏录制器——录制键盘输入和延迟，可变速回放
    /// </summary>
    public class MacroRecorder : IDisposable
    {
        private readonly List<MacroStep> _steps = new List<MacroStep>();
        private Stopwatch _timer;
        private bool _isRecording;
        private long _lastTimestamp;

        /// <summary>录制状态</summary>
        public bool IsRecording { get { return _isRecording; } }

        /// <summary>录制的步骤数</summary>
        public int StepCount { get { return _steps.Count; } }

        /// <summary>录制的总时长</summary>
        public TimeSpan Duration { get; private set; }

        /// <summary>开始录制</summary>
        public void StartRecording()
        {
            _steps.Clear();
            _timer = Stopwatch.StartNew();
            _lastTimestamp = 0;
            _isRecording = true;
        }

        /// <summary>记录一个输入步骤</summary>
        public void RecordInput(string input)
        {
            if (!_isRecording) return;

            var now = _timer.ElapsedMilliseconds;
            var delay = now - _lastTimestamp;
            _lastTimestamp = now;

            _steps.Add(new MacroStep
            {
                DelayMs = delay,
                Input = input,
                StepType = MacroStepType.Input
            });
        }

        /// <summary>停止录制</summary>
        public void StopRecording()
        {
            if (!_isRecording) return;
            _timer.Stop();
            Duration = _timer.Elapsed;
            _isRecording = false;
        }

        /// <summary>回放到终端会话</summary>
        public async Task ReplayAsync(ITerminalSession session, double speed = 1.0, CancellationToken ct = default)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (_steps.Count == 0) return;

            foreach (var step in _steps)
            {
                ct.ThrowIfCancellationRequested();

                if (step.DelayMs > 0)
                {
                    var delay = (int)(step.DelayMs / speed);
                    if (delay > 0) await Task.Delay(delay, ct);
                }

                if (step.StepType == MacroStepType.Input)
                {
                    session.SendInput(step.Input);
                }
            }
        }

        /// <summary>保存宏到文件</summary>
        public void SaveToFile(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("{\"version\":1,\"duration\":").Append((long)Duration.TotalMilliseconds);
            sb.Append(",\"steps\":[");
            for (int i = 0; i < _steps.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var step = _steps[i];
                sb.Append("{\"delay\":").Append(step.DelayMs);
                sb.Append(",\"type\":").Append((int)step.StepType);
                sb.Append(",\"input\":\"").Append(EscapeJson(step.Input)).Append("\"}");
            }
            sb.Append("]}");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>从文件加载宏</summary>
        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException("宏文件不存在", filePath);

            _steps.Clear();
            var json = File.ReadAllText(filePath, Encoding.UTF8);

            // 简单解析 steps 数组
            var stepsIdx = json.IndexOf("\"steps\"");
            if (stepsIdx < 0) return;
            var arrStart = json.IndexOf('[', stepsIdx);
            var arrEnd = json.IndexOf(']', arrStart);
            if (arrStart < 0 || arrEnd < 0) return;

            var content = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            var objects = SplitJsonObjects(content);

            long totalMs = 0;
            foreach (var obj in objects)
            {
                var delay = ExtractLong(obj, "delay");
                var type = ExtractInt(obj, "type");
                var input = ExtractString(obj, "input") ?? "";

                _steps.Add(new MacroStep { DelayMs = delay, StepType = (MacroStepType)type, Input = input });
                totalMs += delay;
            }

            Duration = TimeSpan.FromMilliseconds(totalMs);
        }

        /// <summary>从文件加载宏（静态工厂）</summary>
        public static MacroRecorder FromFile(string filePath)
        {
            var recorder = new MacroRecorder();
            recorder.LoadFromFile(filePath);
            return recorder;
        }

        // ── JSON 辅助 ──
        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static List<string> SplitJsonObjects(string content)
        {
            var result = new List<string>();
            int depth = 0; int start = -1;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '{') { if (depth == 0) start = i; depth++; }
                else if (content[i] == '}') { depth--; if (depth == 0 && start >= 0) { result.Add(content.Substring(start, i - start + 1)); start = -1; } }
            }
            return result;
        }

        private static string ExtractString(string json, string key)
        {
            var p = "\"" + key + "\"";
            var idx = json.IndexOf(p); if (idx < 0) return null;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return null;
            var s = json.IndexOf('"', c + 1); if (s < 0) return null;
            var e = s + 1;
            while (e < json.Length) { if (json[e] == '\\') { e += 2; continue; } if (json[e] == '"') break; e++; }
            return json.Substring(s + 1, e - s - 1).Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");
        }

        private static long ExtractLong(string json, string key)
        {
            var p = "\"" + key + "\"";
            var idx = json.IndexOf(p); if (idx < 0) return 0;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return 0;
            var s = c + 1; while (s < json.Length && json[s] == ' ') s++;
            var e = s; while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            long v; long.TryParse(json.Substring(s, e - s), out v); return v;
        }

        private static int ExtractInt(string json, string key)
        {
            return (int)ExtractLong(json, key);
        }

        public void Dispose()
        {
            _timer?.Stop();
            _steps.Clear();
        }
    }

    /// <summary>宏步骤类型</summary>
    public enum MacroStepType
    {
        Input = 0,
        Delay = 1
    }

    /// <summary>宏步骤</summary>
    public class MacroStep
    {
        public long DelayMs { get; set; }
        public string Input { get; set; }
        public MacroStepType StepType { get; set; }
    }
}
