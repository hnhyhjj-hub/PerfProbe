using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 采集总控。所有状态集中在此，Patch 层只做一件事：
    /// 进函数时 Enter、出函数时 Exit 并把 self 时间交给这里。
    /// 生命周期由 TickManager.DoSingleTick 的每次调用驱动。
    /// </summary>
    public static class ProbeEngine
    {
        /// <summary>
        /// jsonl 每窗口每类输出的行数上限。只影响 jsonl：它的用途是看趋势与榜首，
        /// 不是算总量（各类 selfMs 求和永远小于真实总量）。
        /// summary.md 走 SessionSnapshot，另有自己的上限。
        /// </summary>
        private const int TopPerCatJsonl = 32;

        /// <summary>
        /// summary.md 每类的行数上限。必须 ≥ CostTable.PerCatCap，否则桶建了却被输出截掉，
        /// 「按类合计」会少算。这里取与 PerCatCap 相同的值。
        /// </summary>
        private const int SessionRowCap = CostTable.PerCatCap;

        private static ProbeSettings _settings;
        private static ProbeSession _session;
        private static Game _lastGame;
        private static bool _armed;
        private static int _patchedTypes;
        private static long _lastWindowTs;
        private static long _tickStartTs;
        private static bool _tickOpen;
        private static bool _tickCaptured;
        private static double _windowMs;
        private static int _windowTicks;
        private static double _windowGapMs;
        private static int _gc0;
        private static int _gc1;
        private static int _gc2;
        private static int _seq;
        private static bool _ending;

        // ---------------- 探针自身开销 ----------------
        //
        // 不在每个计时点上再加一对 Qpc.Now()（那会让测量本身更贵，且测不准），
        // 改用「事件计数 × 单次标定成本」：
        //   1. 每个计时点只做一次 int 自增（纳秒级，可忽略）；
        //   2. 会话开始时跑一次标定，量出「一次 Enter+Exit 对」的真实成本；
        //   3. 窗口边界算 overhead = events × perPairMs。
        // 这样开销估计是 O(1) 的，且不会污染被测数据。

        /// <summary>本窗口累计的探针计时事件数（Enter+Exit 记 1）。</summary>
        private static long _probeEvents;

        /// <summary>会话累计的探针计时事件数。</summary>
        private static long _sessionProbeEvents;

        /// <summary>标定值：一次 Enter+Exit 对的毫秒成本。</summary>
        private static double _perPairMs;

        /// <summary>标定是否成功。失败时 summary 会说明「未标定」，而不是给一个假数字。</summary>
        private static bool _calibrated;

        public static bool Calibrated => _calibrated;

        /// <summary>本窗口探针自身开销（毫秒）。</summary>
        public static double ProbeOverheadMs => _probeEvents * _perPairMs;

        /// <summary>全会话探针自身开销（毫秒）。</summary>
        public static double SessionProbeOverheadMs => _sessionProbeEvents * _perPairMs;

        /// <summary>全会话探针计时事件数。</summary>
        public static long SessionProbeEvents => _sessionProbeEvents;

        /// <summary>本窗口探针事件数。</summary>
        public static long ProbeEvents => _probeEvents;

        /// <summary>标定出的单次 Enter+Exit 成本（毫秒）。</summary>
        public static double PerPairMs => _perPairMs;

        /// <summary>每个计时点调一次，只做一次自增。</summary>
        public static void NoteProbeEvent()
        {
            _probeEvents++;
        }

        // ---------------- 探针框架开销（抽样实测） ----------------
        //
        // 上面的标定只覆盖 DepthTracker.Enter/Exit 本身，不包含 Harmony 委托跳转、
        // TickActive 属性读、AddThing 里的 Classify 与 CostTable 双字典查找。
        // 这些每 tick 要走几千次，粗估比标定值还大，必须单独量。
        //
        // 做法：Patch_Thing_DoTick 每 64 个对象挑 1 个，在 Prefix/Postfix 各自的入口与出口
        // 取时间戳，把差值乘 64 累加。抽样开销约 6 次 Stopwatch/tick，可忽略。

        /// <summary>本窗口抽样实测的框架成本（毫秒，已外推）。</summary>
        private static double _frameCostMs;

        /// <summary>会话累计抽样实测的框架成本（毫秒，已外推）。</summary>
        private static double _sessionFrameCostMs;

        /// <summary>
        /// 全会话「被采集窗口的墙钟跨度」累加（秒）。
        /// TPS 的分母必须用它，不能用会话总时长：用户打开设置面板（forcePause）或读档时，
        /// 会话总时长照走但一个 tick 都不采集，用总时长算出的 TPS 会被系统性拉低。
        /// </summary>
        private static double _sessionCapturedSec;

        public static double SessionCapturedSec => _sessionCapturedSec;

        /// <summary>按采集时段算的 TPS。会话还没落过窗口时退回 0。</summary>
        public static double SessionTps
        {
            get
            {
                if (_sessionCapturedSec <= 0.0)
                {
                    return 0.0;
                }
                return FrameProbe.SessionTicks / _sessionCapturedSec;
            }
        }

        /// <summary>本窗口被抽中的样本数。</summary>
        private static long _frameSamples;

        /// <summary>会话累计被抽中的样本数。</summary>
        private static long _sessionFrameSamples;

        public static double FrameCostMs => _frameCostMs;

        public static double SessionFrameCostMs => _sessionFrameCostMs;

        public static long FrameSamples => _frameSamples;

        public static long SessionFrameSamples => _sessionFrameSamples;

        /// <summary>
        /// 记一次抽样到的框架成本。ms 是该样本的实测耗时，scale 是抽样分母（1/scale 抽样 → 乘 scale 外推）。
        /// </summary>
        public static void NoteFrameCost(double ms, int scale)
        {
            if (ms <= 0.0 || scale <= 0)
            {
                return;
            }
            _frameCostMs += ms * scale;
            _frameSamples++;
        }

        /// <summary>窗口关闭时把本窗口框架成本结转到会话累计。</summary>
        private static void CommitFrameCost()
        {
            _sessionFrameCostMs += _frameCostMs;
            _sessionFrameSamples += _frameSamples;
            _frameCostMs = 0.0;
            _frameSamples = 0;
        }

        /// <summary>
        /// 标定一次 Enter+Exit 对的成本。用真实的热路径（DepthTracker）跑 1000 次取均值，
        /// 且在会话开始时调用一次即可 —— 此时还没有任何待测数据，标定本身不影响结果。
        /// </summary>
        private static void CalibrateOverhead()
        {
            _perPairMs = 0.0;
            _calibrated = false;
            try
            {
                const int N = 1000;
                // 预热，避开 JIT 与缓存冷启动。
                for (int i = 0; i < 64; i++)
                {
                    if (DepthTracker.Enter())
                    {
                        DepthTracker.Exit(true);
                    }
                }
                long t0 = Qpc.Now();
                for (int i = 0; i < N; i++)
                {
                    if (DepthTracker.Enter())
                    {
                        DepthTracker.Exit(true);
                    }
                }
                double total = Qpc.ToMs(Qpc.Now() - t0);
                _perPairMs = total / N;
                _calibrated = _perPairMs > 0.0;
            }
            catch
            {
                _perPairMs = 0.0;
                _calibrated = false;
            }
        }

        public static int PhaseTicks { get; private set; } = 32;

        public static int PatchedTypes => _patchedTypes;

        public static bool Collecting => _armed && _session != null;

        /// <summary>本次 DoSingleTick 是否处于采集中。所有子 patch 靠它决定要不要记账。</summary>
        public static bool TickActive { get; private set; }

        /// <summary>
        /// 本帧是否处于采集中。帧阶段与 tick 是两套口径：帧在暂停时也在跑，
        /// 而 tick 只在速度档符合时才采，所以不能复用 TickActive。
        /// 帧级采集跟着会话开关走，不看速度档 —— 掉帧问题在暂停时同样要看。
        /// </summary>
        public static bool FrameShouldCapture
        {
            get { return _armed && _session != null && _settings != null && _settings.Enabled; }
        }

        public static ProbeSession Session => _session;

        public static ProbeSettings Settings => _settings;

        public static void Init(ProbeSettings s)
        {
            _settings = s;
            PhaseTicks = s != null ? s.PhaseTicks : 32;
        }

        /// <summary>设置变更后重算抽样门与相位周期。</summary>
        public static void ApplySettings()
        {
            if (_settings == null)
            {
                return;
            }
            _settings.ClampAll();
            PhaseTicks = _settings.PhaseTicks;
            SampleGate.Configure(_settings.SampleK, _settings.PhaseTicks);
            if (!_settings.Enabled && _session != null)
            {
                EndSession("设置中关闭采集");
            }
        }

        /// <summary>由 patch 层在挂载完成后回报，只用于附录展示。</summary>
        public static void SetPatchedTypes(int n)
        {
            _patchedTypes = n;
        }

        // ---------------- 会话生命周期 ----------------

        /// <summary>游戏切换（读档/新开局）时切新会话。由 tick 驱动，不依赖读档 patch。</summary>
        private static void SyncGame()
        {
            Game g;
            try
            {
                g = Current.Game;
            }
            catch
            {
                g = null;
            }
            if (ReferenceEquals(g, _lastGame))
            {
                return;
            }
            _lastGame = g;
            if (_session != null)
            {
                EndSession("游戏切换（读档或新开局）");
            }
            if (g != null && _settings != null && _settings.Enabled)
            {
                StartSession(ProbeMode.Passive);
            }
        }

        public static bool StartSession(ProbeMode mode)
        {
            if (_settings == null)
            {
                PLog.Warn("设置未初始化，无法开始采集");
                return false;
            }
            if (_session != null)
            {
                EndSession("切换到新模式");
            }

            string dir = ProbePaths.ActiveDir;
            if (string.IsNullOrEmpty(dir))
            {
                PLog.Warn("输出目录不可用，本次不启动采集");
                return false;
            }

            var s = new ProbeSession();
            s.Mode = mode;
            s.StartLocal = DateTime.Now;
            s.StartUtc = DateTime.UtcNow;
            s.StartTs = Qpc.Now();
            try
            {
                s.StartGameTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            }
            catch
            {
                s.StartGameTick = 0;
            }
            _seq++;
            s.BaseName = Retention.MakeSessionBaseName(mode, s.StartLocal, _seq);

            string err;
            if (!s.OpenFiles(dir, out err))
            {
                PLog.Err("打开 jsonl 失败，本次不落盘: " + err);
                return false;
            }

            SchemaWriter.Write(dir);
            GuideWriter.Write(dir);
            FrameProbe.ResetSession();
            FrameStats.ResetSession();
            CostTable.ResetSession();
            AliveCounter.Rebuild();
            SampleGate.Reset();
            DepthTracker.Reset();
            PawnTickContext.Reset();
            PawnIdentityTable.Reset();
            CalibrateOverhead();
            _sessionProbeEvents = 0;
            _probeEvents = 0;
            _frameCostMs = 0.0;
            _sessionFrameCostMs = 0.0;
            _frameSamples = 0;
            _sessionFrameSamples = 0;
            _sessionCapturedSec = 0.0;
            ResetWindowCounters();
            _lastWindowTs = Qpc.Now();
            _session = s;
            _armed = true;

            PLog.Msg("会话已开始（" + mode.ToTag() + "）: " + s.JsonlPath
                     + "，探针单次标定 " + (_calibrated
                         ? (_perPairMs * 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " 微秒"
                         : "失败（开销不单列）"));
            return true;
        }

        public static void EndSession(string reason)
        {
            ProbeSession s = _session;
            if (s == null)
            {
                _armed = false;
                return;
            }
            if (_ending)
            {
                return;
            }
            _ending = true;
            try
            {
                EndSessionCore(s, reason);
            }
            finally
            {
                _ending = false;
            }
        }

        private static void EndSessionCore(ProbeSession s, string reason)
        {
            // 必须先冲刷最后一窗口再清 _session：FlushWindow 依赖 _session 非空。
            try
            {
                FlushWindow(true);
            }
            catch (Exception e)
            {
                PLog.Warn("会话结束时刷新最后一窗口失败: " + e.Message);
            }

            _session = null;
            _armed = false;

            List<CostRow> rows = CostTable.SessionSnapshot(SessionRowCap);
            List<CostRow> modRows = CostTable.AggregateByMod(rows, CostTable.SessionTotalMs);
            double totalMs = CostTable.SessionTotalMs;

            s.CloseFiles();

            string dir = ProbePaths.ActiveDir;
            List<string> deleted = null;
            if (_settings != null && _settings.AutoCleanup && !string.IsNullOrEmpty(dir))
            {
                deleted = Retention.Enforce(dir, _settings.KeepSessions);
            }

            string summaryPath = SummaryWriter.Write(s, rows, modRows, totalMs, deleted, _patchedTypes);
            if (!string.IsNullOrEmpty(summaryPath))
            {
                PLog.Msg("会话已结束（" + reason + "），汇总: " + summaryPath);
            }

            // brief.md 是给网页版 / 纯对话 AI 的精简版。写失败不影响 summary 已落盘。
            string briefPath = SummaryWriter.WriteBrief(s, rows, modRows, totalMs, _patchedTypes);
            if (!string.IsNullOrEmpty(briefPath))
            {
                PLog.Msg("精简报告: " + briefPath);
            }

            if (!string.IsNullOrEmpty(dir))
            {
                List<SessionRecord> records = IndexWriter.Scan(dir);
                IndexWriter.Write(dir, records, _settings != null ? _settings.KeepSessions : 20, deleted);
                PLog.Msg("输出目录: " + dir);
            }
        }

        // ---------------- tick 驱动 ----------------

        /// <summary>TickManager.DoSingleTick 的入口。返回 false 表示本次不采（速度档不符等）。</summary>
        public static bool BeginTick()
        {
            TickActive = false;
            _tickCaptured = false;
            _tickOpen = true;
            _tickStartTs = Qpc.Now();
            if (_settings == null)
            {
                return false;
            }
            if (!ReferenceEquals(Current.Game, _lastGame))
            {
                SyncGame();
            }
            ConsumeRequests();

            ProbeSession s = _session;
            if (s == null || !_armed)
            {
                return false;
            }
            SpeedKind k = SpeedBucket.Current();
            s.RecordSpeed(k);
            if (!SpeedBucket.ShouldCapture(_settings))
            {
                return false;
            }
            SampleGate.Tick();
            TickActive = true;
            _tickCaptured = true;
            return true;
        }

        /// <summary>
        /// TickManager.DoSingleTick 的出口。开合标记在 BeginTick 里已写好，
        /// 因此这里不需要 __state：postfix 与 finalizer 都能安全地重复调用。
        /// </summary>
        public static void FinishTick()
        {
            if (!_tickOpen)
            {
                return;
            }
            _tickOpen = false;
            bool captured = _tickCaptured;
            _tickCaptured = false;
            TickActive = false;
            double ms = Qpc.ToMs(Qpc.Now() - _tickStartTs);
            if (captured)
            {
                EndTick(ms);
            }
            else
            {
                EndTickUncaptured(ms);
            }
        }

        private static void ConsumeRequests()
        {
            if (_settings == null)
            {
                return;
            }
            if (_settings.StopRequested)
            {
                _settings.StopRequested = false;
                EndSession("用户手动停止");
            }
            if (_settings.StartPassiveRequested)
            {
                _settings.StartPassiveRequested = false;
                if (string.IsNullOrEmpty(ProbePaths.ActiveDir))
                {
                    ProbePaths.Resolve(_settings.OutputDir);
                }
                StartSession(ProbeMode.Passive);
            }
        }

        /// <summary>
        /// 帧级请求消费，由 Patch_Root_Update 每帧调用。
        /// 暂停时 DoSingleTick 不执行，因此 tick 路径上的 ConsumeRequests 永远不会被触发；
        /// 这一条是唯一的兜底，让"暂停中点的按钮"也能生效。
        /// 与 tick 路径共用同一组标志位，重复消费是幂等的（标志读完即清）。
        /// </summary>
        public static void ConsumeRequestsAtFrameLevel()
        {
            if (_settings == null)
            {
                return;
            }
            if (!_settings.StopRequested && !_settings.StartPassiveRequested)
            {
                return;
            }
            ConsumeRequests();
        }

        /// <summary>本次 tick 结束。ms 是整个 DoSingleTick 的墙钟耗时。</summary>
        public static void EndTick(double ms)
        {
            ProbeSession s = _session;
            if (s == null)
            {
                return;
            }
            _windowMs += ms;
            _windowTicks++;
            FrameProbe.AddTick(ms, true);
            FrameStats.MarkTick();

            long now = Qpc.Now();
            double sinceMs = Qpc.ToMs(now - _lastWindowTs);
            double winMs = (_settings != null ? _settings.WindowSec : 5) * 1000.0;
            if (sinceMs >= winMs)
            {
                FlushWindow(false);
            }
        }

        /// <summary>未被采集的 tick 也要记覆盖缺口，保证缺口口径完整。</summary>
        public static void EndTickUncaptured(double ms)
        {
            if (_session == null)
            {
                return;
            }
            _windowGapMs += ms;
            FrameProbe.AddTick(ms, false);
        }

        private static void ResetWindowCounters()
        {
            _windowMs = 0.0;
            _windowTicks = 0;
            _windowGapMs = 0.0;
            try
            {
                _gc0 = GC.CollectionCount(0);
                _gc1 = GC.CollectionCount(1);
                _gc2 = GC.CollectionCount(2);
            }
            catch
            {
                _gc0 = _gc1 = _gc2 = 0;
            }
        }

        // ---------------- 窗口落盘 ----------------

        private static void FlushWindow(bool last)
        {
            ProbeSession s = _session;
            if (s == null)
            {
                return;
            }

            double winSec = (_settings != null ? _settings.WindowSec : 5);
            double windowTotalMs = _windowMs;
            int windowTicks = _windowTicks;
            // 本窗口的真实跨度（秒）。不能用配置的 WindowSec：暂停、卡帧、读档都会让它失真，
            // 而 TPS 的分母必须是「这段墙钟时间」，否则暂停一次就把整轮 TPS 拉低。
            double winActualSec = Qpc.ToMs(Qpc.Now() - _lastWindowTs) / 1000.0;
            if (winActualSec <= 0.0)
            {
                winActualSec = winSec;
            }
            if (windowTicks <= 0 && !last)
            {
                _lastWindowTs = Qpc.Now();
                _probeEvents = 0;
                CommitFrameCost();
                // 没有 tick 不代表没有帧：暂停时帧照跑。这里刻意不结帧窗口，
                // 让这段帧数据滚进下一个有 tick 的窗口，而不是丢弃或单独成一窗。
                ResetWindowCounters();
                return;
            }

            AliveCounter.Rebuild();
            List<CostRow> rows = CostTable.Snapshot(TopPerCatJsonl, windowTotalMs);
            List<CostRow> modRows = CostTable.AggregateByMod(rows, windowTotalMs);
            CostTable.AddWindowTotal(windowTotalMs);

            // 探针开销分两部分：标定出的计时成本，与抽样实测出的框架成本。
            double probeMs = ProbeOverheadMs;
            double frameMs = _frameCostMs;
            _sessionProbeEvents += _probeEvents;
            _sessionCapturedSec += winActualSec;
            CommitFrameCost();

            int g0, g1, g2;
            try
            {
                g0 = GC.CollectionCount(0) - _gc0;
                g1 = GC.CollectionCount(1) - _gc1;
                g2 = GC.CollectionCount(2) - _gc2;
            }
            catch
            {
                g0 = g1 = g2 = 0;
            }

            string line = BuildJsonLine(s, rows, modRows, windowTotalMs, windowTicks, winSec, winActualSec, g0, g1, g2, probeMs, frameMs);
            s.Writer.Enqueue(line);
            s.Lines++;

            double msPerTick = windowTicks > 0 ? windowTotalMs / windowTicks : 0.0;
            PLog.Msg("窗口落盘 t=" + s.ElapsedSec.ToString("0.0", CultureInfo.InvariantCulture)
                     + "s msPerTick=" + msPerTick.ToString("0.0000", CultureInfo.InvariantCulture)
                     + " rows=" + rows.Count);

            _lastWindowTs = Qpc.Now();
            _windowMs = 0.0;
            _windowTicks = 0;
            _windowGapMs = 0.0;
            _probeEvents = 0;
            ResetWindowCounters();
            // 必须在 Snapshot 之后：样本值取自窗口字典；且用会话字典遍历以补 0。
            CostTable.CloseWindow(windowTotalMs);
            // 帧阶段同理：jsonl 已取走本窗口数值，这里结转到会话累计并清零。
            FrameStats.CloseWindow();
        }

        private static string BuildJsonLine(ProbeSession s, List<CostRow> rows, List<CostRow> modRows,
                                            double windowTotalMs, int windowTicks, double winSec,
                                            double winActualSec, int g0, int g1, int g2,
                                            double probeMs, double frameMs)
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');

            Json.KeyNum(sb, "t", s.ElapsedSec);
            sb.Append(',');
            Json.KeyInt(sb, "gameTick", GameTick());
            sb.Append(',');
            Json.KeyNum(sb, "gameDay", GameDay());
            sb.Append(',');
            Json.KeyNum(sb, "win", winSec);
            sb.Append(',');
            Json.KeyNum(sb, "winActual", winActualSec);
            sb.Append(',');
            Json.KeyStr(sb, "mode", s.Mode.ToTag());
            sb.Append(',');
            Json.KeyStr(sb, "speed", SpeedBucket.Tag(SpeedBucket.Current()));
            sb.Append(',');
            Json.KeyNum(sb, "tps", winActualSec > 0.0 ? windowTicks / winActualSec : 0.0);
            sb.Append(',');
            Json.KeyNum(sb, "msPerTick", windowTicks > 0 ? windowTotalMs / windowTicks : 0.0);
            sb.Append(',');
            Json.KeyNum(sb, "msPerTickP95", FrameProbe.WindowP95());
            sb.Append(',');
            Json.KeyNum(sb, "frameMs", Root.AverageFrameTime);
            sb.Append(',');
            Json.KeyInt(sb, "gc0", g0);
            sb.Append(',');
            Json.KeyInt(sb, "gc1", g1);
            sb.Append(',');
            Json.KeyInt(sb, "gc2", g2);
            sb.Append(',');
            Json.KeyInt(sb, "dropLines", s.Writer.Dropped);
            sb.Append(',');
            Json.KeyNum(sb, "gapMs", _windowGapMs);
            sb.Append(',');
            Json.KeyInt(sb, "sampleK", SampleGate.K);
            sb.Append(',');
            Json.KeyNum(sb, "probeMs", probeMs);
            sb.Append(',');
            Json.KeyNum(sb, "frameCostMs", frameMs);
            sb.Append(',');

            AppendFrameBlock(sb, winActualSec);

            sb.Append("\"rows\":[");
            bool first = true;
            AppendRows(sb, rows, ref first);
            AppendRows(sb, modRows, ref first);
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 帧级数据块。与 rows[] 平级但独立：rows 的分母是 tick 总耗时，这里的分母是帧数，
        /// 混在一起会让两边占比都失真，所以分成两个对象。
        /// unaccounted 是帧长减去所有已测阶段 —— 引擎渲染、Present、显存等待都在这里，
        /// 它是判断「CPU 卡还是 GPU 卡」的唯一依据。
        /// </summary>
        private static void AppendFrameBlock(StringBuilder sb, double winActualSec)
        {
            double fps = FrameStats.WinFps();
            double avg = FrameStats.WinAvgFrameMs();
            Json.KeyNum(sb, "fps", fps);
            sb.Append(',');
            Json.KeyNum(sb, "frameP50Ms", FrameStats.WinFrameP50());
            sb.Append(',');
            Json.KeyNum(sb, "frameP95Ms", FrameStats.WinFrameP95());
            sb.Append(',');
            Json.KeyInt(sb, "frames", FrameStats.WinFrames);
            sb.Append(',');
            Json.KeyInt(sb, "idleFrames", FrameStats.WinIdleFrames);
            sb.Append(',');
            Json.KeyNum(sb, "frameAvgMs", avg);
            sb.Append(',');
            Json.KeyNum(sb, "frameUnaccMs", FrameStats.WinUnaccountedMs());
            sb.Append(',');
            Json.KeyNum(sb, "ticksPerFrame", FrameStats.WinFrames > 0
                ? (double)_windowTicks / FrameStats.WinFrames
                : 0.0);
            sb.Append(',');

            // 阶段数组：只输出非零段，按耗时降序，避免每窗口固定 10 行空数据。
            sb.Append("\"fstages\":[");
            List<FrameStageRow> rows = FrameStats.WinRows();
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                FrameStageRow r = rows[i];
                sb.Append('{');
                Json.KeyStr(sb, "s", FrameCost.NameOf(r.Id));
                sb.Append(',');
                Json.KeyNum(sb, "ms", r.SelfMs);
                sb.Append(',');
                Json.KeyNum(sb, "msPerFrame", r.MsPerFrame);
                sb.Append(',');
                Json.KeyNum(sb, "pct", r.Pct);
                sb.Append('}');
            }
            sb.Append("],");
        }

        private static void AppendRows(StringBuilder sb, List<CostRow> rows, ref bool first)
        {
            if (rows == null)
            {
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                CostRow r = rows[i];
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                sb.Append('{');
                Json.KeyStr(sb, "cat", r.Cat);
                sb.Append(',');
                Json.KeyStr(sb, "name", r.Name);
                sb.Append(',');
                Json.KeyStr(sb, "mod", r.Mod);
                sb.Append(',');
                Json.KeyStr(sb, "asm", r.Asm);
                sb.Append(',');
                Json.KeyNum(sb, "selfMs", r.SelfMs);
                sb.Append(',');
                Json.KeyNum(sb, "inclMs", r.InclMs);
                sb.Append(',');
                Json.KeyNum(sb, "pct", r.Pct);
                sb.Append(',');
                Json.KeyInt(sb, "n", r.Alive);
                sb.Append(',');
                Json.KeyInt(sb, "dormant", r.Dormant);
                sb.Append(',');
                Json.KeyNum(sb, "perInstMs", r.PerInstMs);
                sb.Append(',');
                Json.KeyNum(sb, "conf", (double)r.Conf);
                sb.Append('}');
            }
        }

        private static long GameTick()
        {
            try
            {
                return Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double GameDay()
        {
            try
            {
                return Find.TickManager != null ? Find.TickManager.TicksGame / 60000.0 : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        // ---------------- 归因累加入口（热路径） ----------------

        /// <summary>thing 级累加。catId 由调用方算好，name 只在建桶时用。</summary>
        public static void AddThing(Thing t, double selfMs, double inclMs)
        {
            if (t == null || t.def == null)
            {
                return;
            }
            string cat = ThingCost.Classify(t);
            byte catId = CostTable.CatIdOf(cat);
            ulong key = CostTable.KeyForThing(catId, t.def.defNameHash);
            CostTable.Add(key, catId, t.def.defName, selfMs, inclMs);
        }

        /// <summary>pawn 方面累加。aspectId 与 PawnCost.AllAspects 下标一致。</summary>
        public static void AddPawnAspect(int aspectId, string aspectName, double selfMs, double inclMs)
        {
            if (aspectId < 0)
            {
                return;
            }
            CostTable.AddPawnAspect(aspectId, aspectName, selfMs, inclMs);
        }

        /// <summary>pawn 个体 / 种族行累加。键与归因由 PawnIdentityTable 算好传进来。</summary>
        public static void AddPawnInstance(ulong key, string name, string mod, string asm,
                                           float conf, double selfMs, double inclMs)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            CostTable.AddPawnInstance(key, name, mod, asm, conf, selfMs, inclMs);
        }

        /// <summary>MapComponent 子类累加。类型名与归因在 patch 侧算好传进来。</summary>
        public static void AddComponent(string typeName, string mod, string asm, float conf,
                                        double selfMs, double inclMs)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return;
            }
            CostTable.AddComponent(typeName, mod, asm, conf, selfMs, inclMs);
        }

        /// <summary>事件累加（M2 用，本版本不挂 patch，接口先留好）。</summary>
        public static void AddIncident(string defName, double selfMs, double inclMs)
        {
            if (string.IsNullOrEmpty(defName))
            {
                return;
            }
            CostTable.AddIncident(defName, AttribIndex.ModOfDef(defName) ?? "(unknown)", selfMs, inclMs);
        }

        /// <summary>
        /// 全局系统累加（map pre/post tick、ticklist 容器、storyteller 等）。
        /// 这些不属于任何 Thing / MapComponent，原先是未归因残差里最大的一块。
        /// </summary>
        public static void AddGlobal(int globalId, double selfMs)
        {
            CostTable.AddGlobal(globalId, selfMs);
        }

        /// <summary>帧阶段累加入口。只接受主线程的数据，worker 线程的时间不进帧账。</summary>
        public static void AddFrameStage(int stageId, double selfMs)
        {
            if (selfMs <= 0.0)
            {
                return;
            }
            FrameStats.AddStage(stageId, selfMs);
        }
    }
}
