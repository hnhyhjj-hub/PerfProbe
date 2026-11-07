using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace PerfProbe
{
    /// <summary>一个 MapComponent 子类的归因信息，patch 挂载时算一次，之后只读。</summary>
    internal sealed class ComponentInfo
    {
        public string Name;
        public string Mod;
        public string Asm;
        public float Conf;
    }

    /// <summary>
    /// MapComponent 子类的逐个挂载。
    /// MapComponentTick 在基类是 public virtual 空实现，子类各自 override，
    /// 因此不能用类级 HarmonyPatch 属性，必须运行时枚举子类、逐个 Patch。
    /// 只挂 MapComponentTick（在 MapPostTick 内，属于采集窗口）；
    /// MapComponentUpdate 在 Map.Update 里，不在窗口内，不挂。
    /// </summary>
    internal static class Patch_MapComponents
    {
        private const string MethodName = "MapComponentTick";

        private static readonly Dictionary<Type, ComponentInfo> _info = new Dictionary<Type, ComponentInfo>();

        private static MethodInfo _prefixMi;
        private static MethodInfo _postfixMi;

        public static int PatchedCount { get; private set; }

        public static void Install(Harmony harmony)
        {
            _prefixMi = AccessTools.Method(typeof(Patch_MapComponents), nameof(Prefix));
            _postfixMi = AccessTools.Method(typeof(Patch_MapComponents), nameof(Postfix));
            if (_prefixMi == null || _postfixMi == null)
            {
                PLog.Err("MapComponent patch 方法定位失败，跳过");
                return;
            }

            Type baseType = typeof(MapComponent);
            List<Type> subs;
            try
            {
                subs = baseType.AllSubclasses();
            }
            catch (Exception e)
            {
                PLog.Err("枚举 MapComponent 子类失败: " + e.Message);
                return;
            }
            if (subs == null)
            {
                return;
            }

            var prefix = new HarmonyMethod(_prefixMi);
            var postfix = new HarmonyMethod(_postfixMi);
            int n = 0;
            for (int i = 0; i < subs.Count; i++)
            {
                Type t = subs[i];
                if (t == null || t.IsAbstract)
                {
                    continue;
                }
                MethodInfo mi;
                try
                {
                    mi = t.GetMethod(MethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }
                if (mi == null || mi.IsAbstract)
                {
                    continue;
                }
                try
                {
                    harmony.Patch(mi, prefix, postfix);
                    _info[t] = BuildInfo(t);
                    n++;
                }
                catch (Exception e)
                {
                    PLog.Warn("挂载 " + t.FullName + "." + MethodName + " 失败: " + e.Message);
                }
            }
            PatchedCount = n;
            PLog.Msg("已挂载 MapComponent 子类: " + n + " 个");
        }

        private static ComponentInfo BuildInfo(Type t)
        {
            var ci = new ComponentInfo();
            ci.Name = t.Name;
            string mod = null;
            string asm = null;
            float conf = 0.5f;
            try
            {
                asm = AttribIndex.AsmNameOf(t);
                mod = AttribIndex.ModOfType(t);
                if (mod != null)
                {
                    conf = 1f;
                }
                else
                {
                    // 类型索引未登记时退回程序集索引：同程序集必属同一模组。
                    mod = AttribIndex.ModOfAssembly(asm);
                    conf = mod != null ? 0.8f : 0.5f;
                }
            }
            catch
            {
            }
            ci.Mod = mod ?? "(unknown)";
            ci.Asm = asm ?? "?";
            ci.Conf = conf;
            return ci;
        }

        private static void Prefix(out bool __state)
        {
            __state = false;
            if (!ProbeEngine.TickActive)
            {
                return;
            }
            try
            {
                __state = DepthTracker.Enter();
            }
            catch
            {
                __state = false;
            }
        }

        private static void Postfix(MapComponent __instance, bool __state)
        {
            if (!__state || __instance == null)
            {
                return;
            }
            try
            {
                double selfMs = DepthTracker.Exit(true);
                if (selfMs <= 0.0)
                {
                    return;
                }
                ComponentInfo ci;
                if (!_info.TryGetValue(__instance.GetType(), out ci))
                {
                    ci = BuildInfo(__instance.GetType());
                    _info[__instance.GetType()] = ci;
                }
                ProbeEngine.AddComponent(ci.Name, ci.Mod, ci.Asm, ci.Conf, selfMs, selfMs);
            }
            catch (Exception e)
            {
                PLog.ErrOnce("MapComponent 计时异常: " + e.Message, 0x9E3731);
            }
        }
    }
}
