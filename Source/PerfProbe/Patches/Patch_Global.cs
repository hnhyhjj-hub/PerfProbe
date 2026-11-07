using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 全局系统计时的公共骨架。这些方法都在 TickManager.DoSingleTick 里直接调用，
    /// 不属于任何 Thing / MapComponent —— 原实现把它们全留在「未归因残差」里。
    ///
    /// 记 self 时间而非 inclusive：内部经过的 Thing.DoTick 已被别的 patch 捕获并被
    /// DepthTracker 扣走，剩下的就是这些系统自身的容器开销，正是要看的那个数。
    ///
    /// 注意 Enter 返回 void：Prefix 的返回值是「是否继续执行原方法」，返回 false 会跳过原方法
    /// —— 探针绝不能因为自己不采就禁用游戏逻辑。是否进入计时只走 __state。
    /// </summary>
    internal static class GlobalPatch
    {
        internal static void Enter(out bool entered)
        {
            entered = false;
            if (!ProbeEngine.TickActive)
            {
                return;
            }
            try
            {
                entered = DepthTracker.Enter();
            }
            catch (Exception e)
            {
                PLog.ErrOnce("global 前缀异常: " + e.Message, 0x9E3741);
                entered = false;
            }
        }

        internal static void Exit(int globalId, bool entered)
        {
            if (!entered)
            {
                return;
            }
            try
            {
                double selfMs = DepthTracker.Exit(true);
                if (selfMs > 0.0)
                {
                    ProbeEngine.AddGlobal(globalId, selfMs);
                }
            }
            catch (Exception e)
            {
                PLog.ErrOnce("global 后缀异常: " + e.Message, 0x9E3742);
            }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
    internal static class Patch_Map_PreTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdMapPreTick, __state);
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
    internal static class Patch_Map_PostTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdMapPostTick, __state);
        }
    }

    /// <summary>
    /// tick 列表容器。Normal / Rare / Long 三个列表共用同一个方法，合到一个桶里；
    /// 它内部逐个调 Thing.DoTick，那些时间会被扣掉，只剩遍历与 try/catch 的开销。
    /// </summary>
    [HarmonyPatch(typeof(TickList), nameof(TickList.Tick))]
    internal static class Patch_TickList_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdTickList, __state);
        }
    }

    [HarmonyPatch(typeof(DateNotifier), nameof(DateNotifier.DateNotifierTick))]
    internal static class Patch_DateNotifier_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdDateNotifier, __state);
        }
    }

    [HarmonyPatch(typeof(Scenario), nameof(Scenario.TickScenario))]
    internal static class Patch_Scenario_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdScenario, __state);
        }
    }

    [HarmonyPatch(typeof(World), nameof(World.WorldTick))]
    internal static class Patch_World_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdWorldTick, __state);
        }
    }

    [HarmonyPatch(typeof(World), nameof(World.WorldPostTick))]
    internal static class Patch_World_PostTick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdWorldPostTick, __state);
        }
    }

    [HarmonyPatch(typeof(StoryWatcher), nameof(StoryWatcher.StoryWatcherTick))]
    internal static class Patch_StoryWatcher_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdStoryWatcher, __state);
        }
    }

    [HarmonyPatch(typeof(GameEnder), nameof(GameEnder.GameEndTick))]
    internal static class Patch_GameEnder_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdGameEnder, __state);
        }
    }

    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.StorytellerTick))]
    internal static class Patch_Storyteller_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdStoryteller, __state);
        }
    }

    [HarmonyPatch(typeof(TaleManager), nameof(TaleManager.TaleManagerTick))]
    internal static class Patch_TaleManager_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdTaleManager, __state);
        }
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.QuestManagerTick))]
    internal static class Patch_QuestManager_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdQuestManager, __state);
        }
    }

    [HarmonyPatch(typeof(History), nameof(History.HistoryTick))]
    internal static class Patch_History_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdHistory, __state);
        }
    }

    [HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.GameComponentTick))]
    internal static class Patch_GameComponent_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdGameComponent, __state);
        }
    }

    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.LetterStackTick))]
    internal static class Patch_LetterStack_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdLetterStack, __state);
        }
    }

    [HarmonyPatch(typeof(Autosaver), nameof(Autosaver.AutosaverTick))]
    internal static class Patch_Autosaver_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdAutosaver, __state);
        }
    }

    [HarmonyPatch(typeof(TransportShipManager), nameof(TransportShipManager.ShipObjectsTick))]
    internal static class Patch_TransportShipManager_Tick
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            GlobalPatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            GlobalPatch.Exit(GlobalCost.IdTransportShips, __state);
        }
    }
}
