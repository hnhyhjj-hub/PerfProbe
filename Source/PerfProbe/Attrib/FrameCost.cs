namespace PerfProbe
{
    /// <summary>
    /// 帧阶段桶。tick 级归因回答「CPU 每 tick 花在哪」，这里回答「每帧的时间花在哪」——
    /// 两者是不同维度：一帧里可能跑 0 个到十几个 tick，帧率掉下来时可能一个 tick 都没多跑。
    ///
    /// 全部记 self 时间（DepthTracker 扣掉子段），因此各段相加 = 可测的帧内耗时，
    /// 与真实帧长的差额单独作为 unaccounted 输出 —— 那一块是引擎渲染、Present 与 GPU 等待，
    /// 是判断「CPU 侧还是 GPU 侧卡住」的关键依据。
    ///
    /// 序号是稳定契约：CostTable 的键依赖它，只能追加不能重排。
    /// </summary>
    public static class FrameCost
    {
        /// <summary>Root.Update：分辨率检查、长事件、UI 根、音效根等引擎侧每帧逻辑。</summary>
        public const string Engine = "f_engine";

        /// <summary>Game.UpdatePlay 自身的剩余部分（扣掉 tick / world / map 三个子段）。</summary>
        public const string Play = "f_play";

        /// <summary>TickManager.TickManagerUpdate：本帧跑的所有 tick 的总耗时。</summary>
        public const string Tick = "f_tick";

        /// <summary>World.WorldUpdate：世界地图侧每帧逻辑。</summary>
        public const string World = "f_world";

        /// <summary>Map.MapUpdate 的非绘制部分（天空、电网、区域、辉光、领主、后处理视觉等）。</summary>
        public const string MapUpdate = "f_map";

        /// <summary>绘制前准备：全局 shader 参数、落叶色、水面贴图。</summary>
        public const string DrawPrep = "f_draw_prep";

        /// <summary>地图网格：MapMeshDrawerUpdate_First + DrawMapMesh（静态地形与建筑网格）。</summary>
        public const string DrawMesh = "f_draw_mesh";

        /// <summary>动态物件：DynamicDrawManager.DrawDynamicThings（小人、动物、机械体）。</summary>
        public const string DrawDynamic = "f_draw_dyn";

        /// <summary>叠加层：区域染色、命令点、飘字、污物、临时物件、气候特效。</summary>
        public const string DrawOverlay = "f_draw_ovl";

        /// <summary>Root.OnGUI：IMGUI 阶段（原版 UI、窗口、浮动菜单）。</summary>
        public const string Gui = "f_gui";

        public static readonly string[] AllStages =
        {
            Engine, Play, Tick, World, MapUpdate,
            DrawPrep, DrawMesh, DrawDynamic, DrawOverlay, Gui
        };

        public const int IdEngine = 0;
        public const int IdPlay = 1;
        public const int IdTick = 2;
        public const int IdWorld = 3;
        public const int IdMapUpdate = 4;
        public const int IdDrawPrep = 5;
        public const int IdDrawMesh = 6;
        public const int IdDrawDynamic = 7;
        public const int IdDrawOverlay = 8;
        public const int IdGui = 9;

        public const int Count = 10;

        public static string NameOf(int id)
        {
            if (id < 0 || id >= AllStages.Length)
            {
                return "?";
            }
            return AllStages[id];
        }

        public static string Label(string name)
        {
            switch (name)
            {
                case Engine: return "引擎每帧（Root.Update）";
                case Play: return "游戏逻辑其余（Game.UpdatePlay 自段）";
                case Tick: return "全部 tick（TickManagerUpdate）";
                case World: return "世界地图（WorldUpdate）";
                case MapUpdate: return "地图每帧非绘制（MapUpdate 自段）";
                case DrawPrep: return "绘制前准备（shader/水面/落叶）";
                case DrawMesh: return "地形建筑网格（MapMesh）";
                case DrawDynamic: return "动态物件（DrawDynamicThings）";
                case DrawOverlay: return "叠加层（区域/飘字/特效）";
                case Gui: return "界面 IMGUI（Root.OnGUI）";
                default: return name;
            }
        }

        /// <summary>
        /// 这一段是不是渲染工作。summary 用它把「CPU 逻辑」与「渲染」分开汇总，
        /// 判据是 §11.7：渲染段占比高且帧率低于 30 才是 GPU 卡口。
        /// </summary>
        public static bool IsRender(int id)
        {
            return id == IdDrawPrep || id == IdDrawMesh || id == IdDrawDynamic || id == IdDrawOverlay;
        }
    }
}
