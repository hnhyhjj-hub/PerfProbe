namespace PerfProbe
{
    /// <summary>
    /// 每 tick 耗时累加 + P95 环形缓冲。环形缓冲定长预分配，写入不产生 GC。
    /// 窗口环与会话环分开：jsonl 里的 msPerTickP95 只反映本窗口，
    /// 不能拿会话累计的分布冒充窗口分布。
    /// </summary>
    public static class FrameProbe
    {
        private const int Capacity = 1024;

        private static readonly double[] _winRing = new double[Capacity];
        private static readonly double[] _winScratch = new double[Capacity];
        private static int _winCount;
        private static int _winHead;

        private static readonly double[] _sesRing = new double[Capacity];
        private static readonly double[] _sesScratch = new double[Capacity];
        private static int _sesCount;
        private static int _sesHead;

        private static int _windowTicks;
        private static double _windowTotalMs;
        private static double _windowGapMs;

        private static int _sessionTicks;
        private static double _sessionTotalMs;
        private static double _sessionGapMs;

        public static int WindowTicks => _windowTicks;

        public static double WindowTotalMs => _windowTotalMs;

        public static double WindowGapMs => _windowGapMs;

        /// <summary>全会话累计的未采集缺口。summary 必须用这个，窗口值每 5 秒被清零。</summary>
        public static double SessionGapMs => _sessionGapMs;

        public static int SessionTicks => _sessionTicks;

        public static double SessionTotalMs => _sessionTotalMs;

        public static void ResetWindow()
        {
            _windowTicks = 0;
            _windowTotalMs = 0;
            _windowGapMs = 0;
            _winCount = 0;
            _winHead = 0;
        }

        public static void ResetSession()
        {
            ResetWindow();
            _sessionTicks = 0;
            _sessionTotalMs = 0;
            _sessionGapMs = 0;
            _sesCount = 0;
            _sesHead = 0;
        }

        /// <summary>记一个 tick 的总耗时。captured=false 时只计入覆盖缺口。</summary>
        public static void AddTick(double ms, bool captured)
        {
            if (!captured)
            {
                _windowGapMs += ms;
                _sessionGapMs += ms;
                return;
            }
            _windowTicks++;
            _windowTotalMs += ms;
            _sessionTicks++;
            _sessionTotalMs += ms;

            Push(_winRing, ref _winHead, ref _winCount, ms);
            Push(_sesRing, ref _sesHead, ref _sesCount, ms);
        }

        private static void Push(double[] ring, ref int head, ref int count, double ms)
        {
            ring[head] = ms;
            head++;
            if (head >= Capacity)
            {
                head = 0;
            }
            if (count < Capacity)
            {
                count++;
            }
        }

        public static double WindowMsPerTick()
        {
            return _windowTicks <= 0 ? 0.0 : _windowTotalMs / _windowTicks;
        }

        public static double WindowTps(double windowSec)
        {
            return windowSec <= 0.0 ? 0.0 : _windowTicks / windowSec;
        }

        public static double SessionMsPerTick()
        {
            return _sessionTicks <= 0 ? 0.0 : _sessionTotalMs / _sessionTicks;
        }

        /// <summary>本窗口 tick 耗时的 P95（毫秒）。样本不足返回 0。</summary>
        public static double WindowP95()
        {
            return Percentile(_winRing, _winScratch, _winHead, _winCount, 0.95);
        }

        /// <summary>全会话 P95（毫秒）。</summary>
        public static double SessionP95()
        {
            return Percentile(_sesRing, _sesScratch, _sesHead, _sesCount, 0.95);
        }

        /// <summary>全会话中位数（毫秒），写进 summary 的会话头。</summary>
        public static double SessionMedian()
        {
            return Percentile(_sesRing, _sesScratch, _sesHead, _sesCount, 0.5);
        }

        /// <summary>
        /// 环形缓冲里最旧的样本在 head 处（未满时在 0 处）。
        /// 必须按逻辑顺序拷出，直接 Array.Copy 前 n 个在回绕后是错的。
        /// </summary>
        private static double Percentile(double[] ring, double[] scratch, int head, int count, double p)
        {
            int n = count;
            if (n <= 0)
            {
                return 0.0;
            }
            int start = n == Capacity ? head : 0;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= Capacity)
                {
                    idx -= Capacity;
                }
                scratch[i] = ring[idx];
            }
            System.Array.Sort(scratch, 0, n);
            int pick = (int)(p * (n - 1));
            if (pick < 0)
            {
                pick = 0;
            }
            if (pick >= n)
            {
                pick = n - 1;
            }
            return scratch[pick];
        }
    }
}
