using System;
using Gdterm.Terminal;

namespace Gdterm.Tests.Terminal
{
    public static class TerminalPasteContentTests
    {
        public static void Run()
        {
            Assert.True(!TerminalPasteContent.RequiresPreview("ls -la"), "short single-line paste does not interrupt with a preview");
            Assert.True(TerminalPasteContent.RequiresPreview("echo first\necho second"), "multi-line paste requires review");
            Assert.True(TerminalPasteContent.RequiresPreview("a\tb"), "tabbed paste requires review so expansion is visible");
            Assert.True(TerminalPasteContent.RequiresPreview(new string('x', TerminalPasteContent.PreviewThreshold + 1)), "long single-line paste requires review");
            Assert.Equal("a    b", TerminalPasteContent.Prepare("a\tb", true, PasteNewLineMode.Preserve), "tab expands to four spaces when selected");
            Assert.Equal("a\rb", TerminalPasteContent.Prepare("a\nb", false, PasteNewLineMode.Cr), "line endings can be normalized to CR");
            Assert.Equal("a\r\nb", TerminalPasteContent.Prepare("a\nb", false, PasteNewLineMode.CrLf), "line endings can be normalized to CRLF");
            Assert.Equal("a\nb", TerminalPasteContent.Prepare("a\r\nb", false, PasteNewLineMode.Preserve), "preserve mode canonicalizes mixed endings to LF");
            Assert.Equal("ab", TerminalPasteContent.Prepare("a\0b", false, PasteNewLineMode.Preserve), "NUL bytes are removed before terminal send");
            Assert.Equal(3, TerminalPasteContent.SplitLines("a\nb\n").Length, "trailing newline is preserved as an empty final line");
        }
    }
}
