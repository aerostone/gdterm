using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Gdterm.Terminal.Rendering;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// 渲染性能基准（P2-8 基线）：大 cat 刷屏场景下每帧耗时 / 脏行命中 / 分配压力。
    /// 走真实路径：CellGdiRenderer（引擎+GDI 画布）画在真实宿主 Form 里，
    /// 帧由 DoEvents 消息泵驱动（与 UiSmokeRunner 同款；0.1.395/396 教训：
    /// parentless 窗口 + 同步 Update() 在 CI 会话硬 SEH 崩）。
    /// 三个场景：
    ///   A scroll1  —— 每帧 1 行（60fps 慢速滚动），必整屏重画（所有行位移）
    ///   B burst25  —— 每帧 25 行（16ms 合并突发），整屏重画
    ///   C inplace  —— 原地覆盖同一行（无滚动），只应重画 1-2 行（脏行路径生效的证据）
    /// 输出纯 ASCII（CI 控制台是 GBK，中文会变 ?）。
    /// </summary>
    public static class PerfBench
    {
        private const int Cols = 160;
        private const int Rows = 45;
        private const int History = 10000;   // 与 P1-5 默认回滚行数一致

        // ── 取证埋点（0.1.395/396/397 三连 SEH，C# catch 接不住、crash 文件都落不下来）──
        // SetUnhandledExceptionFilter 在 SEH 层回调（先于 CLR 终止）：纯 Win32 写证据——
        // perf-seh.txt（异常代码+地址+帧号，手工十六进制、零 CLR 分配）+ perf-crash.dmp
        // （dbghelp minidump：拉回后离线解析故障模块/线程栈），然后 ExitProcess。
        // 若回调自身崩（极端情况），损失 = 现状（无证据），不更坏。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr SehFilterHandler(IntPtr ep);
        [StructLayout(LayoutKind.Sequential)]
        private sealed class ExceptionPointers
        {
            public IntPtr ExceptionRecord;
            public IntPtr ContextRecord;
        }
        [DllImport("kernel32")]
        private static extern uint SetUnhandledExceptionFilter(SehFilterHandler handler);
        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern IntPtr CreateFileA(string name, uint desired, uint share,
            IntPtr sa, uint disp, uint attrs, IntPtr template);
        [DllImport("kernel32")]
        private static extern bool WriteFile(IntPtr h, byte[] buf, uint n, out uint written, IntPtr ov);
        [DllImport("kernel32")]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32")]
        private static extern uint GetProcessId(IntPtr process);
        [DllImport("dbghelp.dll")]
        private static extern bool MiniDumpWriteDump(IntPtr proc, uint pid, IntPtr file,
            uint type, IntPtr exp, IntPtr u, IntPtr c);
        [DllImport("kernel32")]
        private static extern void ExitProcess(uint code);

        private static int _frameNo;
        private static SehFilterHandler _sehKeepAlive; // 防委托被 GC（回调是原生指针）
        private static readonly byte[] HexChars = { (byte)'0',(byte)'1',(byte)'2',(byte)'3',(byte)'4',
            (byte)'5',(byte)'6',(byte)'7',(byte)'8',(byte)'9',(byte)'a',(byte)'b',(byte)'c',(byte)'d',
            (byte)'e',(byte)'f' };

        /// <summary>回调内只走 P/Invoke + 整型运算：x64 EXCEPTION_RECORD 偏移 0=code(4B) 16=address(8B)。</summary>
        private static IntPtr SehFilter(IntPtr ep)
        {
            try
            {
                var ptrs = (ExceptionPointers)Marshal.PtrToStructure(ep, typeof(ExceptionPointers));
                // x64 EXCEPTION_RECORD：[0..3]=ExceptionCode, [16..23]=ExceptionAddress
                int code = (int)Marshal.ReadInt32(ptrs.ExceptionRecord, 0);
                long addr = Marshal.ReadInt64(ptrs.ExceptionRecord, 16);
                var buf = new byte[128];
                int p = 0;
                string head = "gdterm perf SEH code=0x";
                for (int i = 0; i < head.Length; i++) buf[p++] = (byte)head[i];
                for (int i = 24; i >= 0; i -= 4) buf[p++] = HexChars[(code >> i) & 15];
                string mid = " addr=0x";
                for (int i = 0; i < mid.Length; i++) buf[p++] = (byte)mid[i];
                for (int i = 56; i >= 0; i -= 4) buf[p++] = HexChars[(int)((addr >> i) & 15)];
                string tail = " frame=";
                for (int i = 0; i < tail.Length; i++) buf[p++] = (byte)tail[i];
                // 帧号转 ASCII（无 CLR 数字格式化）
                int fr = _frameNo;
                char[] tmp = new char[12];
                int t = 0;
                if (fr == 0) tmp[t++] = '0';
                while (fr > 0) { tmp[t++] = (char)('0' + fr % 10); fr /= 10; }
                while (t > 0) buf[p++] = (byte)tmp[--t];
                buf[p++] = 0x0a;
                IntPtr h = CreateFileA("perf-seh.txt", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);
                if (h != IntPtr.Zero && h != new IntPtr(-1))
                {
                    uint w; WriteFile(h, buf, (uint)p, out w, IntPtr.Zero);
                    CloseHandle(h);
                }
                IntPtr dh = CreateFileA("perf-crash.dmp", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);
                if (dh != IntPtr.Zero && dh != new IntPtr(-1))
                {
                    uint pid = GetProcessId(GetCurrentProcess());
                    // 0x18 = Normal|UnloadedModules|ThreadContext；异常流由 pExceptionParam≠NULL 自动带上
                    MiniDumpWriteDump(GetCurrentProcess(), pid, dh, 0x18, ep, IntPtr.Zero, IntPtr.Zero);
                    CloseHandle(dh);
                }
            }
            catch { }
            ExitProcess(0xDEAD);   // 编译器不知其不返回——下面这行是死代码，仅为 CS0161
            return IntPtr.Zero;
        }

        private static void Heartbeat(string scenario)
        {
            int n = _frameNo;
            if (n % 20 == 0)
                try { File.WriteAllText("perf-progress.txt", scenario + " frame=" + n + " t=" + Environment.TickCount); } catch { }
            _frameNo = n + 1;
        }

        public static int Run(string outJson)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Console.WriteLine("perf-bench: skipped (GDI path is Windows-only; CI runs it)");
                return 0;
            }

            Console.WriteLine("=== gdterm perf bench (render pipeline) ===");
            GC.Collect(); GC.WaitForPendingFinalizers();
            try
            {
                return RunCore(outJson);
            }
            catch (Exception ex)
            {
                // 证据先于宣判：崩溃落盘（appveyor 步骤会把 crash 文件推成产物，
                // CI 控制台匿名读不到）
                var crash = new StringBuilder();
                crash.Append("perf-bench crash: ").Append(ex.GetType().Name).Append('\n');
                crash.Append(ex.Message).Append('\n').Append('\n');
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    crash.Append("inner: ").Append(inner.GetType().Name).Append(' ').Append(inner.Message).Append('\n');
                crash.Append(ex.StackTrace);
                Console.WriteLine(crash.ToString());
                try { File.WriteAllText("perf-bench-crash.txt", crash.ToString(), new UTF8Encoding(true)); } catch { }
                return 2;
            }
        }

        private static int RunCore(string outJson)
        {
            _sehKeepAlive = SehFilter;
            SetUnhandledExceptionFilter(_sehKeepAlive);   // 取证：SEH 级捕获（先于 CLR 终止）
            // 0.1.395/396 教训：parentless canvas + Show() + 裸 Update() 在 CI 上硬崩
            // （SEH，C# catch 接不住，crash 文件都落不下来）。改用冒烟已验证的模式：
            // canvas 放进真实 Form（可见），帧由 DoEvents 消息泵驱动（UiSmokeRunner 同款，
            // CI 上 39 个 dump 全部画出了内容），帧末 Update() 同步 flush。
            var r = new CellGdiRenderer(Rows, Cols, null, History);
            var canvas = (Control)r.GetControl();
            // 按 Consolas 12pt @96dpi 的实测格宽 9x18 给画布（ApplyFont 内部也会 RecomputeGrid）
            int cw = Cols * 9 + 24, ch = Rows * 18 + 24;
            canvas.Size = new Size(cw, ch);

            var host = new Form
            {
                Text = "gdterm-perf-bench",
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedSingle,
                ClientSize = new Size(cw + 8, ch + 8),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0)
            };
            canvas.Location = new Point(4, 4);
            host.Controls.Add(canvas);
            host.Show();
            Application.DoEvents();
            if (!canvas.IsHandleCreated || !canvas.Visible)
            {
                Console.WriteLine("perf-bench: FAILED - canvas not visible (no desktop session?)");
                try { host.Close(); } catch { }
                return 1;
            }

            // 预热：字体加载 / 刷子缓存 / JIT
            for (int i = 0; i < 80; i++) r.Write(MakeLine(i) + "\r\n");
            PumpFrame(r, canvas);
            GC.Collect();

            ScenarioResult a, b, c;
            a = RunScenario(r, canvas, "scroll1", 150, f => r.Write(MakeLine(1000 + f) + "\r\n"));
            b = RunScenario(r, canvas, "burst25", 60, f =>
            {
                for (int k = 0; k < 25; k++) r.Write(MakeLine(2000 + f * 25 + k) + "\r\n");
            });
            c = RunScenario(r, canvas, "inplace", 100, f =>
            {
                // 光标停在最后一行：\r 回车后覆盖写（不产生滚动）
                r.Write("\r" + MakeLine(3000 + f));
            });

            PrintTable(new[] { a, b, c });
            if (!string.IsNullOrEmpty(outJson))
            {
                try
                {
                    string dir = Path.GetDirectoryName(outJson);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outJson, BuildJson(a, b, c), new UTF8Encoding(true));
                    Console.WriteLine("perf-bench: json -> " + outJson);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("perf-bench: json write failed: " + ex.Message);
                }
            }
            try { host.Close(); } catch { }
            return 0;
        }

        /// <summary>一帧：显式快照 + 消息泵（DoEvents 处理 WM_PAINT，冒烟同款）+ 同步 flush。</summary>
        private static void PumpFrame(CellGdiRenderer r, Control canvas)
        {
            r.RenderFrameNow();          // 快照 + 差分 + Invalidate（不含同步绘制）
            Application.DoEvents();      // 泵：把挂起的 WM_PAINT 画掉（生产等价路径）
            try { canvas.Update(); } catch { }   // 兜底：泵没画到就同步 flush
        }

        private class ScenarioResult
        {
            public string Name;
            public int Frames;
            public int FullFrames;           // 整屏帧数（paintedRows >= Rows）
            public long MinMs, AvgMs, P50Ms, P95Ms, MaxMs;
            public long SnapAvgMs, SnapP95Ms;   // 快照+差分（引擎 GetPageSpans + 行快照）
            public double AvgRows;
            public double AvgAllocKB;
        }

        private static string MakeLine(int n)
        {
            // ~158 列宽：贴近真实日志（时间戳 + 路径 + 十六进制 + 空格填充）
            var sb = new StringBuilder(158);
            sb.Append("2026-11-07 14:33:").Append((n % 60).ToString("00", CultureInfo.InvariantCulture))
              .Append(" INFO worker-").Append(n % 8)
              .Append(" job=").Append(n.ToString("x6", CultureInfo.InvariantCulture))
              .Append(" path=/var/log/app/service-").Append(n % 12).Append(".log");
            while (sb.Length < 158) sb.Append(' ');
            return sb.ToString();
        }

        private static ScenarioResult RunScenario(CellGdiRenderer r, Control canvas, string name, int frames, Action<int> feed)
        {
            var res = new ScenarioResult { Name = name, Frames = frames };
            var ms = new long[frames];
            var snapMs = new long[frames];
            double rowsSum = 0;
            long memBefore = GC.GetTotalMemory(false);
            for (int f = 0; f < frames; f++)
            {
                feed(f);
                var sw = Stopwatch.StartNew();
                PumpFrame(r, canvas);
                sw.Stop();
                Heartbeat(name);
                // 亚毫秒帧记 1ms：保留分辨力又不归零（Stopwatch 本身亚微秒精度，这里是口径选择）
                ms[f] = Math.Max(1L, (long)sw.Elapsed.TotalMilliseconds);
                snapMs[f] = r.LastSnapshotMs;
                int painted = r.LastPaintedRowCount;
                rowsSum += painted;
                if (painted >= Rows) res.FullFrames++;
            }
            long memAfter = GC.GetTotalMemory(false);
            Array.Sort(ms);
            Array.Sort(snapMs);
            res.MinMs = ms[0];
            res.P50Ms = ms[frames / 2];
            res.P95Ms = ms[(int)(frames * 0.95)];
            res.MaxMs = ms[frames - 1];
            res.SnapP95Ms = snapMs[(int)(frames * 0.95)];
            long sum = 0, snapSum = 0;
            for (int i = 0; i < frames; i++) { sum += ms[i]; snapSum += snapMs[i]; }
            res.AvgMs = sum / frames;
            res.SnapAvgMs = snapSum / frames;
            res.AvgRows = rowsSum / frames;
            res.AvgAllocKB = (memAfter - memBefore) / 1024.0 / frames;
            return res;
        }

        private static void PrintTable(ScenarioResult[] ss)
        {
            var sb = new StringBuilder();
            sb.AppendLine("scenario  frames  full  min  avg   p50  p95   max  snapAvg snapP95 rows/f allocKB/f");
            foreach (var s in ss)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-9} {1,6} {2,5} {3,4} {4,5} {5,4} {6,5} {7,5} {8,7} {9,7} {10,6} {11,8}",
                    s.Name, s.Frames, s.FullFrames, s.MinMs, s.AvgMs, s.P50Ms, s.P95Ms, s.MaxMs,
                    s.SnapAvgMs, s.SnapP95Ms,
                    s.AvgRows.ToString("F1", CultureInfo.InvariantCulture),
                    s.AvgAllocKB.ToString("F1", CultureInfo.InvariantCulture)));
            }
            Console.WriteLine(sb.ToString());
            Console.WriteLine("note: frame = feed + snapshot(engine GetPageSpans+diff) + GDI paint.");
            Console.WriteLine("      scroll1/burst25 = full-paint frames (scroll moves every row);");
            Console.WriteLine("      inplace = dirty-row path (expect rows/f ~1-2).");
            Console.WriteLine("      budget: full-paint p95 < 16ms, inplace p95 < 4ms @ " + Rows + "x" + Cols);
        }

        private static string BuildJson(ScenarioResult a, ScenarioResult b, ScenarioResult c)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"machine\": \"").Append(Esc(Environment.MachineName)).Append("\",\n");
            sb.Append("  \"grid\": ").Append(Rows).Append("x").Append(Cols).Append(",\n  \"history\": ").Append(History).Append(",\n  \"scenarios\": [\n");
            ScenarioResult[] all = { a, b, c };
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                sb.Append("    { \"name\": \"").Append(s.Name)
                  .Append("\", \"frames\": ").Append(s.Frames)
                  .Append(", \"full_frames\": ").Append(s.FullFrames)
                  .Append(", \"min_ms\": ").Append(s.MinMs.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"avg_ms\": ").Append(s.AvgMs.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"p50_ms\": ").Append(s.P50Ms.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"p95_ms\": ").Append(s.P95Ms.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"max_ms\": ").Append(s.MaxMs.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"snapshot_avg_ms\": ").Append(s.SnapAvgMs.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"snapshot_p95_ms\": ").Append(s.SnapP95Ms.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"avg_rows_per_frame\": ").Append(s.AvgRows.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"avg_alloc_kb_per_frame\": ").Append(s.AvgAllocKB.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(" }");
                if (i < all.Length - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\');
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
