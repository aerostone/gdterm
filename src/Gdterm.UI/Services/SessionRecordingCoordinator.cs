using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Terminal;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>Commands for recording and exporting the active terminal session.</summary>
    public sealed class SessionRecordingCoordinator
    {
        private readonly Form _owner;
        private readonly Func<TerminalControl> _getActiveTerminal;
        private readonly Func<TabContainerControl> _getTabs;
        private readonly BottomBarPanel _statusBar;
        private readonly Func<string> _getRecordingsDirectory;

        public SessionRecordingCoordinator(
            Form owner,
            Func<TerminalControl> getActiveTerminal,
            Func<TabContainerControl> getTabs,
            BottomBarPanel statusBar,
            Func<string> getRecordingsDirectory)
        {
            _owner = owner;
            _getActiveTerminal = getActiveTerminal;
            _getTabs = getTabs;
            _statusBar = statusBar;
            _getRecordingsDirectory = getRecordingsDirectory;
        }

        public bool IsRecordingActive
        {
            get
            {
                var terminal = _getActiveTerminal != null ? _getActiveTerminal() : null;
                return terminal != null && terminal.IsSessionRecording;
            }
        }

        public void ToggleRecording()
        {
            var terminal = _getActiveTerminal != null ? _getActiveTerminal() : null;
            if (terminal == null)
            {
                try { ToastNotifier.Info(L10n.T("当前没有活动的终端会话")); } catch (Exception) { }
                return;
            }

            try
            {
                if (terminal.IsSessionRecording)
                {
                    string path = terminal.SessionRecordingPath;
                    terminal.StopSessionRecording();
                    _statusBar?.UpdateConnectionStatus(L10n.T("录制已停止"));
                    try { ToastNotifier.Success(L10n.T("录制已保存：") + path); } catch (Exception) { }
                }
                else
                {
                    string path = terminal.StartSessionRecording(
                        _getRecordingsDirectory != null ? _getRecordingsDirectory() : null);
                    if (string.IsNullOrEmpty(path))
                    {
                        _statusBar?.UpdateConnectionStatus(L10n.T("录制启动失败"), BottomBarPanel.ConnectionStatusLevel.Failed);
                        try { ToastNotifier.Warning(L10n.T("录制启动失败，详见诊断日志")); } catch (Exception) { }
                        return;
                    }
                    _statusBar?.UpdateConnectionStatus(L10n.T("正在录制会话"));
                    try { ToastNotifier.Info(L10n.T("开始录制：") + Path.GetFileName(path)); } catch (Exception) { }
                }

                _getTabs?.Invoke()?.RefreshTabBadges();
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("SessionRecordingCoordinator.Toggle", ex);
            }
        }

        public void ShowRecordings()
        {
            try
            {
                using (var form = new SessionRecordingsForm(
                    _getRecordingsDirectory != null ? _getRecordingsDirectory() : null))
                    form.ShowDialog(_owner);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("SessionRecordingCoordinator.ShowRecordings", ex);
                try { ToastNotifier.Warning(L10n.T("打开会话录制中心失败：") + ex.Message); } catch (Exception) { }
            }
        }

        public void ExportActiveTerminalBuffer()
        {
            try
            {
                var terminal = _getActiveTerminal != null ? _getActiveTerminal() : null;
                if (terminal == null || !terminal.IsConnected)
                {
                    ToastNotifier.Info(L10n.T("当前没有活动的终端会话"));
                    return;
                }

                using (var dialog = new SaveFileDialog())
                {
                    dialog.Title = L10n.T("导出终端缓冲");
                    dialog.Filter = L10n.T("文本文件 (*.txt)|*.txt|HTML 文件 (*.html)|*.html|所有文件 (*.*)|*.txt");
                    dialog.FileName = "terminal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                    if (dialog.ShowDialog(_owner) != DialogResult.OK) return;

                    var lines = terminal.Session?.GetRecentOutput(2000);
                    if (lines == null) lines = new List<string>();
                    var host = terminal.Config?.Host ?? "localhost";
                    var content = Path.GetExtension(dialog.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase)
                        ? TerminalBufferExport.ExportAsHtml(new List<string>(lines), host)
                        : TerminalBufferExport.ExportAsText(new List<string>(lines), host);
                    TerminalBufferExport.SaveToFile(content, dialog.FileName);
                    ToastNotifier.Success(L10n.T("已导出缓冲"));
                }
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("SessionRecordingCoordinator.ExportBuffer", ex);
                MessageBox.Show(_owner, L10n.Tf("导出失败：{0}", ex.Message), L10n.T("导出缓冲"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
