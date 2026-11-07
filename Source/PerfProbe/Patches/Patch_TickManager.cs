using System;
using HarmonyLib;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 采集的总入口。DoSingleTick 是整个 tick 流水线的最外层，
    /// 这里计时得到的 ms 就是「一 tick 的游戏内耗时」。
    /// postfix 与 finalizer 都调 FinishTick，靠 ProbeEngine 内部的开合标记做幂等，
    /// 保证即使 tick 中途抛异常也能正确收尾。
    /// 不传 __state：captured 由 BeginTick 写进字段，避免依赖 finalizer 对 __state 的注入行为。
    /// </summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    internal static class Patch_TickManager_DoSingleTick
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            try
            {
                ProbeEngine.BeginTick();
            }
            catch (Exception e)
            {
                PLog.ErrOnce("BeginTick 异常: " + e.Message, 0x9E3701);
            }
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                ProbeEngine.FinishTick();
            }
            catch (Exception e)
            {
                PLog.ErrOnce("FinishTick 异常: " + e.Message, 0x9E3703);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            try
            {
                ProbeEngine.FinishTick();
            }
            catch
            {
            }
        }
    }
}
