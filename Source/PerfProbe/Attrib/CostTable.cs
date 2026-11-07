using System;
using System.Collections.Generic;

namespace PerfProbe
{
    /// <summary>一行成本记录。结构体，快照时一次产出，不参与热路径。</summary>
    public struct CostRow
    {
        public string Cat;
        public string Name;
        public string Mod;
        public string Asm;
        public double SelfMs;
        public double InclMs;
        public double Pct;
        public int Alive;
        public int Dormant;
        public double PerInstMs;
        public float Conf;

        /// <summary>窗口级样本的 50 分位（中值）。区分「常态成本」用。</summary>
        public double P50W;

        /// <summary>窗口级样本的 95 分位。</summary>
        public double P95W;

        /// <summary>窗口级样本的最大值。区分「峰值成本」用。</summary>
        public double MaxW;

        /// <summary>窗口级样本的标准差。越大说明越不稳定。</summary>
        public double SdW;

        /// <summary>仅会话快照有值：该行在最贵那一窗口里的耗时占该窗口总 tick 的百分比。</summary>
        public double PctPeak;

        /// <summary>窗口级样本个数（= 该行出现过的窗口数）。</summary>
        public int WinN;

        /// <summary>
        /// 实例数是否可信。AliveCounter 扫不到（n=0）但有耗时时置 false：
        /// 实例数一栏会显示 `?`，避免被读成「没有实例却有耗时」的矛盾。
        /// </summary>
        public bool AliveKnown;
    }

    /// <summary>
    /// 定长桶 + TopK 淘汰 + 双列（self / inclusive）聚合。
    /// 热路径零分配：键是 ulong（catId 高位 + defNameHash 低位），只在首次遇到某键时构造 Slot。
    /// </summary>
    public static class CostTable
    {
        /// <summary>
        /// 每类桶数上限（按不同 def / 类型计，不是实例数）。超过后新键合并进 others 槽，
        /// summary 的「按类合计」就不会包含溢出部分。512 在 300+ mod 的环境里不够用
        /// （实测建筑段会顶到 200 行的输出上限），这里放到 4096。
        /// 内存代价：每桶约 80 字节槽 + 字典项，4096 × 7 类最坏约 2.3 MB，可接受。
        /// </summary>
        public const int PerCatCap = 4096;

        private const byte CatBuildingId = 0;
        private const byte CatItemId = 1;
        private const byte CatOtherId = 2;
        private const byte CatPawnId = 3;
        private const byte CatIncidentId = 4;
        private const byte CatComponentId = 5;
        private const byte CatPawnInstId = 6;
        private const byte CatGlobalId = 7;
        private const byte CatCount = 8;

        private sealed class Slot
        {
            public byte CatId;
            public string Name;
            public string Mod;
            public string Asm;
            public double SelfMs;
            public double InclMs;
            public float Conf;

            /// <summary>键的低 32 位。只给 pawninst 的 ToRow 判合并标记用，其余类别忽略。</summary>
            public uint KeyLow;

            // ---- 窗口级序列（只在会话字典里维护，窗口字典不建） ----

            /// <summary>滚动环形缓冲：每个已关闭窗口贡献一个样本 = 该行在该窗口内的 selfMs。</summary>
            public float[] Series;

            /// <summary>环形缓冲写指针。</summary>
            public int SeriesHead;

            /// <summary>环形缓冲有效样本数（≤ SeriesCap）。</summary>
            public int SeriesCount;

            /// <summary>该行出现过的最贵窗口的 selfMs 与该窗口的总 tick 耗时，用于算 PctPeak。</summary>
            public double PeakSelfMs;
            public double PeakWindowTotalMs;
        }

        /// <summary>每个桶保留多少个窗口级样本（滚动窗口）。128 个窗口 × 5 秒 ≈ 10.7 分钟。</summary>
        public const int SeriesCap = 128;

        private static readonly float[] _pctScratch = new float[SeriesCap];
        private static int _windowCount;
        private static double _lastWindowTotalMs;

        /// <summary>已关闭的窗口数。summary 用它说明「P50W 等列基于多少个样本」。</summary>
        public static int WindowCount => _windowCount;

        private static readonly Dictionary<ulong, Slot> _slots = new Dictionary<ulong, Slot>(1024);
        private static readonly int[] _perCatCount = new int[CatCount];
        private static readonly Slot[] _overflow = new Slot[CatCount];

        // 会话累计（跨窗口），summary 用
        private static readonly Dictionary<ulong, Slot> _session = new Dictionary<ulong, Slot>(1024);
        private static double _sessionTotalMs;

        private static readonly List<Slot> _sortBuf = new List<Slot>(1024);
        private static readonly List<ulong> _keyBuf = new List<ulong>(1024);

        public static double SessionTotalMs => _sessionTotalMs;

        // ---------- 键构造（热路径，零分配） ----------

        public static byte CatIdOf(string cat)
        {
            switch (cat)
            {
                case ThingCost.CatBuilding: return CatBuildingId;
                case ThingCost.CatItem: return CatItemId;
                case ThingCost.CatPawn: return CatPawnId;
                case ThingCost.CatIncident: return CatIncidentId;
                case ThingCost.CatComponent: return CatComponentId;
                case ThingCost.CatPawnInst: return CatPawnInstId;
                case ThingCost.CatGlobal: return CatGlobalId;
                default: return CatOtherId;
            }
        }

        public static string CatNameOf(byte id)
        {
            switch (id)
            {
                case CatBuildingId: return ThingCost.CatBuilding;
                case CatItemId: return ThingCost.CatItem;
                case CatPawnId: return ThingCost.CatPawn;
                case CatIncidentId: return ThingCost.CatIncident;
                case CatComponentId: return ThingCost.CatComponent;
                case CatPawnInstId: return ThingCost.CatPawnInst;
                case CatGlobalId: return ThingCost.CatGlobal;
                default: return ThingCost.CatOther;
            }
        }

        /// <summary>thing 用：catId + defNameHash。defNameHash 是 Def 上预缓存的 int，读取不分配。</summary>
        public static ulong KeyForThing(byte catId, int defNameHash)
        {
            return ((ulong)catId << 32) | (uint)defNameHash;
        }

        /// <summary>pawn 五方面用：catId=pawn + 方面序号。</summary>
        public static ulong KeyForAspect(int aspectId)
        {
            return ((ulong)CatPawnId << 32) | (uint)aspectId;
        }

        /// <summary>事件用：catId=incident + defNameHash。</summary>
        public static ulong KeyForIncident(int defNameHash)
        {
            return ((ulong)CatIncidentId << 32) | (uint)defNameHash;
        }

        /// <summary>MapComponent 用：catId=component + 类型名稳定哈希。</summary>
        public static ulong KeyForComponent(int typeNameHash)
        {
            return ((ulong)CatComponentId << 32) | (uint)typeNameHash;
        }

        /// <summary>
        /// pawn 个体 / 种族行。
        /// 个体模式：merged=false，id 用 thingIDNumber，同名的两只不会合并。
        /// 合并模式：merged=true，id 传 0，kindHash 是 kindDef.defName 的稳定哈希。
        /// 低位最高位（0x80000000）作为合并标记，避开 thingIDNumber 的取值范围。
        /// </summary>
        public static ulong KeyForPawnInstance(int thingId, bool merged, int kindHash)
        {
            uint lo = merged ? (0x80000000u | (uint)(kindHash & 0x7FFFFFFF)) : (uint)thingId;
            return ((ulong)CatPawnInstId << 32) | lo;
        }

        /// <summary>全局系统用：catId=global + 固定序号（不是哈希，序号来自 GlobalCost）。</summary>
        public static ulong KeyForGlobal(int globalId)
        {
            return ((ulong)CatGlobalId << 32) | (uint)globalId;
        }

        // ---------- 累加 ----------

        /// <summary>累加一个窗口样本。name 只在首次建 Slot 时读取，之后忽略。</summary>
        public static void Add(ulong key, byte catId, string name, double selfMs, double inclMs)
        {
            AddInternal(key, catId, name, null, null, 0f, selfMs, inclMs);
        }

        /// <summary>
        /// 带显式归因的累加。MapComponent 这类没有对应 def 的对象用这个，
        /// 由调用方直接给出 mod / asm / conf，避免退化到 (unknown)。
        /// </summary>
        public static void AddWithAttr(ulong key, byte catId, string name, string mod, string asm,
                                       float conf, double selfMs, double inclMs)
        {
            AddInternal(key, catId, name, mod, asm, conf, selfMs, inclMs);
        }

        /// <summary>pawn 方面累加。aspectId 与 PawnCost.AllAspects 下标一致。</summary>
        public static void AddPawnAspect(int aspectId, string name, double selfMs, double inclMs)
        {
            AddInternal(KeyForAspect(aspectId), CatPawnId, name, null, null, 0f, selfMs, inclMs);
        }

        /// <summary>MapComponent 子类累加，归因由调用方显式给出。</summary>
        public static void AddComponent(string typeName, string mod, string asm, float conf,
                                        double selfMs, double inclMs)
        {
            AddInternal(KeyForComponent(Hashing.Stable(typeName)), CatComponentId, typeName,
                        mod, asm, conf, selfMs, inclMs);
        }

        /// <summary>事件累加。</summary>
        public static void AddIncident(string defName, string mod, double selfMs, double inclMs)
        {
            AddInternal(KeyForIncident(Hashing.Stable(defName)), CatIncidentId, defName,
                        mod, null, 1f, selfMs, inclMs);
        }

        /// <summary>
        /// pawn 个体 / 种族行累加。归因由 PawnIdentityTable 算好传进来，
        /// name 与 mod 只在首次建 Slot 时用，热路径命中后不再读。
        /// </summary>
        public static void AddPawnInstance(ulong key, string name, string mod, string asm,
                                           float conf, double selfMs, double inclMs)
        {
            AddInternal(key, CatPawnInstId, name, mod, asm, conf, selfMs, inclMs);
        }

        /// <summary>
        /// 全局系统累加（map_p retick / ticklist / storyteller 等）。
        /// 这些系统不属于任何模组的 def，归因统一给「引擎自身」：它们是引擎的调度代码，
        /// 换掉任何 mod 都去不掉，所以不该占用某个 mod 的账。
        /// </summary>
        public static void AddGlobal(int globalId, double selfMs)
        {
            AddInternal(KeyForGlobal(globalId), CatGlobalId, GlobalCost.NameOf(globalId),
                        ThingCost.EngineMod, "Assembly-CSharp", 1f, selfMs, selfMs);
        }

        private static void AddInternal(ulong key, byte catId, string name, string mod, string asm,
                                        float conf, double selfMs, double inclMs)
        {
            Slot s;
            if (!_slots.TryGetValue(key, out s))
            {
                // 新键。NewSlot 一定会返回槽位：正常键返回新建槽，溢出键返回该类的 others 槽。
                // 不能返回 null —— 那会让溢出部分只累进窗口字典、不进会话字典，
                // 使 summary 的「按类合计」静默少算。
                s = NewSlot(key, catId, name, mod, asm, conf, selfMs, inclMs);
            }
            else
            {
                s.SelfMs += selfMs;
                s.InclMs += inclMs;
            }

            // 会话槽用 s 的真实键（溢出时是 others 的键，不是本次的 key），
            // 否则同一类的所有溢出对象会各自建一个会话槽。
            ulong sKey = ((ulong)s.CatId << 32) | s.KeyLow;
            Slot ss;
            if (!_session.TryGetValue(sKey, out ss))
            {
                ss = new Slot();
                ss.CatId = s.CatId;
                ss.Name = s.Name;
                ss.Mod = s.Mod;
                ss.Asm = s.Asm;
                ss.Conf = s.Conf;
                ss.KeyLow = s.KeyLow;
                _session[sKey] = ss;
            }
            ss.SelfMs += selfMs;
            ss.InclMs += inclMs;
        }

        private static Slot NewSlot(ulong key, byte catId, string name, string mod, string asm,
                                    float conf, double selfMs, double inclMs)
        {
            if (name == null)
            {
                name = "?";
            }

            if (_perCatCount[catId] >= PerCatCap)
            {
                Slot ov = _overflow[catId];
                if (ov == null)
                {
                    ov = new Slot();
                    ov.CatId = catId;
                    ov.Name = "others";
                    ov.Mod = "(merged)";
                    ov.Asm = "?";
                    ov.Conf = 1f;
                    ov.KeyLow = 0xFFFFFFFFu;
                    ov.SelfMs = selfMs;
                    ov.InclMs = inclMs;
                    _overflow[catId] = ov;
                    _slots[((ulong)catId << 32) | 0xFFFFFFFFu] = ov;
                    return ov;
                }
                ov.SelfMs += selfMs;
                ov.InclMs += inclMs;
                return ov;
            }

            var s = new Slot();
            s.CatId = catId;
            s.Name = name;
            s.KeyLow = (uint)(key & 0xFFFFFFFFu);
            s.SelfMs = selfMs;
            s.InclMs = inclMs;
            if (mod != null)
            {
                s.Mod = mod;
                s.Asm = asm ?? "?";
                s.Conf = conf;
            }
            else
            {
                ResolveAttribution(s, name);
            }
            _slots[key] = s;
            _perCatCount[catId]++;
            return s;
        }

        /// <summary>只在建 Slot 时跑一次：查三张索引填 mod / asm / conf。</summary>
        private static void ResolveAttribution(Slot s, string name)
        {
            if (s.CatId == CatPawnId)
            {
                s.Mod = ThingCost.EngineMod;
                s.Asm = "Assembly-CSharp";
                s.Conf = 1f;
                return;
            }

            string mod = AttribIndex.ModOfDef(name);
            if (mod != null)
            {
                s.Mod = mod;
                s.Asm = "Assembly-CSharp";
                s.Conf = 1f;
                return;
            }

            // def 索引里没有（可能是运行时生成的 def 或 pawn 方面名）：退回程序集线索。
            s.Mod = "(unknown)";
            s.Asm = "?";
            s.Conf = 0.5f;
        }

        // ---------- 快照 ----------

        /// <summary>
        /// 取本窗口快照，按 cat 分组、每组按 selfMs 降序取前 perCatCap 条。
        /// windowTotalMs 是分母；返回的行已算好 pct 与 perInstMs。
        /// </summary>
        public static List<CostRow> Snapshot(int perCatCap, double windowTotalMs)
        {
            var rows = new List<CostRow>(64);
            if (windowTotalMs <= 0.0)
            {
                windowTotalMs = 0.0;
            }

            for (byte cat = 0; cat < CatCount; cat++)
            {
                _sortBuf.Clear();
                _keyBuf.Clear();
                foreach (KeyValuePair<ulong, Slot> kv in _slots)
                {
                    if (kv.Value.CatId != cat)
                    {
                        continue;
                    }
                    _sortBuf.Add(kv.Value);
                }
                if (_sortBuf.Count == 0)
                {
                    continue;
                }
                _sortBuf.Sort(CompareSlotDesc);

                int take = _sortBuf.Count < perCatCap ? _sortBuf.Count : perCatCap;
                for (int i = 0; i < take; i++)
                {
                    rows.Add(ToRow(_sortBuf[i], windowTotalMs));
                }
            }

            return rows;
        }

        /// <summary>取全会话累计快照（summary 附录用，可给更大上限）。</summary>
        public static List<CostRow> SessionSnapshot(int perCatCap)
        {
            var rows = new List<CostRow>(128);
            for (byte cat = 0; cat < CatCount; cat++)
            {
                _sortBuf.Clear();
                foreach (KeyValuePair<ulong, Slot> kv in _session)
                {
                    if (kv.Value.CatId != cat)
                    {
                        continue;
                    }
                    _sortBuf.Add(kv.Value);
                }
                if (_sortBuf.Count == 0)
                {
                    continue;
                }
                _sortBuf.Sort(CompareSlotDesc);
                int take = _sortBuf.Count < perCatCap ? _sortBuf.Count : perCatCap;
                for (int i = 0; i < take; i++)
                {
                    rows.Add(ToRow(_sortBuf[i], _sessionTotalMs));
                }
            }
            return rows;
        }

        private static int CompareSlotDesc(Slot a, Slot b)
        {
            return b.SelfMs.CompareTo(a.SelfMs);
        }

        private static CostRow ToRow(Slot s, double totalMs)
        {
            var r = new CostRow();
            r.Cat = CatNameOf(s.CatId);
            r.Name = s.Name;
            r.Mod = s.Mod;
            r.Asm = s.Asm;
            r.SelfMs = s.SelfMs;
            r.InclMs = s.InclMs;
            r.Pct = totalMs > 0.0 ? (s.SelfMs / totalMs * 100.0) : 0.0;
            r.Conf = s.Conf;

            if (s.CatId == CatPawnId)
            {
                r.Alive = AliveCounter.PawnCount;
                r.Dormant = 0;
            }
            else if (s.CatId == CatPawnInstId)
            {
                // 合并模式（动物 / 机械体）的分母是同类总数；个体模式恒为 1，每实例耗时就是该个体的总耗时。
                bool merged = (s.KeyLow & 0x80000000u) != 0u;
                r.Alive = merged ? AliveCounter.GetAlive(ThingCost.MakeKey(ThingCost.CatPawnInst, s.Name)) : 1;
                r.Dormant = 0;
            }
            else if (s.CatId == CatGlobalId)
            {
                // 全局系统没有「实例」概念，分母恒为 1，避免 summary 出现无意义的实例数列。
                r.Alive = 1;
                r.Dormant = 0;
            }
            else
            {
                string key = ThingCost.MakeKey(CatNameOf(s.CatId), s.Name);
                r.Alive = AliveCounter.GetAlive(key);
                r.Dormant = AliveCounter.GetDormant(key);
            }

            r.PerInstMs = r.Alive > 0 ? (s.SelfMs / r.Alive) : 0.0;
            // 有耗时但扫不到实例：item 类是正常现象（装备的 tick 发生在持有者 pawn 身上），
            // pawninst 曾经是缺陷（动物被 TAPI_Barn 移出 spawned 列表），现在第二源已补上。
            // 两种情况都标成「实例数未知」，不给出误导性的 0 分母。
            r.AliveKnown = r.Alive > 0 || s.SelfMs <= 0.0;
            FillSeriesStats(s, ref r);
            return r;
        }

        /// <summary>
        /// 从窗口级环形序列算 P50 / P95 / Max / 标准差。
        /// 样本按环形顺序拷到 scratch 再排序（回绕后起点不在 0，不能直接 Array.Sort 原数组）。
        /// 每次只在快照时跑，不在热路径。
        /// </summary>
        private static void FillSeriesStats(Slot s, ref CostRow r)
        {
            int n = s.SeriesCount;
            if (n <= 0 || s.Series == null)
            {
                return;
            }
            float[] src = s.Series;
            int start = n == SeriesCap ? s.SeriesHead : 0;
            for (int i = 0; i < n; i++)
            {
                _pctScratch[i] = src[(start + i) % SeriesCap];
            }
            Array.Sort(_pctScratch, 0, n);

            r.WinN = n;
            r.P50W = _pctScratch[(n - 1) / 2];
            r.P95W = _pctScratch[Math.Min(n - 1, (int)(n * 0.95))];
            r.MaxW = _pctScratch[n - 1];

            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                sum += _pctScratch[i];
            }
            double mean = sum / n;
            double var = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = _pctScratch[i] - mean;
                var += d * d;
            }
            r.SdW = n > 1 ? Math.Sqrt(var / (n - 1)) : 0.0;

            if (s.PeakWindowTotalMs > 0.0)
            {
                r.PctPeak = s.PeakSelfMs / s.PeakWindowTotalMs * 100.0;
            }
        }

        /// <summary>
        /// 按模组汇总 self / incl，生成 cat=mod 的行。由已归类的行加总，不再单独累加。
        /// 跳过 pawninst：它与 pawn 的方面行覆盖同一批 tick，两边都汇总会让 pawn 的耗时在 mod 行里翻倍。
        /// </summary>
        public static List<CostRow> AggregateByMod(List<CostRow> rows, double totalMs)
        {
            var acc = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                CostRow r = rows[i];
                if (string.Equals(r.Cat, ThingCost.CatPawnInst, StringComparison.Ordinal))
                {
                    continue;
                }
                // pawn 的十四个行里只有外壳层三项能参与合计：pawn_work / pawn_path / ...
                // 是 pawn_body 的内部细分，已经含在 pawn_body 里了。
                // 漏这一条会让「Ludeon.RimWorld」出现 113% 这种不可能的数（实测踩过）。
                if (string.Equals(r.Cat, ThingCost.CatPawn, StringComparison.Ordinal)
                    && !PawnCost.IsShell(r.Name))
                {
                    continue;
                }
                string m = string.IsNullOrEmpty(r.Mod) ? "(unknown)" : r.Mod;
                double[] v;
                if (!acc.TryGetValue(m, out v))
                {
                    v = new double[2];
                    acc[m] = v;
                }
                v[0] += r.SelfMs;
                v[1] += r.InclMs;
            }

            var outRows = new List<CostRow>(acc.Count);
            foreach (KeyValuePair<string, double[]> kv in acc)
            {
                var r = new CostRow();
                r.Cat = ThingCost.CatMod;
                r.Name = kv.Key;
                r.Mod = kv.Key;
                r.Asm = "?";
                r.SelfMs = kv.Value[0];
                r.InclMs = kv.Value[1];
                r.Pct = totalMs > 0.0 ? (kv.Value[0] / totalMs * 100.0) : 0.0;
                r.Alive = 0;
                r.Dormant = 0;
                r.PerInstMs = 0.0;
                r.Conf = 1f;
                outRows.Add(r);
            }
            outRows.Sort(delegate (CostRow a, CostRow b) { return b.SelfMs.CompareTo(a.SelfMs); });
            return outRows;
        }

        // ---------- 生命周期 ----------

        /// <summary>窗口结束：清窗口累加，会话累计保留。</summary>
        public static void EndWindow()
        {
            _slots.Clear();
            for (int i = 0; i < CatCount; i++)
            {
                _perCatCount[i] = 0;
                _overflow[i] = null;
            }
        }

        /// <summary>
        /// 关闭一个窗口：给每个已知桶追加一个窗口级样本，然后清窗口累加。
        /// 遍历的是会话字典而不是窗口字典 —— 本窗口没出现的桶必须补 0，
        /// 否则 P50W 会变成「出现过的窗口里的中值」，把经常为 0 的行系统性高估。
        /// 必须在 Snapshot 之后调用（样本值取自窗口字典）。
        /// </summary>
        public static void CloseWindow(double windowTotalMs)
        {
            _windowCount++;
            _lastWindowTotalMs = windowTotalMs;
            foreach (KeyValuePair<ulong, Slot> kv in _session)
            {
                Slot ss = kv.Value;
                Slot w;
                double sample = _slots.TryGetValue(kv.Key, out w) ? w.SelfMs : 0.0;
                if (ss.Series == null)
                {
                    ss.Series = new float[SeriesCap];
                }
                ss.Series[ss.SeriesHead] = (float)sample;
                ss.SeriesHead = (ss.SeriesHead + 1) % SeriesCap;
                if (ss.SeriesCount < SeriesCap)
                {
                    ss.SeriesCount++;
                }
                if (sample > ss.PeakSelfMs)
                {
                    ss.PeakSelfMs = sample;
                    ss.PeakWindowTotalMs = windowTotalMs;
                }
            }
            EndWindow();
        }

        /// <summary>会话结束：窗口与会话累计都清。</summary>
        public static void ResetSession()
        {
            EndWindow();
            _session.Clear();
            _sessionTotalMs = 0.0;
            _windowCount = 0;
            _lastWindowTotalMs = 0.0;
        }

        /// <summary>把本窗口总耗时累进会话分母。</summary>
        public static void AddWindowTotal(double windowTotalMs)
        {
            if (windowTotalMs > 0.0)
            {
                _sessionTotalMs += windowTotalMs;
            }
        }
    }
}
