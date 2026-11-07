using System;

namespace PerfProbe
{
    /// <summary>
    /// 每线程的嵌套计时栈。Enter/Exit 成对出现，
    /// Exit 返回的是 self 时间（总时间减去已记录的子树时间），
    /// 并把总时间累加给父层，因此各层 self 相加不会重复计数。
    /// 栈与缓冲在首次使用时分配一次，之后零分配。
    /// </summary>
    public static class DepthTracker
    {
        private const int MaxDepth = 64;

        [ThreadStatic] private static long[] _startTs;
        [ThreadStatic] private static double[] _childMs;
        [ThreadStatic] private static int _depth;

        public static int Depth => _depth;

        public static bool IsOutermost()
        {
            return _depth == 0;
        }

        /// <summary>进入一层。栈满时返回 false，调用方据此跳过对应的 Exit。</summary>
        public static bool Enter()
        {
            if (_startTs == null)
            {
                _startTs = new long[MaxDepth];
                _childMs = new double[MaxDepth];
            }
            int d = _depth;
            if (d >= MaxDepth)
            {
                return false;
            }
            _startTs[d] = Qpc.Now();
            _childMs[d] = 0.0;
            _depth = d + 1;
            // 每个计时点记一次事件（一次 int 自增，纳秒级），
            // 会话开始时标定过的单次成本乘以事件数即为探针自身开销。
            ProbeEngine.NoteProbeEvent();
            return true;
        }

        /// <summary>退出一层，返回该层 self 毫秒。未配对调用返回 0。</summary>
        public static double Exit()
        {
            return Exit(true);
        }

        /// <summary>
        /// 退出一层。entered 必须是 Enter() 的返回值：Enter 失败（栈满）时传 false，
        /// 此时不弹栈、返回 0，避免把父层的时间戳错位。
        /// </summary>
        public static double Exit(bool entered)
        {
            if (!entered)
            {
                return 0.0;
            }
            int d = _depth;
            if (d <= 0)
            {
                return 0.0;
            }
            d--;
            _depth = d;

            double totalMs = Qpc.ToMs(Qpc.Now() - _startTs[d]);
            double child = _childMs[d];
            double self = totalMs - child;
            if (self < 0.0)
            {
                self = 0.0;
            }
            if (d > 0)
            {
                _childMs[d - 1] += totalMs;
            }
            return self;
        }

        /// <summary>退出一层并同时取回总耗时（含子树），给需要 inclusive 的调用方。</summary>
        public static double ExitWithTotal(out double selfMs)
        {
            return ExitWithTotal(true, out selfMs);
        }

        /// <summary>带配对标记的版本，语义同 Exit(bool)。</summary>
        public static double ExitWithTotal(bool entered, out double selfMs)
        {
            if (!entered)
            {
                selfMs = 0.0;
                return 0.0;
            }
            int d = _depth;
            if (d <= 0)
            {
                selfMs = 0.0;
                return 0.0;
            }
            d--;
            _depth = d;

            double totalMs = Qpc.ToMs(Qpc.Now() - _startTs[d]);
            double child = _childMs[d];
            double self = totalMs - child;
            if (self < 0.0)
            {
                self = 0.0;
            }
            if (d > 0)
            {
                _childMs[d - 1] += totalMs;
            }
            selfMs = self;
            return totalMs;
        }

        public static void Reset()
        {
            _depth = 0;
        }
    }
}
