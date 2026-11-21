using System;
using System.Collections.Generic;
using Gdterm.Terminal;

namespace Gdterm.Tests.Terminal
{
    public static class TerminalSearchEngineTests
    {
        public static void Run()
        {
            SearchesEverySuppliedHistoryLine();
            NavigationWrapsInBothDirections();
        }

        private static void SearchesEverySuppliedHistoryLine()
        {
            var lines = new List<string>();
            for (int i = 0; i < 700; i++) lines.Add("line-" + i);
            lines[620] = "needle far back";
            var engine = new TerminalSearchEngine(lines);

            Assert.Equal(1, engine.Search("needle"), "search includes lines beyond the former 500-line UI slice");
            Assert.Equal(620, engine.Results[0].LineIndex, "search result preserves the absolute history line");
        }

        private static void NavigationWrapsInBothDirections()
        {
            var engine = new TerminalSearchEngine(new List<string> { "hit 1", "none", "hit 2" });
            engine.Search("hit");
            Assert.Equal(0, engine.CurrentIndex, "search begins on the first result");
            Assert.Equal(2, engine.Next().LineIndex, "next moves to the second result line");
            Assert.Equal(0, engine.Next().LineIndex, "next wraps to the first result");
            Assert.Equal(2, engine.Previous().LineIndex, "previous wraps to the last result line");
        }
    }
}
