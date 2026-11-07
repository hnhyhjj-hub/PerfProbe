using System;

namespace PerfProbe
{
    /// <summary>
    /// 帧阶段计时栈。与 DepthTracker 刻意分开，不复用同一套栈：
    ///
    /// DepthTracker 记录的是 tick 内部几十层嵌套（Thing → Pawn → 各 Tracker），
    /// 而这里的嵌套是另一棵树（Root.Update → UpdatePlay → MapUpdate → DrawMesh）。
    /// 两者共用一套栈会互相污染：f_tick 会把整帧跑的所有 tick 都记成自己的子段，
    /// 于是 f_tick 的 self 变成 0，而 DrawMesh 之类又会被 tick 内部的残留深度错位。
    ///
    /// 只用 DepthTracker 的 Qpc，不复用它的话题。
    /// 与 DepthTracker 一样按线程隔离：并行 mod 会让 worker 线程也走到这里。
    /// </summary>
    public static class FrameDepth
    {
        private const int MaxDepth = 32;

        [ThreadStatic] private static long[] _startTs;
        [ThreadStatic] private static double[] _childMs;
        [ThreadStatic] private static int _depth;

        /// <summary>主线程专用判定：帧阶段的累加只认主线程，worker 线程的时间不进帧账。</summary>
        private static int _mainThreadId;

        public static int Depth => _depth;

        /// <summary>
        /// 显式声明当前线程为主线程。由 Bootstrap 在静态构造里调用 ——
        /// 不能依赖「首次调用即主线程」的惰性判定：并行 mod 可能让 worker 线程
        /// 先走到某个被 patch 的绘制方法，那样主线程 ID 就被记错了，
        /// 之后所有主线程的帧阶段都会被丢弃。
        /// </summary>
        public static void MarkMainThread()
        {
            _mainThreadId = Environment.CurrentManagedThreadId;
        }

        public static bool IsMainThread
        {
            get
            {
                if (_mainThreadId == 0)
                {
                    return false;
                }
                return Environment.CurrentManagedThreadId == _mainThreadId;
            }
        }

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
            ProbeEngine.NoteProbeEvent();
            return true;
        }

        /// <summary>退出一层，返回该层 self 毫秒。</summary>
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

        public static void Reset()
        {
            _depth = 0;
        }
    }
}
