namespace PerfProbe
{
    /// <summary>
    /// 全局系统桶。这些逻辑在 TickManager.DoSingleTick 里直接执行，
    /// 不属于任何 Thing / MapComponent，所以原先全部混在「未归因残差」里看不见。
    ///
    /// 全部记 self 时间（不是 inclusive）：DepthTracker 会把这棵子树里任何已捕获的子帧扣掉，
    /// 例如 TickList.Tick 内部成千上万个 Thing.DoTick 会被扣走，只留下容器本身的遍历开销
    /// —— 这正是我们想看的那个数，且保证与 building / pawn 等类不重叠。
    ///
    /// 序号是稳定契约：CostTable 的键依赖它，只能追加不能重排。
    /// </summary>
    public static class GlobalCost
    {
        public const string MapPreTick = "map_pre_tick";
        public const string MapPostTick = "map_post_tick";
        public const string TickList = "ticklist";
        public const string DateNotifier = "date_notifier";
        public const string Scenario = "scenario";
        public const string WorldTick = "world_tick";
        public const string WorldPostTick = "world_post_tick";
        public const string StoryWatcher = "story_watcher";
        public const string GameEnder = "game_ender";
        public const string Storyteller = "storyteller";
        public const string TaleManager = "tale_manager";
        public const string QuestManager = "quest_manager";
        public const string History = "history";
        public const string GameComponent = "game_component";
        public const string LetterStack = "letter_stack";
        public const string Autosaver = "autosaver";
        public const string FilthMonitor = "filth_monitor";
        public const string TransportShips = "transport_ships";

        public static readonly string[] AllGlobals =
        {
            MapPreTick, MapPostTick, TickList,
            DateNotifier, Scenario, WorldTick, WorldPostTick,
            StoryWatcher, GameEnder, Storyteller, TaleManager, QuestManager, History,
            GameComponent, LetterStack, Autosaver, FilthMonitor, TransportShips
        };

        public const int IdMapPreTick = 0;
        public const int IdMapPostTick = 1;
        public const int IdTickList = 2;
        public const int IdDateNotifier = 3;
        public const int IdScenario = 4;
        public const int IdWorldTick = 5;
        public const int IdWorldPostTick = 6;
        public const int IdStoryWatcher = 7;
        public const int IdGameEnder = 8;
        public const int IdStoryteller = 9;
        public const int IdTaleManager = 10;
        public const int IdQuestManager = 11;
        public const int IdHistory = 12;
        public const int IdGameComponent = 13;
        public const int IdLetterStack = 14;
        public const int IdAutosaver = 15;
        public const int IdFilthMonitor = 16;
        public const int IdTransportShips = 17;

        public const int Count = 18;

        public static string NameOf(int id)
        {
            if (id < 0 || id >= AllGlobals.Length)
            {
                return "?";
            }
            return AllGlobals[id];
        }

        public static string Label(string name)
        {
            switch (name)
            {
                case MapPreTick: return "地图 tick 前段（MapPreTick）";
                case MapPostTick: return "地图 tick 后段（MapPostTick）";
                case TickList: return "tick 列表容器（TickList.Tick）";
                case DateNotifier: return "日期通知（DateNotifier）";
                case Scenario: return "剧本（Scenario）";
                case WorldTick: return "世界 tick（World.WorldTick）";
                case WorldPostTick: return "世界 tick 后段（World.WorldPostTick）";
                case StoryWatcher: return "故事观察器（StoryWatcher）";
                case GameEnder: return "游戏结束判定（GameEnder）";
                case Storyteller: return "叙事者（Storyteller）";
                case TaleManager: return "故事记录（TaleManager）";
                case QuestManager: return "任务系统（QuestManager）";
                case History: return "历史统计（History）";
                case GameComponent: return "游戏组件（GameComponent 全体）";
                case LetterStack: return "信件栈（LetterStack）";
                case Autosaver: return "自动存档（Autosaver）";
                case FilthMonitor: return "污物监控（FilthMonitor）";
                case TransportShips: return "运输船（TransportShipManager）";
                default: return name;
            }
        }
    }
}
