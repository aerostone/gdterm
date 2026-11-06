using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Gdterm.Terminal.Rendering.Vt;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// Ctrl+Click 打开链接/路径（Xshell、MobaXterm、Tabby 都有的基础动作，之前只有「选中文本 → 右键在线动作」）。
    /// URL 交给默认浏览器；本地已存在的路径交给资源管理器；
    /// 远端绝对路径（/var/log、~/app）交给 SFTP 标签跟着打开。
    /// </summary>
    public partial class TerminalControl
    {
        private static readonly char[] LinkDelimiters = new char[]
        {
            ' ', '\t', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>', '|', ',', ';',
            '\uFF0C', '\u3002', '\uFF1A', '\uFF1B', '\u3001', '\u300A', '\u300B', '\u201C', '\u201D'
        };

        /// <summary>Ctrl+左键命中时返回 true（调用方不再走拖选）。</summary>
        private bool TryOpenLinkAt(int pixelX, int pixelY)
        {
            if (_cellRenderer == null) return false;
            int col, row;
            if (!_cellRenderer.TryHitTest(pixelX, pixelY, out col, out row)) return false;
            string line;
            try { line = _cellRenderer.GetRowText(row); } catch { return false; }
            string token = TokenAtColumn(line, col);
            if (string.IsNullOrEmpty(token)) return false;

            Uri uri;
            if (Uri.TryCreate(token, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
                    || uri.Scheme == Uri.UriSchemeFtp || uri.Scheme == "file"))
            {
                OpenWithShell(token);
                return true;
            }
            if (token.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                OpenWithShell("http://" + token);
                return true;
            }
            if (LooksLikeLocalPath(token) && LocalPathExists(token))
            {
                OpenWithShell(token);
                return true;
            }
            if (token.Length > 1 && (token[0] == '/' || token.StartsWith("~/", StringComparison.Ordinal)))
            {
                var h = RemotePathOpenRequested;
                if (h != null) { try { h(this, token); return true; } catch { } }
            }
            return false;
        }

        /// <summary>光标停在可打开目标上时给手型（仅在按住 Ctrl 时判断，避免每次移动都扫行）。</summary>
        private void UpdateLinkCursor(int pixelX, int pixelY)
        {
            var canvas = _cellRenderer != null ? _cellRenderer.GetControl() : null;
            if (canvas == null) return;
            bool hand = false;
            if ((ModifierKeys & Keys.Control) != 0 && _cellRenderer != null)
            {
                int col, row;
                if (_cellRenderer.TryHitTest(pixelX, pixelY, out col, out row))
                {
                    try
                    {
                        string token = TokenAtColumn(_cellRenderer.GetRowText(row), col);
                        hand = !string.IsNullOrEmpty(token) && IsOpenable(token);
                    }
                    catch (Exception ex) { DiagLog.Swallowed("TerminalControl.LinkCursor", ex); }
                }
            }
            var want = hand ? Cursors.Hand : Cursors.IBeam;
            try { if (canvas.Cursor != want) canvas.Cursor = want; } catch { }
        }

        private static bool IsOpenable(string token)
        {
            Uri uri;
            if (Uri.TryCreate(token, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
                    || uri.Scheme == Uri.UriSchemeFtp || uri.Scheme == "file")) return true;
            if (token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
            if (LooksLikeLocalPath(token) && LocalPathExists(token)) return true;
            return token.Length > 1 && (token[0] == '/' || token.StartsWith("~/", StringComparison.Ordinal));
        }

        /// <summary>按显示列取出点击处的 token（宽字符占位列已由 GetRowText 去掉）。</summary>
        private static string TokenAtColumn(string rowText, int column)
        {
            if (string.IsNullOrEmpty(rowText) || column < 0) return null;
            int idx = -1;
            int c = 0;
            for (int i = 0; i < rowText.Length; i++)
            {
                int w = WideText.Columns(rowText[i].ToString());
                if (c <= column && column < c + w) { idx = i; break; }
                c += w;
            }
            if (idx < 0) return null;
            int from = idx;
            int to = idx;
            while (from > 0 && Array.IndexOf(LinkDelimiters, rowText[from - 1]) < 0) from--;
            while (to < rowText.Length - 1 && Array.IndexOf(LinkDelimiters, rowText[to + 1]) < 0) to++;
            string token = rowText.Substring(from, to - from + 1).Trim();
            token = token.TrimEnd('.', ',', ':', ';', ')', ']', '}', '。', '，', '”');
            return token.Length == 0 ? null : token;
        }

        private static bool LooksLikeLocalPath(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (token.StartsWith(@"\\", StringComparison.Ordinal)) return true;                     // UNC
            return token.Length > 2 && char.IsLetter(token[0]) && token[1] == ':' && (token[2] == '\\' || token[2] == '/');
        }

        private static bool LocalPathExists(string token)
        {
            try { return File.Exists(token) || Directory.Exists(token); } catch { return false; }
        }

        private void OpenWithShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TerminalControl.OpenWithShell", ex);
                try { NotificationCenterPanel.Push("warn", "无法打开：" + target); } catch { }
            }
        }
    }
}
