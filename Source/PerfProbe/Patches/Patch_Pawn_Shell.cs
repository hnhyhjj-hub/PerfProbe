using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// pawn 的外壳层：把 Pawn.Tick 与 Pawn.TickInterval 各记成一个 inclusive 桶。
    ///
    /// 为什么需要它们：原实现只有叶子锚点（pather / jobs / health / gene ...），
    /// 于是「pawn_other」混装了三种完全不同的东西 —— Pawn.Tick 自身外壳、
    /// Pawn.TickInterval 里 29 个未被 patch 的子调用、以及 Thing.DoTick 的容器开销。
    /// 三者的优化手段完全不同（前者无法降频、后者可以调 tick 间隔），必须分开才能判断。
    ///
    /// 为什么用 inclusive：回答的是「Pawn.Tick 一共吞了多少时间」，
    /// 所以要把内部子调用一起算进来；叶子桶是它内部的细分说明，两者不能相加。
    ///
    /// 为什么手动挂载而不是 [HarmonyPatch]：两个目标都是 protected override，
    /// 特性写法看不到它们（非 public）。
    ///
    /// 为什么只挂基类、不扫子类：若有 Pawn 子类 override 了 Tick 且内部调 base.Tick()，
    /// 子类 patch 与基类 patch 会把同一段时间记两遍，直接污染数据。
    /// 漏挂的代价小得多 —— 那些 pawn 的 body 桶为 0，PawnTickContext 会自动退回旧口径，不会算错。
    /// 因此这里只做一次「有多少子类 override 了」的诊断统计，不逐个挂载。
    /// </summary>
    internal static class Patch_Pawn_Shell
    {
        internal const string TickName = "Tick";
        internal const string TickIntervalName = "TickInterval";

        /// <summary>基类 tick 是否挂载成功。false 时 PawnTickContext 走旧口径。</summary>
        internal static bool TickPatched { get; private set; }

        internal static bool IntervalPatched { get; private set; }

        /// <summary>自己 override 了 Tick 的 Pawn 子类数（诊断用，不挂载）。</summary>
        internal static int TickOverrideSubclasses { get; private set; }

        /// <summary>自己 override 了 TickInterval 的 Pawn 子类数（诊断用，不挂载）。</summary>
        internal static int IntervalOverrideSubclasses { get; private set; }

        internal static void Install(Harmony harmony)
        {
            MethodInfo tickPrefix = typeof(Patch_Pawn_Shell).GetMethod(nameof(TickPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo tickPostfix = typeof(Patch_Pawn_Shell).GetMethod(nameof(TickPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo intervalPrefix = typeof(Patch_Pawn_Shell).GetMethod(nameof(IntervalPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo intervalPostfix = typeof(Patch_Pawn_Shell).GetMethod(nameof(IntervalPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            TickPatched = InstallBase(harmony, TickName, tickPrefix, tickPostfix);
            IntervalPatched = InstallBase(harmony, TickIntervalName, intervalPrefix, intervalPostfix);

            int tickSubs, intervalSubs;
            CountOverrides(out tickSubs, out intervalSubs);
            TickOverrideSubclasses = tickSubs;
            IntervalOverrideSubclasses = intervalSubs;

            PLog.Msg("pawn 外壳层: Tick=" + (TickPatched ? "已挂载" : "失败")
                     + ", TickInterval=" + (IntervalPatched ? "已挂载" : "失败")
                     + "；子类 override 数 Tick=" + tickSubs + " TickInterval=" + intervalSubs
                     + (tickSubs + intervalSubs > 0 ? "（这些子类的 pawn 不计 body/interval 桶，退回旧口径）" : ""));
        }

        private static bool InstallBase(Harmony harmony, string methodName,
                                        MethodInfo prefix, MethodInfo postfix)
        {
            MethodInfo mi = FindDeclared(typeof(Pawn), methodName);
            if (mi == null)
            {
                PLog.Warn("找不到 Pawn." + methodName + "，外壳层缺失");
                return false;
            }
            try
            {
                harmony.Patch(mi, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                return true;
            }
            catch (Exception e)
            {
                PLog.Warn("挂载 Pawn." + methodName + " 失败: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 诊断：统计有多少 Pawn 子类自己声明了同名方法。
        /// 这些子类的 pawn 不会产生 body / interval 桶 —— 记日志让人能解释「为什么某些 pawn 的 body 是 0」。
        /// </summary>
        private static void CountOverrides(out int tickSubs, out int intervalSubs)
        {
            tickSubs = 0;
            intervalSubs = 0;
            List<Type> subs;
            try
            {
                subs = typeof(Pawn).AllSubclasses();
            }
            catch
            {
                return;
            }
            if (subs == null)
            {
                return;
            }
            for (int i = 0; i < subs.Count; i++)
            {
                Type t = subs[i];
                if (t == null)
                {
                    continue;
                }
                if (FindDeclared(t, TickName) != null)
                {
                    tickSubs++;
                }
                if (FindDeclared(t, TickIntervalName) != null)
                {
                    intervalSubs++;
                }
            }
        }

        /// <summary>
        /// 只取「该类型自己声明的」方法。DeclaredOnly 保证没 override 时返回 null，
        /// 不会把继承来的基类方法算成一次覆盖。
        ///
        /// 不限定参数签名：Pawn.Tick 无参，但 Pawn.TickInterval(int delta) 有一个参数，
        /// 统一用 Type.EmptyTypes 查会让 TickInterval 永远查不到
        /// （日志表现：「找不到 Pawn.TickInterval，外壳层缺失」）。
        /// </summary>
        private static MethodInfo FindDeclared(Type t, string methodName)
        {
            try
            {
                MethodInfo[] all = t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic
                    | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (all == null)
                {
                    return null;
                }
                for (int i = 0; i < all.Length; i++)
                {
                    MethodInfo mi = all[i];
                    if (mi == null || mi.Name != methodName || mi.IsAbstract)
                    {
                        continue;
                    }
                    return mi;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------- Pawn.Tick ----------------

        [HarmonyPrefix]
        private static void TickPrefix(out bool __state)
        {
            __state = ShellEnter();
        }

        [HarmonyPostfix]
        private static void TickPostfix(bool __state)
        {
            ShellExit(PawnCost.IdBody, __state);
        }

        // ---------------- Pawn.TickInterval ----------------

        [HarmonyPrefix]
        private static void IntervalPrefix(out bool __state)
        {
            __state = ShellEnter();
        }

        [HarmonyPostfix]
        private static void IntervalPostfix(bool __state)
        {
            ShellExit(PawnCost.IdInterval, __state);
        }

        /// <summary>
        /// 外壳层的进入条件：必须在采样中的 tick 里、且当前 pawn 上下文已激活。
        /// 未激活说明这次不是被计时的 pawn 的 DoTick（例如 world pawn 走别的路径），不记账。
        /// </summary>
        private static bool ShellEnter()
        {
            if (!ProbeEngine.TickActive || !PawnTickContext.Active)
            {
                return false;
            }
            try
            {
                return DepthTracker.Enter();
            }
            catch (Exception e)
            {
                PLog.ErrOnce("pawn 外壳层前缀异常: " + e.Message, 0x9E3731);
                return false;
            }
        }

        private static void ShellExit(int aspectId, bool entered)
        {
            if (!entered)
            {
                return;
            }
            try
            {
                double selfMs;
                double totalMs = DepthTracker.ExitWithTotal(true, out selfMs);
                // 记 inclusive：外壳层的意义就是「整段一共多少」，内部子调用算在内。
                PawnTickContext.AddAspect(aspectId, totalMs);
            }
            catch (Exception e)
            {
                PLog.ErrOnce("pawn 外壳层后缀异常: " + e.Message, 0x9E3732);
            }
        }
    }
}
