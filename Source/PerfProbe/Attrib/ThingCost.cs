using System.Collections.Generic;
using Verse;

namespace PerfProbe
{
    /// <summary>thing 的三分类归类 + 成本键生成。pawn 不在此处，走 PawnCost 专属链路。</summary>
    public static class ThingCost
    {
        public const string CatBuilding = "building";
        public const string CatItem = "item";
        public const string CatOther = "other";
        public const string CatPawn = "pawn";
        public const string CatMod = "mod";
        public const string CatIncident = "incident";

        /// <summary>MapComponent 子类。没有对应 def，只能按类型名归因到模组。</summary>
        public const string CatComponent = "component";

        /// <summary>pawn 个体 / 种族行。与 cat=pawn 的方面行互不重叠，各占独立分母。</summary>
        public const string CatPawnInst = "pawninst";

        /// <summary>
        /// 全局系统行（map pre/post tick、ticklist 容器、storyteller、世界 tick 等）。
        /// 不经过任何 Thing / MapComponent，原先全部混在「未归因残差」里。
        /// </summary>
        public const string CatGlobal = "global";

        /// <summary>
        /// 「引擎自身」的归属名，用于那些不属于任何模组的开销。
        ///
        /// 为什么不记成 Ludeon.RimWorld：pawn 的十四个方面行、global 的十七个系统行
        /// 都是引擎的调度代码，不是某个 mod 的 def。记在原版名下会让原版挡在模组表
        /// 第一位且占比动辄 100% 以上，玩家第一反应是「原版怎么这么重」 —— 而实际上
        /// 这部分换掉任何 mod 都去不掉，它就该被单独看见。
        /// </summary>
        public const string EngineMod = "(引擎自身)";

        /// <summary>按 ThingDef.category 与 def 特性分三类。不做反射。</summary>
        public static string Classify(Thing t)
        {
            if (t == null)
            {
                return CatOther;
            }
            ThingDef def = t.def;
            if (def == null)
            {
                return CatOther;
            }
            ThingCategory c = def.category;
            if (c == ThingCategory.Building)
            {
                return CatBuilding;
            }
            if (c == ThingCategory.Item)
            {
                return CatItem;
            }
            if (c == ThingCategory.Plant)
            {
                // 玩家种的经济作物/树属于"建筑"语义上的固定资产，但游戏内归 Plant；
                // 这里把可收获植物归 item（产出物侧），其余归 other，避免结论里多出一个无用类。
                if (def.plant != null && def.plant.IsTree)
                {
                    return CatOther;
                }
                return CatItem;
            }
            return CatOther;
        }

        /// <summary>
        /// 成本键：cat + defName。用 defName 而非 defNameHash，便于 summary 直接输出可读名。
        /// 返回 null 表示无法归类（调用方跳过）。
        /// </summary>
        public static string MakeKey(Thing t)
        {
            if (t == null || t.def == null)
            {
                return null;
            }
            string cat = Classify(t);
            string defName = t.def.defName;
            if (string.IsNullOrEmpty(defName))
            {
                defName = "?";
            }
            return cat + "|" + defName;
        }

        public static string MakeKey(string cat, string defName)
        {
            if (string.IsNullOrEmpty(defName))
            {
                defName = "?";
            }
            return cat + "|" + defName;
        }

        public static string SplitCat(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return CatOther;
            }
            int i = key.IndexOf('|');
            return i < 0 ? key : key.Substring(0, i);
        }

        public static string SplitName(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "?";
            }
            int i = key.IndexOf('|');
            return i < 0 ? key : key.Substring(i + 1);
        }
    }
}
