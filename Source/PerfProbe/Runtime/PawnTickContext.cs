using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 当前 pawn 的计时上下文，用栈支持嵌套（被搬运 / 装载的 pawn 在另一个 pawn 的 DoTick 内）。
    /// 以 Thing.DoTick 为起止边界（Pawn 走这个分支），内部各子方面把自己的时间投进对应桶，
    /// DoTick 结束时一次性落账。
    ///
    /// 两种量纲，不能混算：
    ///   外壳层（pawn_body / pawn_interval）是 DoTick 的直接子帧，DepthTracker 已把它们从
    ///   DoTick 的 self 时间里扣走，所以它们的时间不在 selfMs 内。
    ///   叶子层（think/work/... 与 gene/ability/...）是 pawn_body 内部的细分，
    ///   一旦 body 被捕获，它们的时间同样不在 selfMs 内（被 body 那层一起扣走了）。
    ///
    /// 由此得到落账规则：
    ///   外壳层被捕获 → residual = selfMs，不再扣叶子（叶子已在 body 里，再扣会算成负数）；
    ///   外壳层没捕获（Pawn.Tick / TickInterval 挂载失败）→ 退回旧口径，residual = selfMs - 叶子之和。
    /// 这样「body + interval + other」恒等于该 pawn 的 DoTick 总时间，
    /// 叶子层只是 body 内部的细分说明，不能与外壳层相加。
    ///
    /// 单线程（游戏 tick 在主线程），不需要同步。
    /// </summary>
    public static class PawnTickContext
    {
        private const int AspectCount = PawnCost.AspectCount;
        private const int MaxNesting = 8;

        private static readonly bool[] _active = new bool[MaxNesting];
        private static readonly double[,] _aspectMs = new double[MaxNesting, AspectCount];
        private static readonly Pawn[] _pawn = new Pawn[MaxNesting];
        private static int _depth = -1;

        public static bool Active => _depth >= 0 && _active[_depth];

        public static int Nesting => _depth + 1;

        /// <summary>压入一层上下文。嵌套超限时返回 false，调用方不应再调用 End。</summary>
        public static bool Begin(bool sampled, Pawn pawn)
        {
            int d = _depth + 1;
            if (d >= MaxNesting)
            {
                return false;
            }
            _depth = d;
            _active[d] = sampled;
            _pawn[d] = pawn;
            for (int i = 0; i < AspectCount; i++)
            {
                _aspectMs[d, i] = 0.0;
            }
            return true;
        }

        /// <summary>子方面 patch 在 Exit 后把自己的 self 时间投到这里。未采样时静默丢弃。</summary>
        public static void AddAspect(int aspectId, double selfMs)
        {
            if (_depth < 0 || !_active[_depth] || aspectId < 0 || aspectId >= AspectCount || selfMs <= 0.0)
            {
                return;
            }
            _aspectMs[_depth, aspectId] += selfMs;
        }

        /// <summary>
        /// Pawn 的 DoTick 结束时调用。selfMs 是该 pawn 的 DoTick self 时间
        /// —— DepthTracker 已经把这棵子树里所有被捕获子帧的 total 扣掉了。
        ///
        /// 落账规则见类注释：外壳层被捕获时 residual 就是 selfMs 全部；
        /// 外壳层没捕获时才退回「selfMs 减叶子之和」的旧口径，避免双重扣减。
        /// </summary>
        public static void End(double selfMs)
        {
            int d = _depth;
            if (d < 0)
            {
                return;
            }
            bool sampled = _active[d];
            Pawn pawn = _pawn[d];
            _pawn[d] = null;
            _depth = d - 1;
            if (!sampled)
            {
                return;
            }

            double leafSum = 0.0;
            double shellSum = 0.0;
            for (int i = 0; i < AspectCount; i++)
            {
                double ms = _aspectMs[d, i];
                _aspectMs[d, i] = 0.0;
                if (i == PawnCost.IdOther || ms <= 0.0)
                {
                    continue;
                }
                if (PawnCost.IsShell(i))
                {
                    shellSum += ms;
                }
                else
                {
                    leafSum += ms;
                }
                ProbeEngine.AddPawnAspect(i, PawnCost.NameOf(i), ms, ms);
            }

            // 外壳层被捕获时，selfMs 里已经不含任何 pawn 内部时间，全部是 DoTick 自身开销；
            // 只有外壳层缺失（挂载失败）才需要从 selfMs 里减掉叶子，退回旧口径。
            double residual = shellSum > 0.0 ? selfMs : selfMs - leafSum;
            if (residual > 0.0)
            {
                ProbeEngine.AddPawnAspect(PawnCost.IdOther, PawnCost.Other, residual, residual);
            }

            // 个体 / 种族行：与上面十四个方面桶互不重叠，分母也不同（aspect 行是全部 pawn，
            // 这一行是该个体或该类动物的总耗时），所以另开一个 cat，不参与方面合计。
            //
            // 必须用 DoTick 的总时间 = selfMs + 外壳层。不能加 leafSum ——
            // 叶子层是 body 内部的细分，它的时间已经在 body 里了，加进来会翻倍。
            if (pawn != null && selfMs > 0.0)
            {
                PawnIdentity ident = PawnIdentityTable.Of(pawn);
                if (ident.Name != null)
                {
                    double pawnTotal = selfMs + shellSum;
                    ProbeEngine.AddPawnInstance(ident.Key, ident.Name, ident.Mod, ident.Asm,
                                                ident.Conf, pawnTotal, pawnTotal);
                }
            }
        }

        /// <summary>丢弃当前层而不落账（patch 前缀开帧成功但后缀判定不记账时用）。</summary>
        public static void Pop()
        {
            if (_depth >= 0)
            {
                _depth--;
            }
        }

        public static void Reset()
        {
            _depth = -1;
            for (int d = 0; d < MaxNesting; d++)
            {
                _active[d] = false;
                _pawn[d] = null;
                for (int i = 0; i < AspectCount; i++)
                {
                    _aspectMs[d, i] = 0.0;
                }
            }
        }
    }
}
