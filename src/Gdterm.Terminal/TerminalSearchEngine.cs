using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 搜索结果
    /// </summary>
    public class SearchResult
    {
        public int LineIndex { get; set; }
        public int StartIndex { get; set; }
        public int Length { get; set; }
        public string LineText { get; set; }
    }

    /// <summary>
    /// 终端缓冲区搜索引擎——正则搜索、高亮匹配、上下跳转
    /// </summary>
    public class TerminalSearchEngine
    {
        private readonly List<string> _buffer;
        private List<SearchResult> _results = new List<SearchResult>();
        private int _currentIndex = -1;

        public IReadOnlyList<SearchResult> Results => _results;
        public int CurrentIndex => _currentIndex;
        public int TotalMatches => _results.Count;
        public bool HasResults => _results.Count > 0;

        /// <summary>当前搜索匹配变更时触发</summary>
        public event Action<SearchResult> CurrentMatchChanged;

        public TerminalSearchEngine(List<string> buffer)
        {
            _buffer = buffer;
        }

        /// <summary>搜索缓冲区</summary>
        public int Search(string pattern, bool caseSensitive = false, bool useRegex = false, bool wholeWord = false)
        {
            _results.Clear();
            _currentIndex = -1;

            if (string.IsNullOrEmpty(pattern) || _buffer == null || _buffer.Count == 0)
                return 0;

            try
            {
                string searchPattern = pattern;
                if (!useRegex)
                {
                    searchPattern = Regex.Escape(pattern);
                    if (wholeWord) searchPattern = @"\b" + searchPattern + @"\b";
                }

                RegexOptions opts = RegexOptions.Compiled;
                if (!caseSensitive) opts |= RegexOptions.IgnoreCase;

                var regex = new Regex(searchPattern, opts);

                for (int i = 0; i < _buffer.Count; i++)
                {
                    var line = _buffer[i];
                    if (string.IsNullOrEmpty(line)) continue;

                    var matches = regex.Matches(line);
                    foreach (Match m in matches)
                    {
                        _results.Add(new SearchResult
                        {
                            LineIndex = i,
                            StartIndex = m.Index,
                            Length = m.Length,
                            LineText = line
                        });
                    }
                }

                if (_results.Count > 0)
                {
                    _currentIndex = 0;
                    CurrentMatchChanged?.Invoke(_results[0]);
                }

                return _results.Count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>跳到下一个匹配</summary>
        public SearchResult Next()
        {
            if (_results.Count == 0) return null;
            _currentIndex = (_currentIndex + 1) % _results.Count;
            var result = _results[_currentIndex];
            CurrentMatchChanged?.Invoke(result);
            return result;
        }

        /// <summary>跳到上一个匹配</summary>
        public SearchResult Previous()
        {
            if (_results.Count == 0) return null;
            _currentIndex = (_currentIndex - 1 + _results.Count) % _results.Count;
            var result = _results[_currentIndex];
            CurrentMatchChanged?.Invoke(result);
            return result;
        }

        /// <summary>跳到指定索引</summary>
        public SearchResult GoTo(int index)
        {
            if (index < 0 || index >= _results.Count) return null;
            _currentIndex = index;
            var result = _results[_currentIndex];
            CurrentMatchChanged?.Invoke(result);
            return result;
        }

        /// <summary>清除搜索</summary>
        public void Clear()
        {
            _results.Clear();
            _currentIndex = -1;
        }
    }
}
