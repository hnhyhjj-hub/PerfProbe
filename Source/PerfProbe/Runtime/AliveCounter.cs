using System.Collections.Generic;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 活跃 / 休眠实例计数。活跃 = 真的会 tick（tickerType != Never）；
    /// 休眠单独计数，不进每实例均耗的分母。
    /// </summary>
    public static class AliveCounter
    {
        private static readonly Dictionary<string, int> _alive = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _dormant = new Dictionary<string, int>();
        private static readonly HashSet<int> _seenPawns = new HashSet<int>();
        private static bool _dirty;
        private static int _pawnCount;

        public static bool Dirty => _dirty;

        /// <summary>当前 spawned 的 pawn 总数，pawn 行的每实例均耗分母。</summary>
        public static int PawnCount => _pawnCount;

        public static void Reset()
        {
            _alive.Clear();
            _dormant.Clear();
            _pawnCount = 0;
            _dirty = true;
        }

        public static void AddAlive(string key, int n)
        {
            if (n <= 0)
            {
                return;
            }
            int cur;
            _alive.TryGetValue(key, out cur);
            _alive[key] = cur + n;
            _dirty = true;
        }

        public static void AddDormant(string key, int n)
        {
            if (n <= 0)
            {
                return;
            }
            int cur;
            _dormant.TryGetValue(key, out cur);
            _dormant[key] = cur + n;
            _dirty = true;
        }

        public static int GetAlive(string key)
        {
            int v;
            return _alive.TryGetValue(key, out v) ? v : 0;
        }

        public static int GetDormant(string key)
        {
            int v;
            return _dormant.TryGetValue(key, out v) ? v : 0;
        }

        public static Dictionary<string, int> AliveMap => _alive;

        public static Dictionary<string, int> DormantMap => _dormant;

        /// <summary>
        /// 重新扫描所有地图，刷新活跃/休眠计数。每个快照窗口调一次（5 秒），不是每 tick。
        ///
        /// 三个来源合并，缺一不可：
        ///   1. map.listerThings.AllThings —— 常规 spawned thing 与 pawn；
        ///   2. map.mapPawns.AllPawns —— 被移出 spawned 状态但仍在 tick 的 pawn。
        ///      实测 TAPI_Barn 会把动物从 AllThings 里摘走，只查第 1 源会让它们 n=0，
        ///      表现为「有耗时却没有实例」；
        ///   3. map.components —— MapComponent 没有 def，按类型名计数。
        /// 第 1、2 源以 thingIDNumber 去重，避免同一只 pawn 被算两遍。
        /// </summary>
        public static void Rebuild()
        {
            _alive.Clear();
            _dormant.Clear();
            _pawnCount = 0;

            List<Map> maps = null;
            try
            {
                maps = Find.Maps;
            }
            catch
            {
                maps = null;
            }
            if (maps == null)
            {
                _dirty = false;
                return;
            }

            for (int m = 0; m < maps.Count; m++)
            {
                Map map = maps[m];
                if (map == null)
                {
                    continue;
                }

                _seenPawns.Clear();

                ListerThings lt = map.listerThings;
                List<Thing> all = lt != null ? lt.AllThings : null;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        Thing t = all[i];
                        if (t == null || t.def == null)
                        {
                            continue;
                        }
                        Pawn pawn = t as Pawn;
                        if (pawn != null)
                        {
                            CountPawn(pawn);
                            continue;
                        }
                        string key = ThingCost.MakeKey(t);
                        if (key == null)
                        {
                            continue;
                        }
                        if (t.def.tickerType == TickerType.Never)
                        {
                            AddDormant(key, 1);
                        }
                        else
                        {
                            AddAlive(key, 1);
                        }
                    }
                }

                // 第二源：补上不在 AllThings 里的 pawn（被建筑收容、正在飞行、世界 pawn 回图等）。
                MapPawns mp = map.mapPawns;
                List<Pawn> pawns = mp != null ? mp.AllPawns : null;
                if (pawns != null)
                {
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Pawn p = pawns[i];
                        if (p == null || p.def == null)
                        {
                            continue;
                        }
                        CountPawn(p);
                    }
                }

                // 第三源：MapComponent 没有 def，按类型名计数，键与 CostTable 的 component 行同名。
                List<MapComponent> comps = map.components;
                if (comps != null)
                {
                    for (int i = 0; i < comps.Count; i++)
                    {
                        MapComponent mc = comps[i];
                        if (mc == null)
                        {
                            continue;
                        }
                        AddAlive(ThingCost.MakeKey(ThingCost.CatComponent, mc.GetType().Name), 1);
                    }
                }
            }

            _dirty = false;
        }

        /// <summary>按 thingIDNumber 去重地计入一只 pawn：pawn 总数、合并模式的同类计数。</summary>
        private static void CountPawn(Pawn pawn)
        {
            if (!_seenPawns.Add(pawn.thingIDNumber))
            {
                return;
            }
            _pawnCount++;
            if (PawnIdentityTable.IsMergedType(pawn))
            {
                PawnIdentity ident = PawnIdentityTable.Of(pawn);
                if (ident.Name != null)
                {
                    AddAlive(ThingCost.MakeKey(ThingCost.CatPawnInst, ident.Name), 1);
                }
            }
        }
    }
}
