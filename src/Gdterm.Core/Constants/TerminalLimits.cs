namespace Gdterm.Core.Constants
{
    /// <summary>
    /// 终端回滚缓冲的硬边界。之前 2000 行硬顶写死在 VtTerminalEngine 两处，
    /// 排查长日志（cat 大文件 / dmesg / journalctl）时不够用；Xshell/MobaXterm 都是万行级。
    /// </summary>
    public static class TerminalLimits
    {
        /// <summary>
        /// 设置界面允许的最小回滚行数（人机口径）。引擎层不套这个下限——调用方显式要 30 行就得给 30 行，
        /// 只在低于一屏时抬到一屏（见 ClampScrollback）。
        /// </summary>
        public const int MinScrollback = 100;

        /// <summary>允许的最大回滚行数（约 100k 行 ≈ 200MB 峰值，够用且不会打爆 32 位进程）。</summary>
        public const int MaxScrollback = 100000;

        /// <summary>默认回滚行数（新配置/新标签用）。</summary>
        public const int DefaultScrollback = 10000;

        /// <summary>
        /// 把任意输入收敛到 [0, Max]，且不小于当前屏幕行数。
        /// 不套 MinScrollback：那是设置界面给人看的下限，引擎层强制抬高会让"我要 30 行"变成 100 行。
        /// </summary>
        public static int ClampScrollback(int value, int rows)
        {
            if (value < 0) value = 0;
            if (value > MaxScrollback) value = MaxScrollback;
            if (rows > 0 && value < rows) value = rows;
            return value;
        }
    }
}
