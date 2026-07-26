using System;
using System.Collections.Generic;

namespace Gdterm.Tests
{
    /// <summary>
    /// 零依赖断言——避免引入 NUnit/MSTest NuGet，Win7 绿色可编译。
    /// </summary>
    public static class Assert
    {
        public static int Failures { get; private set; }
        public static int Passes { get; private set; }
        public static readonly List<string> Messages = new List<string>();

        public static void Reset()
        {
            Failures = 0;
            Passes = 0;
            Messages.Clear();
        }

        public static void True(bool condition, string message)
        {
            if (condition)
            {
                Passes++;
            }
            else
            {
                Failures++;
                Messages.Add("FAIL: " + message);
                Console.WriteLine("FAIL: " + message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            bool ok = Equals(expected, actual);
            True(ok, message + " expected=[" + expected + "] actual=[" + actual + "]");
        }

        public static void Contains(string haystack, string needle, string message)
        {
            True(haystack != null && needle != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0,
                message + " needle=[" + needle + "] haystack=[" + haystack + "]");
        }

        public static void NotContains(string haystack, string needle, string message)
        {
            True(haystack == null || needle == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0,
                message + " should not contain [" + needle + "] in [" + haystack + "]");
        }
    }
}
