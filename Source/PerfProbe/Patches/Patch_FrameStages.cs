using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 帧阶段计时的公共骨架。与 GlobalPatch 的区别只有两处：
    /// 用 FrameDepth（独立栈）而不是 DepthTracker，且记账走 AddFrameStage。
    ///
    /// 为什么必须分开：DepthTracker 记录的是 tick 内部的几十层嵌套，
    /// 而这里是另一棵树（UpdatePlay → MapUpdate → DrawMesh）。共用一套栈时
    /// f_tick 会把整帧跑的所有 tick 都记成自己的子段，self 变成 0；
    /// 反过来 tick 内部的残留深度也会让绘制段错位。
    /// </summary>
    internal static class FramePatch
    {
        internal static void Enter(out bool entered)
        {
            entered = false;
            if (!FrameStats.FrameActive)
            {
                return;
            }
            try
            {
                // 只认主线程：并行 mod 会让 worker 线程也走到这些方法。
                if (!FrameDepth.IsMainThread)
                {
                    return;
                }
                entered = FrameDepth.Enter();
            }
            catch (Exception e)
            {
                PLog.ErrOnce("frame 前缀异常: " + e.Message, 0x7A2101);
                entered = false;
            }
        }

        internal static void Exit(int stageId, bool entered)
        {
            if (!entered)
            {
                return;
            }
            try
            {
                double selfMs = FrameDepth.Exit(true);
                if (selfMs > 0.0)
                {
                    ProbeEngine.AddFrameStage(stageId, selfMs);
                }
            }
            catch (Exception e)
            {
                PLog.ErrOnce("frame 后缀异常: " + e.Message, 0x7A2102);
            }
        }
    }

    /// <summary>
    /// 帧边界。挂在 Root.Update 的前缀上：它每帧必被调用一次，
    /// 且在主菜单与游戏内都会走，是唯一稳定的帧锚点。
    ///
    /// 帧长 = 本帧 Root.Update 起点 − 上一帧 Root.Update 起点，
    /// 因此 render、OnGUI、Present 全都落在这个区间里 —— 这正是要看的。
    ///
    /// 与下面的 EngineEnter/EngineExit 分成两个类：Harmony 一个类只能有一个 Prefix。
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class Patch_Frame_Boundary
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            try
            {
                if (!FrameDepth.IsMainThread)
                {
                    return;
                }
                FrameStats.BeginFrame();
            }
            catch (Exception e)
            {
                PLog.ErrOnce("帧边界异常: " + e.Message, 0x7A2103);
            }
        }
    }

    /// <summary>f_engine：Root.Update 自身（扣掉 UpdatePlay 子树）。</summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class Patch_Frame_Root_Update
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void EngineEnter(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void EngineExit(bool __state)
        {
            FramePatch.Exit(FrameCost.IdEngine, __state);
        }
    }

    /// <summary>f_play：Game.UpdatePlay 自段（扣掉 tick / world / map 三个子段）。</summary>
    [HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
    internal static class Patch_Frame_Game_UpdatePlay
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdPlay, __state);
        }
    }

    /// <summary>f_tick：本帧跑的所有 tick 的总耗时。</summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    internal static class Patch_Frame_TickManagerUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdTick, __state);
        }
    }

    /// <summary>f_world：世界地图侧每帧逻辑。</summary>
    [HarmonyPatch(typeof(World), nameof(World.WorldUpdate))]
    internal static class Patch_Frame_WorldUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdWorld, __state);
        }
    }

    /// <summary>f_map：Map.MapUpdate 的非绘制部分。</summary>
    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    internal static class Patch_Frame_MapUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdMapUpdate, __state);
        }
    }

    /// <summary>f_draw_mesh 的前半：地图网格更新。</summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDrawerUpdate_First))]
    internal static class Patch_Frame_MapMeshDrawerUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdDrawMesh, __state);
        }
    }

    /// <summary>f_draw_mesh 的后半：实际提交网格绘制。</summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    internal static class Patch_Frame_DrawMapMesh
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdDrawMesh, __state);
        }
    }

    /// <summary>f_draw_dyn：小人、动物、机械体等动态物件的绘制。</summary>
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    internal static class Patch_Frame_DrawDynamicThings
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdDrawDynamic, __state);
        }
    }

    /// <summary>f_draw_ovl：区域染色、命令点、飘字、污物等叠加层。</summary>
    [HarmonyPatch(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawAllOverlays))]
    internal static class Patch_Frame_DrawAllOverlays
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdDrawOverlay, __state);
        }
    }

    /// <summary>f_draw_ovl 的另一半：临时物件（拖拽预览、建造幻影等）。</summary>
    [HarmonyPatch(typeof(TemporaryThingDrawer), nameof(TemporaryThingDrawer.Draw))]
    internal static class Patch_Frame_TemporaryThingDraw
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdDrawOverlay, __state);
        }
    }

    /// <summary>f_draw_ovl：特效（fleck）。</summary>
    [HarmonyPatch(typeof(FleckManager), nameof(FleckManager.FleckManagerDraw))]
    internal static class Patch_Frame_FleckManagerDraw
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdDrawOverlay, __state);
        }
    }

    /// <summary>f_gui：IMGUI 阶段（原版 UI、窗口、浮动菜单）。</summary>
    [HarmonyPatch(typeof(Root), nameof(Root.OnGUI))]
    internal static class Patch_Frame_Root_OnGUI
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            FramePatch.Enter(out __state);
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            FramePatch.Exit(FrameCost.IdGui, __state);
        }
    }
}
