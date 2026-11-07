using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PerfProbe
{
    /// <summary>
    /// pawn 十四个方面里「叶子层」的公共骨架（核心组 + 扩展组，共十二个桶）。
    /// 外壳层（`pawn_body` / `pawn_interval`）在 Patch_Pawn_Shell 里，两者互不重叠。
    /// 只有 pawn 的 Thing.DoTick 处于采样中（PawnTickContext.Active）时这些 patch 才记账，
    /// 其余情况开销是一次静态布尔判断。
    ///
    /// self / total 的取舍规则：
    ///   本 patch 的子树里还有别的 aspect patch → 记 self（避免与子帧重复计数）；
    ///   叶子帧 → 记 total（含其内部未 patch 的子调用，如 Hediff.Tick）。
    /// 这样各叶子桶时间两两不重叠。
    /// 注意它们的时间都含在 `pawn_body` 的 inclusive 时间里，所以不能与外壳层相加。
    /// </summary>
    internal static class AspectPatch
    {
        internal static bool Enter(out bool entered)
        {
            entered = false;
            if (!ProbeEngine.TickActive || !PawnTickContext.Active)
            {
                return false;
            }
            try
            {
                entered = DepthTracker.Enter();
            }
            catch (Exception e)
            {
                PLog.ErrOnce("aspect 前缀异常: " + e.Message, 0x9E3721);
                entered = false;
                return false;
            }
            return true;
        }

        /// <summary>记 self（用于有 aspect 子帧的方法）。</summary>
        internal static void ExitSelf(int aspectId, bool entered)
        {
            if (!entered)
            {
                return;
            }
            try
            {
                double selfMs = DepthTracker.Exit(true);
                PawnTickContext.AddAspect(aspectId, selfMs);
            }
            catch (Exception e)
            {
                PLog.ErrOnce("aspect 后缀异常: " + e.Message, 0x9E3722);
            }
        }

        /// <summary>记 total（用于叶子方法）。</summary>
        internal static void ExitTotal(int aspectId, bool entered)
        {
            if (!entered)
            {
                return;
            }
            try
            {
                double selfMs;
                double totalMs = DepthTracker.ExitWithTotal(true, out selfMs);
                PawnTickContext.AddAspect(aspectId, totalMs);
            }
            catch (Exception e)
            {
                PLog.ErrOnce("aspect 后缀异常: " + e.Message, 0x9E3723);
            }
        }
    }

    // ---------------- 思考决策 ----------------
    // JobTrackerTick 内含 curDriver.DriverTick()（已单独记为工作驱动），因此这里只记 self。

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick))]
    internal static class Patch_Pawn_JobTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitSelf(PawnCost.IdThink, __state);
        }
    }

    // ---------------- 工作驱动 ----------------

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTick))]
    internal static class Patch_JobDriver_DriverTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdWork, __state);
        }
    }

    // ---------------- 寻路 ----------------

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
    internal static class Patch_Pawn_PathFollower_PatherTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdPath, __state);
        }
    }

    // ---------------- 状态效果与需求 ----------------

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick))]
    internal static class Patch_Pawn_HealthTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdState, __state);
        }
    }

    [HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval))]
    internal static class Patch_Pawn_NeedsTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdState, __state);
        }
    }

    [HarmonyPatch(typeof(Pawn_MindState), nameof(Pawn_MindState.MindStateTickInterval))]
    internal static class Patch_Pawn_MindStateTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdState, __state);
        }
    }

    [HarmonyPatch(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.StanceTrackerTick))]
    internal static class Patch_Pawn_StanceTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdState, __state);
        }
    }

    [HarmonyPatch(typeof(VerbTracker), nameof(VerbTracker.VerbsTick))]
    internal static class Patch_VerbTracker_VerbsTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdState, __state);
        }
    }

    // ---------------- 组件与装备 ----------------

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.EquipmentTrackerTick))]
    internal static class Patch_Pawn_EquipmentTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdGear, __state);
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.ApparelTrackerTickInterval))]
    internal static class Patch_Pawn_ApparelTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdGear, __state);
        }
    }

    // ================= 扩展组：把原先全落进 pawn_other 的 tracker 拆开 =================
    //
    // 这些方法全部是 Pawn.Tick / Pawn.TickInterval 的直接子调用（Verse.Pawn.cs:1590-1615 与 :1618+），
    // 因此它们的时间本来就在 DoTick 的 self 时间里，拆出来只是换个桶，不改变总和。

    // ---------------- 基因 ----------------
    // 异星种族（Wolfein / Milira / Ratkin）的主要嫌疑点，来自 Verse.Pawn.cs:1592 `genes?.GeneTrackerTick()`。

    [HarmonyPatch(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.GeneTrackerTick))]
    internal static class Patch_Pawn_GeneTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdGene, __state);
        }
    }

    // ---------------- 能力 ----------------
    // Verse.Pawn.cs:1590 `abilities?.AbilitiesTick()`。

    [HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.AbilitiesTick))]
    internal static class Patch_Pawn_AbilityTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdAbility, __state);
        }
    }

    // ---------------- 变异体 ----------------
    // Verse.Pawn.cs:1595 `mutant?.MutantTrackerTick()`，只在 Anomaly 激活时跑。

    [HarmonyPatch(typeof(Pawn_MutantTracker), nameof(Pawn_MutantTracker.MutantTrackerTick))]
    internal static class Patch_Pawn_MutantTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdMutant, __state);
        }
    }

    // ---------------- 低频 tick ----------------
    // Verse.Pawn.cs:1546-1549 `if (this.IsHashIntervalTick(250)) TickRare();`
    // 每 250 刻一次，单次可能很贵；因为它是 Pawn.Tick 的子调用，走 total 记法。
    // TickRare 在 Verse.Pawn 里是 public override（Verse.Pawn.cs:1524），可直接用 nameof。

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.TickRare))]
    internal static class Patch_Pawn_TickRare
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdRare, __state);
        }
    }

    // ---------------- 渲染特化 ----------------
    // Verse.Pawn.cs:1615 `drawer?.renderer.EffectersTick(...)`。
    // PawnRenderer.EffectersTick(bool suspended) 是 public，位于 Verse 命名空间。

    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.EffectersTick))]
    internal static class Patch_PawnRenderer_EffectersTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdRender, __state);
        }
    }

    // ---------------- 杂项兜底组 ----------------
    // 剩下四个 Pawn.Tick 子调用 + 一个 TickInterval 子调用，逐个拆进 pawn_misc。

    // Verse.Pawn.cs:1591 `inventory?.InventoryTrackerTick()`（类型 Verse.Pawn_InventoryTracker）
    [HarmonyPatch(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.InventoryTrackerTick))]
    internal static class Patch_Pawn_InventoryTrackerTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdMisc, __state);
        }
    }

    // Verse.Pawn.cs:1563 `roping?.RopingTick()`（类型 RimWorld.Pawn_RopeTracker，注意不叫 RopingTracker）
    [HarmonyPatch(typeof(Pawn_RopeTracker), nameof(Pawn_RopeTracker.RopingTick))]
    internal static class Patch_Pawn_RopeTracker_RopingTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdMisc, __state);
        }
    }

    // Verse.Pawn.cs:1564 `flight?.FlightTick()`（类型 RimWorld.Pawn_FlightTracker）
    [HarmonyPatch(typeof(Pawn_FlightTracker), nameof(Pawn_FlightTracker.FlightTick))]
    internal static class Patch_Pawn_FlightTracker_FlightTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdMisc, __state);
        }
    }

    // Verse.Pawn.cs:1565 `natives.NativeVerbsTick()`（类型 RimWorld.Pawn_NativeVerbs）
    [HarmonyPatch(typeof(Pawn_NativeVerbs), nameof(Pawn_NativeVerbs.NativeVerbsTick))]
    internal static class Patch_Pawn_NativeVerbs_NativeVerbsTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            AspectPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            AspectPatch.ExitTotal(PawnCost.IdMisc, __state);
        }
    }
}
