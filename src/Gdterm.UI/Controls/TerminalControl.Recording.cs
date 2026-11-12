using System;
using System.IO;
using System.Text;
using Gdterm.Core.Recording;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 会话录制（P1-2）的终端侧抓取。与 <see cref="TerminalAutoLogger"/>（人读的文本日志）不是同一件事：
    ///  - `.log`：按行加时间戳，给人和 grep 用，**丢失转义序列**；
    ///  - `.gtr`：原始字节流 + 毫秒偏移，能原样回放进终端引擎（asciinema 那种），给审计和复盘用。
    ///
    /// 录制开关是**显式**动作（终端菜单「开始/停止录制会话」）：默认偷偷录所有会话是做得到的，
    /// 但那是"未经同意的凭据之外的数据落盘"，与 DATA-SECURITY 的口径不符。
    /// </summary>
    public partial class TerminalControl
    {
        private SessionRecordingWriter _recordingWriter;
        private System.Windows.Forms.Timer _recordingFlushTimer;

        /// <summary>是否正在录制。</summary>
        public bool IsSessionRecording { get { return _recordingWriter != null; } }

        /// <summary>当前录制文件路径（未录制为 null）。</summary>
        public string SessionRecordingPath
        {
            get { return _recordingWriter != null ? _recordingWriter.Path : null; }
        }

        /// <summary>
        /// 开始录制。directory 通常为 data/recordings；返回录制文件路径，失败返回 null（不抛：
        /// 录制失败不该让终端不可用，但要留日志）。
        /// </summary>
        public string StartSessionRecording(string directory)
        {
            if (_recordingWriter != null) return _recordingWriter.Path;
            if (string.IsNullOrEmpty(directory)) return null;
            try
            {
                var header = new SessionRecordingHeader
                {
                    Columns = _cellRenderer != null ? _cellRenderer.Columns : (_renderer != null ? _renderer.Columns : 80),
                    Rows = _cellRenderer != null ? _cellRenderer.Rows : (_renderer != null ? _renderer.Rows : 24),
                    Host = _config != null ? _config.Host : null,
                    ConnectionName = _config != null ? _config.Name : null,
                    StartedUtc = DateTime.UtcNow
                };
                string safeName = SanitizeForFileName(
                    (_config != null && !string.IsNullOrEmpty(_config.Name) ? _config.Name : "session"));
                string safeHost = SanitizeForFileName(_config != null ? _config.Host : null);
                string file = Path.Combine(directory,
                    safeName + "_" + safeHost + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gtr");

                _recordingWriter = new SessionRecordingWriter(file, header);

                // 逐帧 Flush 太贵（SSH 输出是碎片的），1s 一次把内部缓冲落盘：
                // 崩溃最多丢最后 1 秒输出，换掉的是每条输出一次 fsync
                _recordingFlushTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _recordingFlushTimer.Tick += (s, e) =>
                {
                    try { _recordingWriter?.Flush(); } catch (Exception ex) { DiagLog.Swallowed("TerminalControl.RecordFlush", ex); }
                };
                _recordingFlushTimer.Start();

                DiagLog.Info("TerminalControl.Recording", "start " + file);
                return file;
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TerminalControl.StartSessionRecording", ex);
                StopSessionRecording();
                return null;
            }
        }

        /// <summary>停止录制并写尾部收尾行（含总时长/帧数）。</summary>
        public void StopSessionRecording()
        {
            var writer = _recordingWriter;
            _recordingWriter = null;
            if (_recordingFlushTimer != null)
            {
                try { _recordingFlushTimer.Stop(); _recordingFlushTimer.Dispose(); } catch (Exception) { }
                _recordingFlushTimer = null;
            }
            if (writer == null) return;
            try
            {
                writer.Dispose();
                DiagLog.Info("TerminalControl.Recording", "stop " + writer.Path + " frames=" + writer.Frames);
            }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.StopSessionRecording", ex); }
        }

        /// <summary>主机输出入录（在 OnTerminalOutput 的两个分支里调用，暂停的标签也要录）。</summary>
        private void RecordOutput(string text)
        {
            var writer = _recordingWriter;
            if (writer == null || string.IsNullOrEmpty(text)) return;
            try { writer.Append(SessionFrameKind.Output, Encoding.UTF8.GetBytes(text)); }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.RecordOutput", ex); }
        }

        /// <summary>本地输入入录（审计要能还原"谁敲了什么"）。</summary>
        private void RecordInput(string text)
        {
            var writer = _recordingWriter;
            if (writer == null || string.IsNullOrEmpty(text)) return;
            try { writer.Append(SessionFrameKind.Input, Encoding.UTF8.GetBytes(text)); }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.RecordInput", ex); }
        }

        /// <summary>
        /// 按键直通路径的输入入录：这里拿到的就是**发往主机的原始字节**，
        /// 必须先入录再发，且不能再过一遍 string（输入法逐键到达时可能是 UTF-8 半截字符，
        /// 解码成 string 再编码会把它变成 U+FFFD，录制下来的"敲了什么"就是假的）。
        /// </summary>
        private void RecordInputBytes(byte[] data)
        {
            var writer = _recordingWriter;
            if (writer == null || data == null || data.Length == 0) return;
            try { writer.Append(SessionFrameKind.Input, data, 0, data.Length); }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.RecordInputBytes", ex); }
        }

        /// <summary>文件名净化：主机名/连接名里可能有冒号、斜杠、中文标点。</summary>
        private static string SanitizeForFileName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "session";
            var sb = new StringBuilder(value.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in value)
            {
                bool bad = c == ' ' || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?'
                           || c == '"' || c == '<' || c == '>' || c == '|';
                if (!bad)
                {
                    for (int i = 0; i < invalid.Length; i++)
                        if (invalid[i] == c) { bad = true; break; }
                }
                sb.Append(bad ? '_' : c);
            }
            string s = sb.ToString().Trim('_');
            return s.Length == 0 ? "session" : s;
        }
    }
}
