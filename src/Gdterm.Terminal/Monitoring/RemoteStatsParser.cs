using System;
using System.Globalization;

namespace Gdterm.Terminal.Monitoring
{
    /// <summary>
    /// /proc 采样文本解析（纯函数，便于单测）。全部按 KiB / 字节原始值返回，换算留给调用方。
    /// 远端可能是 busybox/coreutils，输出格式差异都由这里兜住：解析失败返回 false 而不是抛。
    /// </summary>
    public static class RemoteStatsParser
    {
        /// <summary>/proc/stat 首行：cpu  user nice system idle iowait irq softirq steal ...</summary>
        public static bool ParseCpuFirstLine(string line, out ulong totalJiffies, out ulong idleJiffies)
        {
            totalJiffies = 0;
            idleJiffies = 0;
            if (string.IsNullOrEmpty(line)) return false;
            var t = line.Trim();
            if (!t.StartsWith("cpu", StringComparison.Ordinal)) return false;
            var parts = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return false;
            ulong total = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                ulong v;
                if (!ulong.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) break;
                total += v;
            }
            ulong idle;
            if (!ulong.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out idle)) return false;
            // iowait 也算空闲（top 的口径），有就加上
            ulong iowait;
            if (parts.Length > 5 && ulong.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out iowait))
                idle += iowait;
            totalJiffies = total;
            idleJiffies = idle;
            return total > 0;
        }

        /// <summary>/proc/meminfo 里 MemTotal/MemAvailable（老内核没有 MemAvailable 时退回 MemFree）。</summary>
        public static bool ParseMemInfo(string text, out long totalKb, out long availableKb)
        {
            totalKb = 0;
            availableKb = 0;
            long memFree = -1;
            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) totalKb = ReadKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) availableKb = ReadKb(line);
                else if (line.StartsWith("MemFree:", StringComparison.Ordinal)) memFree = ReadKb(line);
            }
            if (availableKb <= 0 && memFree > 0) availableKb = memFree;
            return totalKb > 0;
        }

        /// <summary>/proc/meminfo 里 SwapTotal/SwapFree。</summary>
        public static bool ParseSwapInfo(string text, out long totalKb, out long freeKb)
        {
            totalKb = 0;
            freeKb = 0;
            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                if (line.StartsWith("SwapTotal:", StringComparison.Ordinal)) totalKb = ReadKb(line);
                else if (line.StartsWith("SwapFree:", StringComparison.Ordinal)) freeKb = ReadKb(line);
            }
            return totalKb > 0;
        }

        /// <summary>/proc/loadavg 首列。</summary>
        public static double ParseLoad1(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var parts = text.Trim().Split(' ');
            double v;
            if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        /// <summary>/proc/net/dev：跳过 lo，累计所有物理接口的收发字节。</summary>
        public static bool ParseNetDev(string text, out long rxBytes, out long txBytes)
        {
            rxBytes = 0;
            txBytes = 0;
            bool any = false;
            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                if (name == "lo" || name.Length == 0) continue;
                var cols = line.Substring(colon + 1).Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 9) continue;
                long rx, tx;
                if (!long.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out rx)) continue;
                if (!long.TryParse(cols[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out tx)) continue;
                rxBytes += rx;
                txBytes += tx;
                any = true;
            }
            return any;
        }

        /// <summary>df -kP 的数值行（第 2 列 total、第 3 列 used，单位 KiB）。</summary>
        public static bool ParseDf(string text, out long totalKb, out long usedKb)
        {
            totalKb = 0;
            usedKb = 0;
            bool first = true;
            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (first) { first = false; continue; }   // 表头
                var cols = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 3) continue;
                long t, u;
                if (long.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out t)
                    && long.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out u))
                {
                    totalKb = t;
                    usedKb = u;
                    return true;
                }
            }
            return false;
        }

        private static long ReadKb(string line)
        {
            int idx = line.IndexOf(':');
            if (idx < 0) return 0;
            var rest = line.Substring(idx + 1).Trim();
            var cols = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length == 0) return 0;
            long v;
            return long.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            return text.Replace("\r", "").Split('\n');
        }
    }
}
