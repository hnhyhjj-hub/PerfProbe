using System;
using System.Collections.Generic;
using Verse;

namespace PerfProbe
{
    /// <summary>一个 pawn 的成本身份。建一次缓存起来，热路径只查字典取结构体。</summary>
    public struct PawnIdentity
    {
        /// <summary>成本表里的键低位：个体模式是 thingIDNumber，合并模式是 kindHash 打标记。</summary>
        public ulong Key;

        /// <summary>显示名。个体模式为「名字[种族]」，合并模式为种族 defName。</summary>
        public string Name;

        public string Mod;
        public string Asm;
        public float Conf;

        /// <summary>true 表示已按种族合并（动物 / 机械体），此时分母是同类总数。</summary>
        public bool Merged;
    }

    /// <summary>
    /// pawn 的个体 / 种族二级归因。
    ///
    /// 命名策略（两档）：
    ///   - 动物、机械体：按 kindDef 合并，同一类只留一行。这些单位数量大、个体名无意义，
    ///     逐个体列出来只会把 Top-N 挤满同类行。
    ///   - 人类、异星种族：按个体区分，显示为「名字[kindDef]」，同名个体靠 thingIDNumber 在键上分开。
    ///
    /// 字符串只在首次见到某个 pawn 时构造一次，之后命中 _cache 直接返回结构体，热路径零分配。
    /// </summary>
    public static class PawnIdentityTable
    {
        private const int CacheCap = 4096;

        private static readonly Dictionary<int, PawnIdentity> _cache = new Dictionary<int, PawnIdentity>(512);

        public static int Count => _cache.Count;

        public static void Reset()
        {
            _cache.Clear();
        }

        /// <summary>取某 pawn 的身份。首次调用时构造名字并归因，之后命中缓存。</summary>
        public static PawnIdentity Of(Pawn p)
        {
            if (p == null)
            {
                return default(PawnIdentity);
            }
            int id = p.thingIDNumber;
            PawnIdentity ident;
            if (_cache.TryGetValue(id, out ident))
            {
                return ident;
            }
            ident = Build(p);
            if (_cache.Count < CacheCap)
            {
                _cache[id] = ident;
            }
            return ident;
        }

        private static PawnIdentity Build(Pawn p)
        {
            var ident = new PawnIdentity();
            ident.Key = CostTable.KeyForPawnInstance(p.thingIDNumber, false, 0);
            ident.Merged = false;
            ident.Conf = 1f;
            ident.Asm = AttribIndex.AsmNameOf(p.GetType());

            string kind = SafeKind(p);
            string raceMod = ModOfPawn(p);

            if (IsMergedType(p))
            {
                int kindHash = Hashing.Stable(kind);
                ident.Key = CostTable.KeyForPawnInstance(0, true, kindHash);
                ident.Name = kind;
                ident.Mod = raceMod;
                ident.Merged = true;
                ident.Conf = 1f;
                return ident;
            }

            string label = SafeLabel(p);
            ident.Name = (string.IsNullOrEmpty(label) ? kind : label) + "[" + kind + "]";
            ident.Mod = raceMod;
            return ident;
        }

        /// <summary>
        /// 是否按种族合并。动物与机械体合并，人类与异星种族按个体区分。
        /// RaceProps.Animal 在 1.7 的定义是 ToolUser==false &amp;&amp; IsFlesh &amp;&amp; !IsAnomalyEntity。
        /// </summary>
        public static bool IsMergedType(Pawn p)
        {
            try
            {
                RaceProperties rp = p.RaceProps;
                if (rp == null)
                {
                    return false;
                }
                return rp.Animal || rp.IsMechanoid;
            }
            catch
            {
                return false;
            }
        }

        private static string SafeKind(Pawn p)
        {
            try
            {
                if (p.kindDef != null && !string.IsNullOrEmpty(p.kindDef.defName))
                {
                    return p.kindDef.defName;
                }
            }
            catch
            {
            }
            try
            {
                if (p.def != null && !string.IsNullOrEmpty(p.def.defName))
                {
                    return p.def.defName;
                }
            }
            catch
            {
            }
            return "?";
        }

        private static string SafeLabel(Pawn p)
        {
            try
            {
                return p.LabelShortCap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>种族归因：先查 kindDef（异星种族的 kind 通常属于种族 mod），再退回 def。</summary>
        private static string ModOfPawn(Pawn p)
        {
            try
            {
                if (p.kindDef != null)
                {
                    string m = AttribIndex.ModOfDef(p.kindDef.defName);
                    if (m != null)
                    {
                        return m;
                    }
                }
            }
            catch
            {
            }
            try
            {
                if (p.def != null)
                {
                    string m = AttribIndex.ModOfDef(p.def.defName);
                    if (m != null)
                    {
                        return m;
                    }
                }
            }
            catch
            {
            }
            return "(unknown)";
        }
    }
}
