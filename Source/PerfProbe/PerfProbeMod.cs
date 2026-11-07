using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 模组入口。构造函数只做三件事：存设置、初始化引擎、打一条日志。
    ///
    /// 真正的挂载放在 <see cref="PerfProbeBootstrap"/> 的静态构造里，原因有两条：
    /// 1) 本构造函数由 LoadedModManager.CreateModClasses() 调用，而它运行在
    ///    LoadModXML() 之前 —— 此刻 Def 库还是空的，AttribIndex 的 def 索引必然为 0。
    /// 2) 早期版本用 LongEventHandler.QueueLongEvent(..., doAsynchronously: true) 延后挂载，
    ///    实测 InitPatch 一次都没执行（Player.log 里 [PerfProbe] 零命中），
    ///    而 StaticConstructorOnStartup 由 StaticConstructorOnStartupUtility.CallAll()
    ///    在 def 加载完毕后调用，是官方标准时机，可靠得多。
    /// </summary>
    public sealed class PerfProbeMod : Mod
    {
        public const string HarmonyId = "perfprobe.runtime";

        /// <summary>写入 index.json 的探针版本号。</summary>
        public const string Version = "1.4.0";

        public static PerfProbeMod Instance { get; private set; }

        public static ProbeSettings Settings { get; private set; }

        public PerfProbeMod(ModContentPack content) : base(content)
        {
            Instance = this;
            PLog.Msg("构造函数进入，版本 " + Version);

            try
            {
                Settings = GetSettings<ProbeSettings>();
                Settings.ClampAll();
            }
            catch (Exception e)
            {
                PLog.Err("读取设置失败，使用默认值: " + e);
                Settings = new ProbeSettings();
            }

            ProbeEngine.Init(Settings);
        }

        public override string SettingsCategory()
        {
            return "PerfProbe";
        }

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            ProbeSettingsUI.Draw(inRect, Settings);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            try
            {
                ProbeEngine.ApplySettings();
            }
            catch (Exception e)
            {
                PLog.Warn("应用设置失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 挂载入口。静态构造在 StaticConstructorOnStartupUtility.CallAll() 里执行，
    /// 此时所有 Def 已加载完毕、所有 mod 程序集已加载完毕，适合建归因索引与挂 Harmony patch。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PerfProbeBootstrap
    {
        private static Harmony _harmony;

        /// <summary>带 [HarmonyPatch] 且成功产生补丁的类数量。为 0 说明挂载全线失败，采集不可能工作。</summary>
        public static int PatchOk { get; private set; }

        /// <summary>挂载失败的类数量。</summary>
        public static int PatchBad { get; private set; }

        /// <summary>挂载是否已跑过（区分「还没跑」与「跑了但全失败」）。</summary>
        public static bool Started { get; private set; }

        static PerfProbeBootstrap()
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                PLog.Err("启动挂载失败: " + e);
            }
        }

        private static void Run()
        {
            Started = true;
            ProbeSettings settings = PerfProbeMod.Settings;

            // 必须在任何 patch 生效前认主线程：并行 mod 会让 worker 线程先走到被 patch 的
            // 绘制方法，惰性判定会把主线程 ID 记错，之后所有帧阶段数据都被丢弃。
            try
            {
                FrameDepth.MarkMainThread();
            }
            catch (Exception e)
            {
                PLog.Warn("标记主线程失败: " + e.Message);
            }

            // 归因索引必须先建：MapComponent 挂载时要靠它算 mod / asm / conf。
            try
            {
                AttribIndex.Build();
                PLog.Msg("归因索引: asm=" + AttribIndex.AsmCount
                         + " def=" + AttribIndex.DefCount
                         + " type=" + AttribIndex.TypeCount);
            }
            catch (Exception e)
            {
                PLog.Err("建立归因索引失败: " + e);
            }

            try
            {
                _harmony = new Harmony(PerfProbeMod.HarmonyId);
                PatchEachClass(_harmony);
            }
            catch (Exception e)
            {
                PLog.Err("创建 Harmony 实例失败: " + e);
                return;
            }

            try
            {
                Patch_MapComponents.Install(_harmony);
                ProbeEngine.SetPatchedTypes(Patch_MapComponents.PatchedCount);
            }
            catch (Exception e)
            {
                PLog.Err("挂载 MapComponent patch 失败: " + e);
            }

            try
            {
                Patch_Pawn_Shell.Install(_harmony);
            }
            catch (Exception e)
            {
                PLog.Err("挂载 pawn 外壳层 patch 失败: " + e);
            }

            try
            {
                ProbePaths.Resolve(settings != null ? settings.OutputDir : "");
                PLog.Msg("输出目录: " + ProbePaths.ActiveDir
                         + (ProbePaths.FellBack ? "（已回退：" + ProbePaths.FallbackReason + "）" : ""));
            }
            catch (Exception e)
            {
                PLog.Err("解析输出目录失败: " + e);
            }

            try
            {
                ProbeEngine.ApplySettings();
            }
            catch (Exception e)
            {
                PLog.Warn("应用设置失败: " + e.Message);
            }

            PLog.Msg("已加载。采集默认关闭，在「选项 - 模组设置 - PerfProbe」中开启。");
        }

        /// <summary>
        /// 逐个 patch 类挂载，而不是 Harmony.PatchAll(Assembly)。
        ///
        /// 原因：PatchAll 遇到第一个坏 patch 就抛异常并中止，之前挂上的也一并留在半成品状态，
        /// 而且整个 mod 的采集会静默失效（实测过一次：Prefix 多了一个 __sampled 参数，
        /// 导致 Thing.DoTick 挂载失败、PatchAll 中断，最后所有 patch 全没挂上，
        /// 界面显示「会话累积 0 秒」而日志里只有一条挂载失败）。
        ///
        /// 逐个挂载后，单个类失败只影响它自己，其余照常工作，失败项会逐条打进日志。
        /// </summary>
        private static void PatchEachClass(Harmony harmony)
        {
            int ok = 0;
            int bad = 0;
            Type[] types;
            try
            {
                types = typeof(PerfProbeBootstrap).Assembly.GetTypes();
            }
            catch (Exception e)
            {
                PLog.Err("枚举 patch 类型失败: " + e.Message);
                return;
            }
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                // 注意：不能判 IsAbstract 来排除非 patch 类 —— C# 的 static class 在 IL 里是
                // abstract sealed，IsAbstract 恒为 true。本模组所有 patch 类都是
                // internal static class，加了这个判断会把它们全部跳过，
                // 表现为「patch 一个都没挂上、tick 数为 0」，而手动挂载的 MapComponent 却正常
                // （实测踩过：已挂组件类型 150，DoSingleTick 却没挂，summary 里采集 tick 数 = 0）。
                // 是否该挂载完全由是否有 [HarmonyPatch] 特性决定，与 abstract 无关。
                if (t == null || !t.IsClass)
                {
                    continue;
                }
                // 只处理带 [HarmonyPatch] 的类，且必须自己声明了 Prefix/Postfix/Transpiler/Finalizer。
                object[] attrs;
                try
                {
                    attrs = t.GetCustomAttributes(typeof(HarmonyPatch), false);
                }
                catch
                {
                    continue;
                }
                if (attrs == null || attrs.Length == 0)
                {
                    continue;
                }
                try
                {
                    PatchClassProcessor proc = harmony.CreateClassProcessor(t);
                    List<MethodInfo> patched = proc.Patch();
                    if (patched != null && patched.Count > 0)
                    {
                        ok++;
                    }
                    else
                    {
                        bad++;
                        PLog.Warn("patch 类未产生任何补丁: " + t.FullName);
                    }
                }
                catch (Exception e)
                {
                    bad++;
                    PLog.Err("挂载 patch 类失败 " + t.FullName + ": " + e.Message);
                }
            }
            PLog.Msg("Harmony patch 挂载完成：成功 " + ok + " 个类，失败 " + bad + " 个");
            PatchOk = ok;
            PatchBad = bad;
        }
    }
}
