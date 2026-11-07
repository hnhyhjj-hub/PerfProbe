namespace PerfProbe
{
    /// <summary>
    /// pawn 的十四方面分解桶。子 patch 把耗时投到对应方面，
    /// 未被任何锚点覆盖的剩余部分落到 pawn_other 兜底。
    ///
    /// 分三档：
    ///   外壳层（序号 12-13）—— Pawn.Tick 与 Pawn.TickInterval 这两个 protected override。
    ///     它们记 inclusive 时间（含内部全部子调用），是 residual 的唯一扣减项。
    ///   核心组（前六）—— 外壳层里最粗的几个大头。
    ///   扩展组（后六）—— 把原先全落进 pawn_other 的 tracker 逐个拆出来。
    ///
    /// 关键：外壳层与叶子层不能同时参与 residual 扣减。叶子层的耗时已经含在外壳层的
    /// inclusive 时间里，两者一起扣会让 residual 变成负数、十四桶之和小于 selfMs。
    /// 所以 PawnTickContext 只从 residual 里扣外壳层（见 IsShell），叶子层纯记录。
    ///
    /// 序号是稳定契约：CostTable 的键、summary 的表格顺序都依赖它，只能追加不能重排。
    /// </summary>
    public static class PawnCost
    {
        // ---- 核心组（序号 0-5，历史顺序，不可重排） ----
        public const string Think = "pawn_think";
        public const string Work = "pawn_work";
        public const string Path = "pawn_path";
        public const string State = "pawn_state";
        public const string Gear = "pawn_gear";
        public const string Other = "pawn_other";

        // ---- 扩展组（序号 6-11，把 Other 的残差拆开） ----
        public const string Gene = "pawn_gene";
        public const string Ability = "pawn_ability";
        public const string Mutant = "pawn_mutant";
        public const string Rare = "pawn_rare";
        public const string Render = "pawn_render";
        public const string Misc = "pawn_misc";

        // ---- 外壳层（序号 12-13，把 pawn_other 剩下的部分一分为二） ----
        /// <summary>Pawn.Tick 的 inclusive 时间：每 tick 都跑，不受任何降频 mod 影响。</summary>
        public const string Body = "pawn_body";

        /// <summary>Pawn.TickInterval 的 inclusive 时间：调用频率由 UpdateRateTicks 决定。</summary>
        public const string Interval = "pawn_interval";

        public static readonly string[] AllAspects =
        {
            Think, Work, Path, State, Gear, Other,
            Gene, Ability, Mutant, Rare, Render, Misc,
            Body, Interval
        };

        // 方面序号，必须与 AllAspects 下标一致（CostTable 用它拼成本键）。
        public const int IdThink = 0;
        public const int IdWork = 1;
        public const int IdPath = 2;
        public const int IdState = 3;
        public const int IdGear = 4;
        public const int IdOther = 5;
        public const int IdGene = 6;
        public const int IdAbility = 7;
        public const int IdMutant = 8;
        public const int IdRare = 9;
        public const int IdRender = 10;
        public const int IdMisc = 11;
        public const int IdBody = 12;
        public const int IdInterval = 13;

        /// <summary>方面总数。PawnTickContext 的累加数组按它开。</summary>
        public const int AspectCount = 14;

        /// <summary>
        /// 是否外壳层。外壳层三项（body / interval / other）相加等于 DoTick 总时间，
        /// 叶子层是 body 内部的细分说明。
        /// </summary>
        public static bool IsShell(int aspectId)
        {
            return aspectId == IdBody || aspectId == IdInterval || aspectId == IdOther;
        }

        /// <summary>按名字判断是否属于外壳层（summary 分表用）。</summary>
        public static bool IsShell(string aspect)
        {
            return aspect == Body || aspect == Interval || aspect == Other;
        }

        /// <summary>方面序号转方面名。序号必须与 AllAspects 一致。</summary>
        public static string NameOf(int aspectId)
        {
            if (aspectId < 0 || aspectId >= AllAspects.Length)
            {
                return Other;
            }
            return AllAspects[aspectId];
        }

        /// <summary>把方面名转成成本键（cat 固定为 pawn，name 用方面名）。</summary>
        public static string Key(string aspect)
        {
            return ThingCost.CatPawn + "|" + aspect;
        }

        public static string KeyThink => Key(Think);
        public static string KeyWork => Key(Work);
        public static string KeyPath => Key(Path);
        public static string KeyState => Key(State);
        public static string KeyGear => Key(Gear);
        public static string KeyOther => Key(Other);
        public static string KeyGene => Key(Gene);
        public static string KeyAbility => Key(Ability);
        public static string KeyMutant => Key(Mutant);
        public static string KeyRare => Key(Rare);
        public static string KeyRender => Key(Render);
        public static string KeyMisc => Key(Misc);
        public static string KeyBody => Key(Body);
        public static string KeyInterval => Key(Interval);

        /// <summary>方面名的中文可读标签，summary 用。</summary>
        public static string Label(string aspect)
        {
            switch (aspect)
            {
                case Think: return "思考决策";
                case Work: return "工作驱动";
                case Path: return "寻路";
                case State: return "状态效果与需求";
                case Gear: return "组件与装备";
                case Other: return "其它（未归因残差）";
                case Gene: return "基因";
                case Ability: return "能力";
                case Mutant: return "变异体";
                case Rare: return "低频 tick（每 250 刻）";
                case Render: return "渲染特化";
                case Misc: return "杂项（库存/绳索/飞行/原生动词/血雨）";
                case Body: return "Pawn.Tick 合计（每 tick 必跑）";
                case Interval: return "Pawn.TickInterval 合计（按间隔跑）";
                default: return aspect;
            }
        }

        /// <summary>是否属于扩展组（summary 分表用）。</summary>
        public static bool IsExtended(string aspect)
        {
            return aspect == Gene || aspect == Ability || aspect == Mutant
                   || aspect == Rare || aspect == Render || aspect == Misc;
        }
    }
}
