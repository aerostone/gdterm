using System;

namespace Gdterm.Terminal
{
    public enum PasteNewLineMode
    {
        Preserve,
        Cr,
        Lf,
        CrLf
    }

    public static class TerminalPasteContent
    {
        public const int PreviewThreshold = 256;

        public static bool RequiresPreview(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length > PreviewThreshold || text.IndexOf('\t') >= 0) return true;
            return SplitLines(text).Length > 1;
        }

        public static string Prepare(string text, bool expandTabs, PasteNewLineMode newLineMode)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", string.Empty);
            if (expandTabs) normalized = normalized.Replace("\t", "    ");
            switch (newLineMode)
            {
                case PasteNewLineMode.Cr: return normalized.Replace("\n", "\r");
                case PasteNewLineMode.CrLf: return normalized.Replace("\n", "\r\n");
                case PasteNewLineMode.Lf:
                case PasteNewLineMode.Preserve:
                default: return normalized;
            }
        }

        public static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }

    public sealed class MouseWheelDeltaAccumulator
    {
        private long _remainder;

        public int Add(int delta)
        {
            _remainder += delta;
            int detents = (int)(_remainder / 120);
            _remainder %= 120;
            return detents;
        }

        public void Reset()
        {
            _remainder = 0;
        }
    }
}
