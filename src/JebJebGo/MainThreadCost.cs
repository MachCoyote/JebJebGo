using System.Diagnostics;

namespace JebJebGo
{
    /// <summary>
    /// Diagnostics: the slowest single main-thread run of each JebJebGo hook since
    /// the last reset, so a frame hitch can be attributed (or ruled out). The
    /// harness resets <see cref="MaxMs"/> per measurement; <see cref="EditorHitchLog"/>
    /// reads and resets its own per-frame copy, so the two never interfere.
    /// Hooks nest (a list refresh runs inside a search step), so the slowest run
    /// is the outermost one's total, not a sum.
    /// </summary>
    public static class MainThreadCost
    {
        public static double MaxMs { get; private set; }
        public static string MaxWhere { get; private set; }

        internal static double FrameMaxMs { get; private set; }
        internal static string FrameMaxWhere { get; private set; }

        public static void Reset()
        {
            MaxMs = 0;
            MaxWhere = null;
        }

        internal static void ResetFrame()
        {
            FrameMaxMs = 0;
            FrameMaxWhere = null;
        }

        internal static void Record(string where, Stopwatch clock)
        {
            double ms = clock.Elapsed.TotalMilliseconds;
            if (ms > MaxMs)
            {
                MaxMs = ms;
                MaxWhere = where;
            }
            if (ms > FrameMaxMs)
            {
                FrameMaxMs = ms;
                FrameMaxWhere = where;
            }
        }
    }
}
