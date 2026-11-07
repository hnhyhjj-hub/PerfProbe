using System.Diagnostics;

namespace PerfProbe
{
    /// <summary>高精度计时封装。只用 Stopwatch.GetTimestamp，禁用 DateTime.Now。</summary>
    public static class Qpc
    {
        private static readonly double MsPerTick;

        static Qpc()
        {
            MsPerTick = 1000.0 / Stopwatch.Frequency;
        }

        public static long Now()
        {
            return Stopwatch.GetTimestamp();
        }

        public static double ToMs(long deltaTicks)
        {
            return deltaTicks * MsPerTick;
        }
    }
}
