using Verse;

namespace PerfProbe
{
    /// <summary>可调参数全部在此。默认关闭（G2：不开启时行为与未装模组一致）。</summary>
    public class ProbeSettings : ModSettings
    {
        public bool Enabled = false;
        public string OutputDir = "";
        public int WindowSec = 5;

        /// <summary>
        /// 抽样分母。默认 1（全量）。原因：各 cat 的抽样倍率本来就不一致 ——
        /// component 每 tick 全量计时，building / item / pawn 走 thingIDNumber % K 抽样，
        /// 跨类占比不可比；且小数量高成本的类（例如只有 2 个实例的建筑）
        /// 会在相位轮换覆盖不到时被整段漏掉，×K 外推补不回来。
        /// 只有在探针自身开销明显时才调大。
        /// </summary>
        public int SampleK = 1;
        public int PhaseTicks = 32;
        public int KeepSessions = 20;
        public bool CaptureFast = true;
        public bool CaptureSuperfast = true;
        public bool CaptureUltrafast = false;
        public bool AutoCleanup = true;

        // 会话内一次性开关：由设置面板按钮置位，主循环消费后自动清零，不写盘。
        public bool StartPassiveRequested;
        public bool StopRequested;

        public const int WindowSecMin = 1;
        public const int WindowSecMax = 30;
        public const int SampleKMin = 1;
        public const int SampleKMax = 32;
        public const int PhaseTicksMin = 8;
        public const int PhaseTicksMax = 128;
        public const int KeepSessionsMin = 1;
        public const int KeepSessionsMax = 200;

        public void ClampAll()
        {
            WindowSec = Clamp(WindowSec, WindowSecMin, WindowSecMax);
            SampleK = Clamp(SampleK, SampleKMin, SampleKMax);
            PhaseTicks = Clamp(PhaseTicks, PhaseTicksMin, PhaseTicksMax);
            KeepSessions = Clamp(KeepSessions, KeepSessionsMin, KeepSessionsMax);
            if (OutputDir == null)
            {
                OutputDir = "";
            }
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo)
            {
                return lo;
            }
            return v > hi ? hi : v;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref Enabled, "pp_enabled", false);
            Scribe_Values.Look(ref OutputDir, "pp_outputDir", "");
            Scribe_Values.Look(ref WindowSec, "pp_windowSec", 5);
            Scribe_Values.Look(ref SampleK, "pp_sampleK", 1);
            Scribe_Values.Look(ref PhaseTicks, "pp_phaseTicks", 32);
            Scribe_Values.Look(ref KeepSessions, "pp_keepSessions", 20);
            Scribe_Values.Look(ref CaptureFast, "pp_captureFast", true);
            Scribe_Values.Look(ref CaptureSuperfast, "pp_captureSuperfast", true);
            Scribe_Values.Look(ref CaptureUltrafast, "pp_captureUltrafast", false);
            Scribe_Values.Look(ref AutoCleanup, "pp_autoCleanup", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ClampAll();
            }
        }
    }
}
