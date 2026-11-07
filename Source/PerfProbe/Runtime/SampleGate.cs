namespace PerfProbe
{
    /// <summary>
    /// 轮换抽样门。用 thingIDNumber 加相位对 K 取模决定是否计时，
    /// 相位每 PhaseTicks 刻轮换一次，保证长期覆盖全部对象。
    /// </summary>
    public static class SampleGate
    {
        private static int _phase;
        private static int _k = 8;
        private static int _phaseTicks = 32;
        private static int _ticksToRotate;

        public static int K => _k;

        public static int Phase => _phase;

        public static void Configure(int k, int phaseTicks)
        {
            _k = k < 1 ? 1 : (k > 32 ? 32 : k);
            _phaseTicks = phaseTicks < 8 ? 8 : (phaseTicks > 128 ? 128 : phaseTicks);
            if (_ticksToRotate <= 0)
            {
                _ticksToRotate = _phaseTicks;
            }
        }

        /// <summary>K == 1 时恒为 true，即全量模式。</summary>
        public static bool ShouldSample(int id)
        {
            if (_k <= 1)
            {
                return true;
            }
            int v = id + _phase;
            if (v < 0)
            {
                v = -v;
            }
            return (v % _k) == 0;
        }

        /// <summary>每 tick 调一次（在总耗时 patch 内），到点轮换相位。</summary>
        public static void Tick()
        {
            if (_k <= 1)
            {
                return;
            }
            _ticksToRotate--;
            if (_ticksToRotate > 0)
            {
                return;
            }
            _ticksToRotate = _phaseTicks;
            _phase++;
            if (_phase >= _k)
            {
                _phase = 0;
            }
        }

        public static void Reset()
        {
            _phase = 0;
            _ticksToRotate = _phaseTicks;
        }
    }
}
