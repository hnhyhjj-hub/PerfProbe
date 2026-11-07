using Verse;

namespace PerfProbe
{
    /// <summary>速度档判定。只采 Fast 与 Superfast，其余档位记入覆盖缺口 gapMs。</summary>
    public static class SpeedBucket
    {
        public static SpeedKind Current()
        {
            TickManager tm;
            try
            {
                tm = Find.TickManager;
            }
            catch
            {
                return SpeedKind.Paused;
            }
            if (tm == null)
            {
                return SpeedKind.Paused;
            }
            switch (tm.CurTimeSpeed)
            {
                case TimeSpeed.Paused: return SpeedKind.Paused;
                case TimeSpeed.Normal: return SpeedKind.Normal;
                case TimeSpeed.Fast: return SpeedKind.Fast;
                case TimeSpeed.Superfast: return SpeedKind.Superfast;
                case TimeSpeed.Ultrafast: return SpeedKind.Ultrafast;
                default: return SpeedKind.Paused;
            }
        }

        /// <summary>当前档位是否应累计成本。</summary>
        public static bool ShouldCapture(ProbeSettings s)
        {
            if (s == null)
            {
                return false;
            }
            SpeedKind k = Current();
            if (k == SpeedKind.Fast)
            {
                return s.CaptureFast;
            }
            if (k == SpeedKind.Superfast)
            {
                return s.CaptureSuperfast;
            }
            if (k == SpeedKind.Ultrafast)
            {
                return s.CaptureUltrafast;
            }
            return false;
        }

        /// <summary>档位短名，落盘用。</summary>
        public static string Tag(SpeedKind k)
        {
            switch (k)
            {
                case SpeedKind.Fast: return "fast";
                case SpeedKind.Superfast: return "superfast";
                case SpeedKind.Ultrafast: return "ultrafast";
                case SpeedKind.Normal: return "normal";
                default: return "paused";
            }
        }
    }

    public enum SpeedKind
    {
        Paused = 0,
        Normal = 1,
        Fast = 2,
        Superfast = 3,
        Ultrafast = 4
    }
}
