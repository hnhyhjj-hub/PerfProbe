using System;
using HarmonyLib;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 逐对象计时的最外层。Thing.DoTick 是所有 tick 中的 thing 的公共入口，
    /// Pawn 也走这里（Pawn.Tick 是 protected override，由 DoTick 内部调用），
    /// 因此统一在此开帧，Pawn 分支交给 PawnTickContext 做十四个方面分解。
    /// 未处于采集中的 tick 直接返回，热路径开销只有一次静态布尔判断。
    ///
    /// 未采样（抽样未命中）的 pawn 也要压一个 inactive 层：
    /// 否则它内部 tracker 的 tick 会被记到外层 pawn 的方面桶里，
    /// 破坏「body + interval + other == 本 pawn 的 DoTick 时间」这个不变量。
    ///
    /// 框架成本抽样：每 64 个对象挑 1 个，在 Prefix 与 Postfix **各自**的入口与出口取时间戳。
    /// 两段都要量 —— 只量中间的原方法会把游戏逻辑算成探针开销，只量其中一个补丁又会漏掉另一段。
    /// 抽样开销约 6 次 Stopwatch/tick，可忽略；结果乘 64 外推。
    ///
    /// 参数命名注意：Harmony 只认 __instance / __result / __state / __originalMethod / __runOriginal
    /// 这几个固定名，其它任何 __xxx 都会被当成「取原方法同名参数」。Thing.DoTick() 没有这些参数，
    /// 一旦多写一个 __sampled，Harmony 会抛
    /// 「No parameter at requested index __sampled」并让整个 PatchAll 中断 —— 所有 patch 一起失效。
    /// 因此这里把「是否被抽样」压进 __state 的位标志，不新增参数。
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
    internal static class Patch_Thing_DoTick
    {
        // __state 低两位是状态：0 = 未开帧；1 = 普通 thing（有深度层）；
        // 2 = pawn 已采样（有深度层 + 上下文层）；3 = pawn 未采样（只有上下文层，用于屏蔽内层记账）。
        private const int StateMask = 3;
        private const int StateNone = 0;
        private const int StateThing = 1;
        private const int StatePawn = 2;
        private const int StatePawnQuiet = 3;

        /// <summary>__state 第 3 位：本对象被框架成本抽样选中。</summary>
        private const int FlagSampled = 4;

        /// <summary>框架成本抽样掩码：&amp; 63 == 0 即 1/64。</summary>
        private const int FrameSampleMask = 63;

        private const int FrameSampleScale = 64;

        [HarmonyPrefix]
        private static bool Prefix(Thing __instance, out int __state)
        {
            __state = StateNone;
            if (!ProbeEngine.TickActive || __instance == null)
            {
                return true;
            }
            bool probe = (__instance.thingIDNumber & FrameSampleMask) == 0;
            long t0 = probe ? Qpc.Now() : 0L;
            int flag = probe ? FlagSampled : 0;
            try
            {
                bool sampled = SampleGate.ShouldSample(__instance.thingIDNumber);
                Pawn pawn = __instance as Pawn;
                if (pawn != null)
                {
                    bool entered = false;
                    if (sampled)
                    {
                        entered = DepthTracker.Enter();
                        if (!entered)
                        {
                            sampled = false;
                        }
                    }
                    if (!PawnTickContext.Begin(sampled, pawn))
                    {
                        // 嵌套超限：退掉刚进的深度层，本帧不记账。
                        if (entered)
                        {
                            DepthTracker.Exit(true);
                        }
                        NoteFrame(t0);
                        return true;
                    }
                    __state = (sampled ? StatePawn : StatePawnQuiet) | flag;
                    NoteFrame(t0);
                    return true;
                }

                if (!sampled)
                {
                    NoteFrame(t0);
                    return true;
                }
                if (!DepthTracker.Enter())
                {
                    NoteFrame(t0);
                    return true;
                }
                __state = StateThing | flag;
            }
            catch (Exception e)
            {
                PLog.ErrOnce("Thing.DoTick 前缀异常: " + e.Message, 0x9E3711);
                __state = StateNone;
            }
            NoteFrame(t0);
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(Thing __instance, int __state)
        {
            int st = __state & StateMask;
            // 后缀的成本必须从后缀自己的入口算起：整段 Prefix→Postfix 会把原方法（真实游戏逻辑）
            // 的耗时也算进框架开销，那样得到的数字没有意义。
            long t0 = (__state & FlagSampled) != 0 ? Qpc.Now() : 0L;
            if (st == StateNone)
            {
                NoteFrame(t0);
                return;
            }
            try
            {
                if (st == StatePawnQuiet)
                {
                    // 只弹上下文层，不记时间。
                    PawnTickContext.End(0.0);
                    NoteFrame(t0);
                    return;
                }
                double selfMs = DepthTracker.Exit(true);
                if (st == StatePawn)
                {
                    PawnTickContext.End(selfMs);
                }
                else if (__instance != null)
                {
                    ProbeEngine.AddThing(__instance, selfMs, selfMs);
                }
            }
            catch (Exception e)
            {
                PLog.ErrOnce("Thing.DoTick 后缀异常: " + e.Message, 0x9E3712);
            }
            NoteFrame(t0);
        }

        /// <summary>记一次抽样到的框架成本。t0 == 0 表示本次没抽到，直接返回。</summary>
        private static void NoteFrame(long t0)
        {
            if (t0 == 0L)
            {
                return;
            }
            try
            {
                ProbeEngine.NoteFrameCost(Qpc.ToMs(Qpc.Now() - t0), FrameSampleScale);
            }
            catch
            {
            }
        }
    }
}
