using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PerfProbe
{
    /// <summary>
    /// 写 summary.md：前段给人看的四列对比表 + 一句结论，后段附录只给数据。
    /// 不写建议性判断（避免把 AI 带偏），只给可核对的数字与口径说明。
    /// </summary>
    public static class SummaryWriter
    {
        private const int TopPerCatFront = 8;

        /// <summary>
        /// 附录每类的行数上限。必须 ≥ 传入的 sessionRows 里单类的最大行数，
        /// 否则「按类合计」会漏算被截掉的部分。ProbeEngine 用 SessionRowCap = PerCatCap 取快照，
        /// 这里同步取同值，保证「建了多少桶就输出多少行」。
        /// </summary>
        private const int TopPerCatAppendix = CostTable.PerCatCap;

        /// <summary>生成并写入 summary.md。返回写入路径，失败返回空串。</summary>
        public static string Write(ProbeSession s, List<CostRow> sessionRows, List<CostRow> modRows,
                                   double sessionTotalMs, List<string> deletedFiles, int patchedTypes)
        {
            if (s == null || string.IsNullOrEmpty(s.SummaryPath))
            {
                return "";
            }
            string text;
            try
            {
                text = Build(s, sessionRows, modRows, sessionTotalMs, deletedFiles, patchedTypes);
            }
            catch (Exception e)
            {
                PLog.Err("生成 summary 内容失败: " + e);
                return "";
            }

            try
            {
                File.WriteAllText(s.SummaryPath, text, new UTF8Encoding(false));
                return s.SummaryPath;
            }
            catch (Exception e)
            {
                PLog.Err("写 summary.md 失败: " + e.Message);
                return "";
            }
        }

        private static string Build(ProbeSession s, List<CostRow> sessionRows, List<CostRow> modRows,
                                    double sessionTotalMs, List<string> deletedFiles, int patchedTypes)
        {
            var sb = new StringBuilder(16384);
            double durSec = s.ElapsedSec;
            double msPerTick = FrameProbe.SessionMsPerTick();
            // TPS 必须按「采集时段」算，不用会话总时长 —— 打开设置面板会强制暂停，
            // 那段时间照走但一个 tick 都不采，用总时长算出来的 TPS 会被系统性拉低。
            double tps = ProbeEngine.SessionTps;
            double capturedSec = ProbeEngine.SessionCapturedSec;

            sb.Append("# PerfProbe 会话汇总\n\n");
            sb.Append("| 项 | 值 |\n|---|---|\n");
            Row(sb, "会话文件", s.BaseName + ".jsonl");
            Row(sb, "模式", s.Mode.ToTag());
            Row(sb, "开始时间", s.StartLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Row(sb, "时长（秒）", durSec.ToString("0.0", CultureInfo.InvariantCulture));
            Row(sb, "其中已采集时段（秒）", capturedSec.ToString("0.0", CultureInfo.InvariantCulture));
            Row(sb, "采集 tick 数", FrameProbe.SessionTicks.ToString(CultureInfo.InvariantCulture));
            Row(sb, "**每 tick 均耗时（毫秒）**", msPerTick.ToString("0.0000", CultureInfo.InvariantCulture));
            Row(sb, "tick 耗时 P95（毫秒）", FrameProbe.SessionP95().ToString("0.0000", CultureInfo.InvariantCulture));
            Row(sb, "tick 耗时中位数（毫秒）", FrameProbe.SessionMedian().ToString("0.0000", CultureInfo.InvariantCulture));
            Row(sb, "实测 TPS（按已采集时段）", tps.ToString("0.0", CultureInfo.InvariantCulture));
            AppendFrameHeaderRows(sb);
            Row(sb, "覆盖缺口（毫秒）", FrameProbe.SessionGapMs.ToString("0.0", CultureInfo.InvariantCulture));
            Row(sb, "速度档", SpeedsText(s));
            Row(sb, "抽样分母 K", SampleGate.K.ToString(CultureInfo.InvariantCulture));
            Row(sb, "相位轮换周期（刻）", ProbeEngine.PhaseTicks.ToString(CultureInfo.InvariantCulture));
            Row(sb, "jsonl 行数", s.Lines.ToString(CultureInfo.InvariantCulture));
            Row(sb, "写盘丢行数", s.Writer.Dropped.ToString(CultureInfo.InvariantCulture));
            Row(sb, "已挂 patch 的组件类型数", patchedTypes.ToString(CultureInfo.InvariantCulture));
            Row(sb, "输出目录", ProbePaths.ActiveDir);
            if (ProbePaths.FellBack)
            {
                Row(sb, "目录回退原因", ProbePaths.FallbackReason);
            }
            AddProbeOverheadRows(sb, sessionTotalMs);
            sb.Append('\n');

            AppendConclusion(sb, msPerTick, tps, sessionRows, modRows);
            AppendFrameSection(sb);
            AppendMainTable(sb, sessionRows, sessionTotalMs);
            AppendModTable(sb, modRows, sessionTotalMs);
            AppendPawnTable(sb, sessionRows, sessionTotalMs);
            AppendAppendix(sb, sessionRows, sessionTotalMs);
            AppendRetention(sb, deletedFiles);
            AppendNotes(sb);

            return sb.ToString();
        }

        /// <summary>
        /// 探针自身开销与修正后的未归因残差。
        /// 这一节存在的理由：K=1 时探针给每个 thing 的 tick 都加了一对 Stopwatch 调用，
        /// 这些时间不落任何 Add*，会全额进「未归因」。不单列出来，
        /// 就会把「探针自己的成本」误读成「游戏里还有 38% 找不到来源」。
        /// </summary>
        private static void AddProbeOverheadRows(StringBuilder sb, double sessionTotalMs)
        {
            long events = ProbeEngine.SessionProbeEvents;
            double overhead = ProbeEngine.SessionProbeOverheadMs;
            double frameCost = ProbeEngine.SessionFrameCostMs;
            if (events <= 0 && ProbeEngine.SessionFrameSamples <= 0)
            {
                Row(sb, "探针自身开销", "未标定/无事件，见口径说明");
                return;
            }

            Row(sb, "探针计时事件数", events.ToString(CultureInfo.InvariantCulture));
            if (ProbeEngine.Calibrated)
            {
                Row(sb, "单次 Enter+Exit 成本（微秒）",
                    (ProbeEngine.PerPairMs * 1000.0).ToString("0.000", CultureInfo.InvariantCulture));
            }
            else
            {
                Row(sb, "单次 Enter+Exit 成本（微秒）", "未标定");
            }
            Row(sb, "探针计时成本（毫秒）", overhead.ToString("0.0", CultureInfo.InvariantCulture));
            Row(sb, "框架抽样样本数", ProbeEngine.SessionFrameSamples.ToString(CultureInfo.InvariantCulture));
            Row(sb, "探针框架成本（毫秒）", frameCost.ToString("0.0", CultureInfo.InvariantCulture));
            double total = overhead + frameCost;
            Row(sb, "探针总开销（毫秒）", total.ToString("0.0", CultureInfo.InvariantCulture));
            if (sessionTotalMs > 0.0)
            {
                Row(sb, "探针总开销占 tick 总耗时",
                    (total / sessionTotalMs * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%");
            }
        }

        /// <summary>
        /// 抽样口径说明与覆盖率。这是最容易读错的地方，必须写在结论段里：
        /// component 每 tick 全量计时，building / item / pawn 走 thingIDNumber % K 抽样，
        /// 直接把各类 selfMs 相加得到的占比是错的。
        /// </summary>
        private static void AppendSamplingNote(StringBuilder sb, double[] catSum)
        {
            int k = SampleGate.K;
            double compMs = catSum[5];
            double globalMs = catSum[7];
            double sampledRaw = catSum[0] + catSum[1] + catSum[2] + catSum[3];
            double totalMs = FrameProbe.SessionTotalMs;

            sb.Append("**抽样口径**：`component` 与 `global` 每 tick 全量计时，")
              .Append("`building` / `item` / `other` / `pawn` 按 `thingIDNumber % K` 抽样，当前 K = ")
              .Append(k.ToString(CultureInfo.InvariantCulture)).Append("。\n\n");

            if (k > 1)
            {
                sb.Append("上表各类的 `selfMs` **倍率不同，不能直接横向比**。")
                  .Append("下表的「修正后」对抽样类乘 K 还原：\n\n");
            }
            else
            {
                sb.Append("K = 1（全量），各类 `selfMs` 可直接横向比，无需修正。\n\n");
            }

            double fixedSampled = sampledRaw * k;
            double fixedSum = compMs + globalMs + fixedSampled;
            sb.Append("| 类 | 原始累计 ms | 倍率 | 修正后 ms | 占全 tick |\n");
            sb.Append("|---|---|---|---|---|\n");
            AppendCorrectedRow(sb, "人物", catSum[3], k, totalMs);
            AppendCorrectedRow(sb, "建筑", catSum[0], k, totalMs);
            AppendCorrectedRow(sb, "组件", compMs, 1, totalMs);
            AppendCorrectedRow(sb, "全局系统", globalMs, 1, totalMs);
            AppendCorrectedRow(sb, "物品", catSum[1], k, totalMs);
            AppendCorrectedRow(sb, "其它", catSum[2], k, totalMs);
            sb.Append("| **合计已归因** | | | ")
              .Append(fixedSum.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(Pct(fixedSum, totalMs)).Append(" |\n");
            double residual = totalMs - fixedSum;
            double probeOverhead = ProbeEngine.SessionProbeOverheadMs + ProbeEngine.SessionFrameCostMs;
            if (probeOverhead > 0.0)
            {
                sb.Append("| *其中：探针自身开销* | | | ")
                  .Append(probeOverhead.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Pct(probeOverhead, totalMs)).Append(" |\n");
            }
            if (residual > 0.0)
            {
                sb.Append("| *未归因残差* | | | ")
                  .Append(residual.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Pct(residual, totalMs)).Append(" |\n");
            }
            if (probeOverhead > 0.0 && residual > probeOverhead)
            {
                sb.Append("| *扣掉探针后的真实未归因* | | | ")
                  .Append((residual - probeOverhead).ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Pct(residual - probeOverhead, totalMs)).Append(" |\n");
            }
            sb.Append('\n');
            sb.Append("「未归因残差」里有两块是**探针自己**的成本：计时点的高精度时间戳，")
              .Append("以及 Harmony 委托跳转与字典查找（后者按 1/64 抽样实测外推）。")
              .Append("这部分时间不落任何类别，只会抬高总耗时。上表已把两者分开 —— ")
              .Append("看「扣掉探针后的真实未归因」才是游戏里真正找不到归属的开销。");
            if (globalMs > 0.0)
            {
                sb.Append("`global` 行已经覆盖了 `DoSingleTick` 里不经过 Thing / MapComponent 的 ")
                  .Append("map tick / 世界 tick / 叙事者 / 任务 / 历史等全局系统。");
            }
            sb.Append("\n\n");
        }

        private static void AppendCorrectedRow(StringBuilder sb, string name, double raw, int mult, double totalMs)
        {
            double fixedMs = raw * mult;
            sb.Append("| ").Append(name).Append(" | ")
              .Append(raw.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ×")
              .Append(mult.ToString(CultureInfo.InvariantCulture)).Append(" | ")
              .Append(fixedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(Pct(fixedMs, totalMs)).Append(" |\n");
        }

        private static string Pct(double part, double total)
        {
            if (total <= 0.0)
            {
                return "-";
            }
            return (part / total * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        private static void AppendConclusion(StringBuilder sb, double msPerTick, double tps,
                                             List<CostRow> rows, List<CostRow> modRows)
        {
            sb.Append("## 结论\n\n");
            if (FrameProbe.SessionTicks <= 0)
            {
                sb.Append("本次会话没有采集到有效 tick 样本（可能全程未处于被采集的速度档）。\n\n");
                return;
            }
            sb.Append("每 tick 平均 ")
              .Append(msPerTick.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(" 毫秒，实测 ")
              .Append(tps.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" tick/秒。\n\n");

            // 分类合计。mod 行是二次汇总，不能与其它类相加，故不参与合计。
            // pawninst 与 pawn 覆盖同一批 tick，二选一（取 pawn 的方面分解）。
            // pawn 叶子层是 pawn_body 的内部细分，同样不能重复计入 —— 见 CountsTowardCatTotal。
            double[] catSum = new double[8];
            for (int i = 0; i < rows.Count; i++)
            {
                if (!CountsTowardCatTotal(rows[i]))
                {
                    continue;
                }
                int ci = CatIndex(rows[i].Cat);
                if (ci >= 0)
                {
                    catSum[ci] += rows[i].SelfMs;
                }
            }
            string[] names = { "建筑", "物品", "其它", "人物", "事件", "组件", "模组", "全局系统" };
            sb.Append("按类合计：");
            bool first = true;
            // 只列参与合计的类（不含 mod 二次汇总），否则「按类合计」这一行的总数与上表对不上。
            for (int i = 0; i < names.Length; i++)
            {
                if (i == 6)
                {
                    continue;
                }
                if (catSum[i] <= 0.0)
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append("；");
                }
                first = false;
                sb.Append(names[i]).Append(' ').Append(catSum[i].ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms");
            }
            sb.Append("。\n\n");

            AppendSamplingNote(sb, catSum);

            // 单条最大。跳过 mod（二次汇总）与 pawninst（与人物方面行重叠），
            // 否则「单条最高」会指向一个人物个体而不是一个 def。
            CostRow top = default(CostRow);
            bool hasTop = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (IsDerivedCat(rows[i].Cat))
                {
                    continue;
                }
                if (!hasTop || rows[i].SelfMs > top.SelfMs)
                {
                    top = rows[i];
                    hasTop = true;
                }
            }
            if (hasTop && top.SelfMs > 0.0)
            {
                sb.Append("单条耗时最高：`").Append(top.Cat).Append('|').Append(top.Name)
                  .Append("`（模组 ").Append(top.Mod).Append("），")
                  .Append(top.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms，占 ")
                  .Append(top.Pct.ToString("0.0", CultureInfo.InvariantCulture)).Append("%");
                if (top.Alive > 0)
                {
                    sb.Append("，").Append(top.Alive).Append(" 个活跃实例，每实例 ")
                      .Append(top.PerInstMs.ToString("0.000000", CultureInfo.InvariantCulture)).Append(" ms");
                }
                sb.Append("。\n\n");
            }

            // 模组最大
            if (modRows != null && modRows.Count > 0)
            {
                CostRow m = modRows[0];
                sb.Append("耗时最高的模组：**").Append(m.Name).Append("**，")
                  .Append(m.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms，占 ")
                  .Append(m.Pct.ToString("0.0", CultureInfo.InvariantCulture)).Append("%。\n\n");
            }

            AppendPeakSection(sb, rows);
        }

        /// <summary>
        /// 峰值窗口排行。回答的是「哪一行在某一瞬间单独吃掉了很多」，
        /// 与按会话累计算的排行榜是两个问题：持续型热点的累计值高但不吓人，
        /// 爆发型（如炮台拦截波）累计值中等却能把单帧推爆。
        /// </summary>
        private static void AppendPeakSection(StringBuilder sb, List<CostRow> rows)
        {
            if (CostTable.WindowCount <= 0)
            {
                return;
            }
            var top = new List<CostRow>(16);
            for (int i = 0; i < rows.Count; i++)
            {
                CostRow r = rows[i];
                if (r.MaxW <= 0.0 || IsDerivedCat(r.Cat))
                {
                    continue;
                }
                top.Add(r);
            }
            if (top.Count == 0)
            {
                return;
            }
            top.Sort(delegate (CostRow a, CostRow b) { return b.MaxW.CompareTo(a.MaxW); });

            sb.Append("## 峰值窗口排行（单窗口最贵，前 10）\n\n");
            sb.Append("`MaxW` 是该行在任意一个窗口里的最高耗时（窗口长 ")
              .Append((ProbeEngine.Settings != null ? ProbeEngine.Settings.WindowSec : 5))
              .Append(" 秒）；`P50W` 是典型窗口的耗时。")
              .Append("两者差距大的行属于「平时不显、出事才爆」，与累计排行里的地位不一致是正常的。\n\n");
            sb.Append("| 类 | 名称 | 模组 | MaxW | P50W | P95W | SdW | 形态 | 占最贵窗口 % |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|\n");
            int take = top.Count < 10 ? top.Count : 10;
            for (int i = 0; i < take; i++)
            {
                CostRow r = top[i];
                sb.Append("| ").Append(r.Cat).Append(" | ").Append(Json.Esc(r.Name)).Append(" | ")
                  .Append(Json.Esc(r.Mod)).Append(" | ")
                  .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.P50W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.P95W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.SdW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Kind(r)).Append(" | ")
                  .Append(r.PctPeak.ToString("0.00", CultureInfo.InvariantCulture)).Append(" |\n");
            }
            sb.Append('\n');
        }

        /// <summary>
        /// 会话头里的帧率三项。与 tick 指标并排，因为「TPS 低」有两种完全不同的原因：
        /// CPU 每 tick 太慢，或者帧率太低导致每帧能跑的 tick 被砍。
        /// 只看 msPerTick 会把后者误判成前者。
        /// </summary>
        private static void AppendFrameHeaderRows(StringBuilder sb)
        {
            if (FrameStats.SesFrames <= 0)
            {
                Row(sb, "帧率（FPS）", "未采到帧样本");
                return;
            }
            double fps = FrameStats.SesFps();
            Row(sb, "**帧率（FPS）**", fps.ToString("0.0", CultureInfo.InvariantCulture));
            Row(sb, "平均帧长（毫秒）", FrameStats.SesAvgFrameMs().ToString("0.000", CultureInfo.InvariantCulture));
            Row(sb, "帧长 P95（毫秒）", FrameStats.WinFrameP95().ToString("0.000", CultureInfo.InvariantCulture));
            Row(sb, "采集帧数", FrameStats.SesFrames.ToString(CultureInfo.InvariantCulture));
            // 空闲帧 = 本帧一个 tick 都没跑（暂停 / 读档 / 主菜单）。它们的帧长只有十几毫秒，
            // 若混进帧率会把结果抬高近一倍，所以被排除在外。这里明示排除了多少，便于核对。
            if (FrameStats.SesIdleFrames > 0)
            {
                Row(sb, "已排除空闲帧", FrameStats.SesIdleFrames.ToString(CultureInfo.InvariantCulture)
                    + "（暂停/读档/主菜单，不计入帧率）");
            }
            Row(sb, "每帧 tick 数", (FrameStats.SesFrames > 0
                ? (double)FrameProbe.SessionTicks / FrameStats.SesFrames
                : 0.0).ToString("0.00", CultureInfo.InvariantCulture));
            // 30 FPS 是引擎的临界值：低于它，每帧能跑的 tick 被 TickRateMultiplier×2 夹住，
            // TPS 开始随帧率线性下滑且不可补偿。这一行直接告诉用户处在哪一侧。
            Row(sb, "帧率是否卡住 TPS", fps >= 30.0
                ? "否（≥30 FPS，TPS 只受 CPU 影响）"
                : "是（<30 FPS，每帧 tick 数被上限夹住，TPS 随帧率下降）");
        }

        /// <summary>
        /// 帧阶段分解。这是本次新增的核心结论段：
        /// 帧长减去所有已测阶段就是 unaccounted —— 引擎渲染、Present、显存等待都在里面。
        /// 它大且帧率低 → GPU 侧；它小且某 CPU 阶段大 → CPU 侧。
        /// </summary>
        private static void AppendFrameSection(StringBuilder sb)
        {
            if (FrameStats.SesFrames <= 0)
            {
                sb.Append("## 帧阶段分解\n\n本次会话没有采到帧样本。\n\n");
                return;
            }

            double sesFrames = FrameStats.SesFrames;
            double avgFrame = FrameStats.SesAvgFrameMs();
            double fps = FrameStats.SesFps();
            double accounted = FrameStats.SesAccountedMs();
            double unacc = FrameStats.SesUnaccountedMs();

            sb.Append("## 帧阶段分解\n\n");
            sb.Append("每帧平均 ").Append(avgFrame.ToString("0.000", CultureInfo.InvariantCulture))
              .Append(" 毫秒（").Append(fps.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" FPS），共 ").Append(sesFrames.ToString("0", CultureInfo.InvariantCulture))
              .Append(" 帧。下表是各阶段在**每帧**里的平均耗时。\n\n");
            if (FrameStats.SesIdleFrames > 0)
            {
                sb.Append("这里只统计**活跃帧**（本帧至少跑了一个 tick）。另有 ")
                  .Append(FrameStats.SesIdleFrames.ToString("0", CultureInfo.InvariantCulture))
                  .Append(" 个空闲帧（暂停 / 读档 / 主菜单）已排除：它们帧长只有十几毫秒，")
                  .Append("混进来会把帧率抬高近一倍，而玩家实际感受到的卡顿发生在活跃帧里。\n\n");
            }
            sb.Append("帧长的定义是「本帧 `Root.Update` 起点 − 上一帧起点」，")
              .Append("因此 `OnGUI` 与引擎渲染、Present 都落在这个区间内。")
              .Append("各阶段记 self 时间（已扣除嵌套子段），可相加。\n\n");

            sb.Append("| 阶段 | 每帧毫秒 | 占帧长 % | 性质 |\n|---|---|---|---|\n");
            List<FrameStageRow> rows = FrameStats.SesRows();
            for (int i = 0; i < rows.Count; i++)
            {
                FrameStageRow r = rows[i];
                sb.Append("| ").Append(Json.Esc(FrameCost.Label(FrameCost.NameOf(r.Id)))).Append(" | ")
                  .Append(r.MsPerFrame.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(FrameCost.IsRender(r.Id) ? "渲染" : "逻辑").Append(" |\n");
            }
            // unaccounted 单列一行，因为它是本次新增里最有信息量的那个数。
            double unaccPerFrame = sesFrames > 0 ? unacc / sesFrames : 0.0;
            sb.Append("| **未测部分（引擎渲染 / Present / 显存等待）** | **")
              .Append(unaccPerFrame.ToString("0.0000", CultureInfo.InvariantCulture)).Append("** | **")
              .Append(avgFrame > 0.0 ? (unaccPerFrame / avgFrame * 100.0).ToString("0.00", CultureInfo.InvariantCulture) : "0.00")
              .Append("** | — |\n\n");

            // 判据：把「渲染侧」与「CPU 逻辑侧」分开汇总，直接给结论。
            double renderMs = 0.0;
            double logicMs = 0.0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (FrameCost.IsRender(rows[i].Id))
                {
                    renderMs += rows[i].SelfMs;
                }
                else
                {
                    logicMs += rows[i].SelfMs;
                }
            }
            double renderPerFrame = sesFrames > 0 ? renderMs / sesFrames : 0.0;
            double logicPerFrame = sesFrames > 0 ? logicMs / sesFrames : 0.0;
            sb.Append("渲染侧合计 ").Append(renderPerFrame.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(" ms/帧，逻辑侧合计 ").Append(logicPerFrame.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(" ms/帧，未测 ").Append(unaccPerFrame.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(" ms/帧。\n\n");

            sb.Append("**判读**：\n\n");
            if (unaccPerFrame > renderPerFrame + logicPerFrame)
            {
                sb.Append("- 未测部分占多数，说明时间花在引擎渲染、Present 或显存换页上，")
                  .Append("不是 mod 的 tick 逻辑。此时继续优化 `msPerTick` 对帧率帮助有限，")
                  .Append("应查显存（贴图预算、`PawnTextureAtlas` 尺寸）与绘制批次数量。\n");
            }
            else if (renderPerFrame > logicPerFrame)
            {
                sb.Append("- 渲染侧（含网格与动态物件绘制）大于逻辑侧，")
                  .Append("瓶颈在「画多少」而不是「算多少」：视野内实体数量、贴图尺寸、叠加层密度是主要抓手。\n");
            }
            else
            {
                sb.Append("- 逻辑侧大于渲染侧，瓶颈在 CPU 的每帧逻辑（tick、地图更新、UI），")
                  .Append("继续用下面的 tick 归因表下钻。\n");
            }
            if (fps < 30.0)
            {
                sb.Append("- 帧率已低于 30 FPS 临界值，每帧 tick 数被 `TickRateMultiplier × 2` 夹住，")
                  .Append("**TPS 正在随帧率一起下降**，且这部分损失不可补偿（累加器余量每帧清零）。\n");
            }
            sb.Append('\n');
        }

        private static void AppendMainTable(StringBuilder sb, List<CostRow> rows, double totalMs)
        {
            sb.Append("## 分类对比（前段）\n\n");
            sb.Append("同一分母（本次会话采集到的总 tick 耗时 ").Append(totalMs.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" ms）、同一单位。`selfMs` 可加总，`inclMs` 不可加总。\n\n");
            sb.Append("| 类 | 名称 | 模组 | selfMs | inclMs | 占比% | P50W | P95W | MaxW | SdW | 形态 | 活跃 | 休眠 | 每实例 ms | 置信 |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");

            string[] cats = { ThingCost.CatBuilding, ThingCost.CatItem, ThingCost.CatPawn, ThingCost.CatComponent, ThingCost.CatOther, ThingCost.CatGlobal };
            for (int c = 0; c < cats.Length; c++)
            {
                int shown = 0;
                for (int i = 0; i < rows.Count && shown < TopPerCatFront; i++)
                {
                    if (!string.Equals(rows[i].Cat, cats[c], StringComparison.Ordinal))
                    {
                        continue;
                    }
                    AppendRow(sb, rows[i]);
                    shown++;
                }
                if (shown == 0)
                {
                    sb.Append("| ").Append(cats[c]).Append(" | （无数据） | | | | | | | | | | | | | |\n");
                }
            }
            sb.Append('\n');
        }

        private static void AppendModTable(StringBuilder sb, List<CostRow> modRows, double totalMs)        {
            sb.Append("## 模组对比（前段）\n\n");
            if (modRows == null || modRows.Count == 0)
            {
                sb.Append("（无数据）\n\n");
                return;
            }
            sb.Append("| 模组 | selfMs | inclMs | 占比% |\n|---|---|---|---|\n");
            int take = modRows.Count < 20 ? modRows.Count : 20;
            for (int i = 0; i < take; i++)
            {
                CostRow r = modRows[i];
                sb.Append("| ").Append(Json.Esc(r.Name)).Append(" | ")
                  .Append(r.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.InclMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.0", CultureInfo.InvariantCulture)).Append(" |\n");
            }
            sb.Append('\n');
        }

        private static void AppendPawnTable(StringBuilder sb, List<CostRow> rows, double totalMs)
        {
            sb.Append("## 人物十四方面分解（前段）\n\n");
            sb.Append("`pawn_body` / `pawn_interval` / `pawn_other` 是**外壳层**。")
              .Append("前者是 `Pawn.Tick`（每 tick 必跑，不受任何降频 mod 影响），")
              .Append("中者是 `Pawn.TickInterval`（频率由 `UpdateRateTicks` 决定），")
              .Append("后者是 `Thing.DoTick` 自身外壳（缓存检查、属性读、`directlyHeldThings.DoTick`）。")
              .Append("**这三项相加恒等于所有 pawn 的 `Thing.DoTick` 总时间。**\n\n")
              .Append("核心组与扩展组是 `pawn_body` **内部**的细分，不能与外壳层相加 —— ")
              .Append("它们是「body 里花在哪」的答案，不是 body 之外的另一笔开销。\n\n");

            bool any = AppendPawnAspectTable(sb, rows, PawnCost.Body, "外壳层（三项相加 = 人物总时间）");
            AppendPawnAspectTable(sb, rows, null, "核心组（`pawn_body` 内部）");
            AppendPawnAspectTable(sb, rows, null, "扩展组（`pawn_body` 内部）");

            if (!any)
            {
                sb.Append("（本次会话没有采集到 pawn 样本）\n\n");
            }

            double shellSum = 0.0;
            double leafSum = 0.0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!string.Equals(rows[i].Cat, ThingCost.CatPawn, StringComparison.Ordinal))
                {
                    continue;
                }
                if (PawnCost.IsShell(rows[i].Name))
                {
                    shellSum += rows[i].SelfMs;
                }
                else
                {
                    leafSum += rows[i].SelfMs;
                }
            }
            if (shellSum > 0.0 && totalMs > 0.0)
            {
                sb.Append("人物合计（外壳层三项之和）**").Append(shellSum.ToString("0.000", CultureInfo.InvariantCulture))
                  .Append(" ms**，占全会话 tick 总耗时的 **")
                  .Append((shellSum / totalMs * 100.0).ToString("0.00", CultureInfo.InvariantCulture))
                  .Append("%**。\n\n");
                if (leafSum > 0.0)
                {
                    sb.Append("叶子层（核心组 + 扩展组）合计 ").Append(leafSum.ToString("0.000", CultureInfo.InvariantCulture))
                      .Append(" ms —— 这是 `pawn_body` 的内部构成，**已在上面那个数里**，不要重复加。")
                      .Append("它小于 `pawn_body` 的部分就是 body 里尚未打点的调用（音效维持、`Suspended` 判断等）。\n\n");
                }
            }

            AppendPawnInstanceTable(sb, rows, totalMs);
        }

        /// <summary>
        /// 输出人物某一段的方面表。group 语义：
        ///   PawnCost.Body       → 只列外壳层（body / interval / other）
        ///   其它（按 title）→ 核心组 = 非外壳层且非扩展组；扩展组 = IsExtended
        /// 返回这一段是否有数据。
        /// </summary>
        private static bool AppendPawnAspectTable(StringBuilder sb, List<CostRow> rows, string group, string title)
        {
            sb.Append("### ").Append(title).Append("\n\n");
            sb.Append("| 方面 | 中文 | selfMs | 占比% | P50W | MaxW | 形态 | 活跃 pawn | 每实例 ms |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|\n");
            bool any = false;
            for (int i = 0; i < rows.Count; i++)
            {
                CostRow r = rows[i];
                if (!string.Equals(r.Cat, ThingCost.CatPawn, StringComparison.Ordinal))
                {
                    continue;
                }
                bool want;
                if (group == PawnCost.Body)
                {
                    want = PawnCost.IsShell(r.Name);
                }
                else if (title.StartsWith("核心组", StringComparison.Ordinal))
                {
                    want = !PawnCost.IsShell(r.Name) && !PawnCost.IsExtended(r.Name);
                }
                else
                {
                    want = PawnCost.IsExtended(r.Name);
                }
                if (!want)
                {
                    continue;
                }
                any = true;
                sb.Append("| ").Append(r.Name).Append(" | ").Append(PawnCost.Label(r.Name)).Append(" | ")
                  .Append(r.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.P50W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Kind(r)).Append(" | ")
                  .Append(AliveCell(r)).Append(" | ")
                  .Append(PerInstCell(r)).Append(" |\n");
            }
            if (!any)
            {
                sb.Append("| （无数据） | | | | | | | | |\n");
            }
            sb.Append('\n');
            return any;
        }

        /// <summary>
        /// 人物个体 / 种族排行。人类与异星种族按个体列出（名字[种族]），
        /// 动物与机械体按 kindDef 合并（同一类一行，每实例 ms 是同类均值）。
        /// </summary>
        private static void AppendPawnInstanceTable(StringBuilder sb, List<CostRow> rows, double totalMs)
        {
            sb.Append("## 人物个体 / 种族排行（前段）\n\n");
            sb.Append("人类与异星种族按个体列出，`名称` 形如 `名字[kindDef]`；")
              .Append("动物与机械体按种族合并，`名称` 就是 kindDef，`每实例 ms` 是同类均值。\n\n");
            sb.Append("| 名称 | 模组 | asm | selfMs | 占比% | P50W | MaxW | 形态 | 实例数 | 每实例 ms | 置信 |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|---|---|\n");
            int shown = 0;
            for (int i = 0; i < rows.Count && shown < TopPerCatFront; i++)
            {
                if (!string.Equals(rows[i].Cat, ThingCost.CatPawnInst, StringComparison.Ordinal))
                {
                    continue;
                }
                CostRow r = rows[i];
                sb.Append("| ").Append(Json.Esc(r.Name)).Append(" | ")
                  .Append(Json.Esc(r.Mod)).Append(" | ")
                  .Append(Json.Esc(r.Asm)).Append(" | ")
                  .Append(r.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.P50W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Kind(r)).Append(" | ")
                  .Append(AliveCell(r)).Append(" | ")
                  .Append(PerInstCell(r)).Append(" | ")
                  .Append(r.Conf.ToString("0.00", CultureInfo.InvariantCulture)).Append(" |\n");
                shown++;
            }
            if (shown == 0)
            {
                sb.Append("| （无数据） | | | | | | | | | | |\n");
            }
            sb.Append('\n');
        }

        private static void AppendAppendix(StringBuilder sb, List<CostRow> rows, double totalMs)
        {
            sb.Append("## 附录：全量数据\n\n");
            sb.Append("每类最多 ").Append(TopPerCatAppendix).Append(" 行，按 `selfMs` 降序。\n\n");
            string[] cats = { ThingCost.CatBuilding, ThingCost.CatItem, ThingCost.CatOther, ThingCost.CatPawn, ThingCost.CatPawnInst, ThingCost.CatComponent, ThingCost.CatGlobal };
            string[] titles = { "建筑", "物品", "其它", "人物方面", "人物个体 / 种族", "组件", "全局系统" };
            for (int c = 0; c < cats.Length; c++)
            {
                sb.Append("### ").Append(titles[c]).Append("（`cat=").Append(cats[c]).Append("`）\n\n");
                if (string.Equals(cats[c], ThingCost.CatPawn, StringComparison.Ordinal))
                {
                    // 这张表把外壳层与叶子层并列展示，两者相加会重复计数。必须在表头就说清楚。
                    sb.Append("> `pawn_body` / `pawn_interval` / `pawn_other` 是**外壳层**，三项之和即全部 pawn 的时间；\n")
                      .Append("> 其余行（`pawn_work` / `pawn_path` / `pawn_state` …）是 `pawn_body` 的**内部细分**，\n")
                      .Append("> 已经在 `pawn_body` 里了，**不能再加一次**。\n\n");
                }
                sb.Append("| 类 | 名称 | 模组 | asm | selfMs | inclMs | 占比% | P50W | P95W | MaxW | SdW | 形态 | 活跃 | 休眠 | 每实例 ms | 置信 |\n");
                sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
                int shown = 0;
                for (int i = 0; i < rows.Count && shown < TopPerCatAppendix; i++)
                {
                    if (!string.Equals(rows[i].Cat, cats[c], StringComparison.Ordinal))
                    {
                        continue;
                    }
                    AppendRow(sb, rows[i]);
                    shown++;
                }
                if (shown == 0)
                {
                    sb.Append("| ").Append(cats[c]).Append(" | （无数据） | | | | | | | | | | | | | | |\n");
                }
                sb.Append('\n');
            }

            // 归因存疑
            sb.Append("### 归因存疑（conf 小于 1）\n\n");
            bool any = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Conf >= 1f)
                {
                    continue;
                }
                if (!any)
                {
                    sb.Append("| 类 | 名称 | 推断模组 | conf | selfMs |\n|---|---|---|---|---|\n");
                    any = true;
                }
                sb.Append("| ").Append(rows[i].Cat).Append(" | ").Append(Json.Esc(rows[i].Name)).Append(" | ")
                  .Append(Json.Esc(rows[i].Mod)).Append(" | ")
                  .Append(rows[i].Conf.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(rows[i].SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" |\n");
            }
            if (!any)
            {
                sb.Append("（全部命中 def 索引，无存疑项）\n");
            }
            sb.Append('\n');
        }

        private static void AppendRetention(StringBuilder sb, List<string> deletedFiles)
        {
            sb.Append("## 保留策略\n\n");
            if (deletedFiles == null || deletedFiles.Count == 0)
            {
                sb.Append("本次启动未删除任何旧会话文件。\n\n");
                return;
            }
            sb.Append("本次启动删除了 ").Append(deletedFiles.Count).Append(" 个旧会话文件：\n\n");
            for (int i = 0; i < deletedFiles.Count; i++)
            {
                sb.Append("- `").Append(deletedFiles[i]).Append("`\n");
            }
            sb.Append('\n');
        }

        private static void AppendNotes(StringBuilder sb)
        {
            sb.Append("## 口径说明\n\n");
            sb.Append("1. `selfMs` 是自身耗时，可跨行加总；`inclMs` 含子调用，加总会重复计数。\n");
            sb.Append("2. `Pawn` 继承 `Thing`，探针在 thing 层显式跳过 pawn，因此 `building` / `item` / `other` 与 `pawn` 无重叠。\n");
            sb.Append("3. 只有 `fast` 与 `superfast` 档进统计；暂停、一倍速、四倍速期间只累加覆盖缺口。\n");
            sb.Append("4. `perInstMs` 的分母只算活跃实例（`tickerType` 不为 `Never`），休眠实例单独给 `dormant`。\n");
            sb.Append("5. 抽样分母 K 大于 1 时，各行的 `selfMs` 是抽样后的累计值，跨会话比较请保持 K 与窗口设置一致。\n");
            sb.Append("6. 事件维度（`cat=incident`）在 M2 提供，本版本 schema 已预留该取值。\n");            sb.Append("7. `P50W` / `P95W` / `MaxW` / `SdW` 基于**窗口级**样本（每窗口一个点），"
                      + "不是 tick 级分布。窗口内没出现的行按 0 计入样本，因此 `P50W` 可以直接读成"
                      + "「典型 5 秒窗口里这行花了多少」。保留最近 ")
              .Append(CostTable.SeriesCap).Append(" 个窗口（本次 ")
              .Append(CostTable.WindowCount).Append(" 个）。\n");
            sb.Append("8. `形态` 是机械判别：`持续型` = 最大窗口/mid 窗口 < 2 倍；"
                      + "`波动型` ≥ 2 倍；`爆发型` ≥ 3 倍且标准差 ≥ 中值；"
                      + "`偶发` = 中值为 0；`样本少` = 只有一个窗口。\n");
            sb.Append("9. `占最贵窗口 %` 是「该行最贵的那一窗口里，它占该窗口总 tick 耗时的比例」，"
                      + "用来判断单点爆发能不能解释整帧卡死。\n");
            sb.Append("10. 人物表分三层：`pawn_body` / `pawn_interval` 是**外壳层**（inclusive，来自 `Pawn.Tick` 与 `Pawn.TickInterval`），"
                      + "`pawn_other` 是 `Thing.DoTick` 本身的外壳开销；这三项相加才是该 pawn 的 DoTick 总时间。"
                      + "核心组 / 扩展组是 `pawn_body` 的内部细分，**不能与外壳层相加**。\n");
            sb.Append("11. `实测 TPS` 的分母是「已采集时段」，不是会话总时长 —— 打开模组设置面板会强制暂停，"
                      + "那段时间照走但不采集 tick，用总时长算会把 TPS 系统性拉低。\n");
            sb.Append("12. `global` 是 `TickManager.DoSingleTick` 里不经过任何 Thing / MapComponent 的全局系统"
                      + "（map pre/post tick、ticklist 容器、叙事者、任务、历史、世界 tick 等）。"
                      + "它们记 self 时间：`TickList.Tick` 内部几千个 `Thing.DoTick` 已被扣走，只剩下遍历与 try/catch 的开销。\n");
            sb.Append("13. 探针开销分两部分：`探针计时成本` 由 `DepthTracker.Enter/Exit` 标定值 × 事件数得出；"
                      + "`探针框架成本` 由 `Thing.DoTick` 每 64 个对象抽样 1 个、实测 Prefix/Postfix 自身耗时后 ×64 外推。"
                      + "后者覆盖 Harmony 委托跳转、字典查找等不在标定范围内的成本。\n");
        }

        private static void AppendRow(StringBuilder sb, CostRow r)
        {
            sb.Append("| ").Append(r.Cat).Append(" | ").Append(Json.Esc(r.Name)).Append(" | ")
              .Append(Json.Esc(r.Mod)).Append(" | ")
              .Append(r.SelfMs.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(r.InclMs.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(r.Pct.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(r.P50W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(r.P95W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(r.SdW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(Kind(r)).Append(" | ")
              .Append(AliveCell(r)).Append(" | ")
              .Append(r.Dormant.ToString(CultureInfo.InvariantCulture)).Append(" | ")
              .Append(PerInstCell(r)).Append(" | ")
              .Append(r.Conf.ToString("0.00", CultureInfo.InvariantCulture)).Append(" |\n");
        }

        /// <summary>
        /// 实例数一栏。扫不到实例但有耗时的时候显示 `?` —— 
        /// 写 0 会被读成「没有实例却有耗时」的矛盾，实际是「分母未知」。
        /// </summary>
        private static string AliveCell(CostRow r)
        {
            return r.AliveKnown ? r.Alive.ToString(CultureInfo.InvariantCulture) : "?";
        }

        private static string PerInstCell(CostRow r)
        {
            return r.AliveKnown
                ? r.PerInstMs.ToString("0.000000", CultureInfo.InvariantCulture)
                : "?";
        }

        /// <summary>
        /// 峰值形态判别。判据只看窗口级样本：
        /// 最大窗口与中值窗口的比值，配合标准差相对中值的离散度。
        /// 这套判据的意义是区分「每窗口都在烧」与「平时没事、出事时爆」——两者的优化手段完全不同。
        /// </summary>
        private static string Kind(CostRow r)
        {
            if (r.WinN <= 1)
            {
                return "样本少";
            }
            if (r.MaxW <= 0.0)
            {
                return "-";
            }
            double ratio = r.P50W > 0.0 ? r.MaxW / r.P50W : double.MaxValue;
            double cv = r.P50W > 0.0 ? r.SdW / r.P50W : double.MaxValue;

            if (r.P50W <= 0.0)
            {
                // 中值为 0：多数窗口完全不烧，只在个别窗口出现。
                return "偶发";
            }
            if (ratio >= 3.0 && cv >= 1.0)
            {
                return "爆发型";
            }
            if (ratio >= 2.0)
            {
                return "波动型";
            }
            return "持续型";
        }

        private static void Row(StringBuilder sb, string k, string v)
        {
            sb.Append("| ").Append(k).Append(" | ").Append(string.IsNullOrEmpty(v) ? "-" : v).Append(" |\n");
        }

        private static string SpeedsText(ProbeSession s)
        {
            if (s.FastSeen && s.SuperfastSeen)
            {
                return "fast + superfast（分窗口记录，同一行不混算）";
            }
            if (s.FastSeen)
            {
                return "fast";
            }
            if (s.SuperfastSeen)
            {
                return "superfast";
            }
            if (s.UltrafastSeen)
            {
                return "ultrafast";
            }
            return "（未记录）";
        }

        private static int CatIndex(string cat)
        {
            switch (cat)
            {
                case ThingCost.CatBuilding: return 0;
                case ThingCost.CatItem: return 1;
                case ThingCost.CatOther: return 2;
                case ThingCost.CatPawn: return 3;
                case ThingCost.CatIncident: return 4;
                case ThingCost.CatComponent: return 5;
                case ThingCost.CatMod: return 6;
                case ThingCost.CatGlobal: return 7;
                default: return -1;
            }
        }

        /// <summary>
        /// 二次汇总行：不参与「单条最高」这类单点比较，避免与原始行重复计数。
        /// mod 是跨类汇总，pawninst 与人物方面行覆盖同一批 pawn tick。
        /// </summary>
        private static bool IsDerivedCat(string cat)
        {
            return string.Equals(cat, ThingCost.CatMod, StringComparison.Ordinal)
                || string.Equals(cat, ThingCost.CatPawnInst, StringComparison.Ordinal);
        }

        /// <summary>
        /// 某行是否计入「按类合计」。
        ///
        /// pawn 类必须特殊处理：`cat=pawn` 的十四行里，只有 pawn_body / pawn_interval /
        /// pawn_other 是外壳层，三项之和才等于全部 pawn 的 `Thing.DoTick` 总时间；
        /// 其余十一个叶子行（work / path / state / ...）是 **pawn_body 的内部细分**，
        /// 已经含在 pawn_body 里，再加一次就是重复计数。
        ///
        /// 历史 bug：早期只排除 mod 与 pawninst，漏了 pawn 叶子层。
        /// 后果是「合计已归因」能超过 100%（实测 119.68%），且与 summary 自己的
        /// 「人物合计（外壳层三项之和）」直接矛盾 —— 同一个文件给出两个不同的人物总计。
        /// 旧会话没暴露是因为当时没有 global 类，重复的那部分全藏在残差里。
        /// </summary>
        private static bool CountsTowardCatTotal(CostRow r)
        {
            if (IsDerivedCat(r.Cat))
            {
                return false;
            }
            if (string.Equals(r.Cat, ThingCost.CatPawn, StringComparison.Ordinal))
            {
                return PawnCost.IsShell(r.Name);
            }
            return true;
        }

        // ================= brief.md：给网页版 / 纯对话 AI 的精简版 =================

        /// <summary>
        /// brief 每类只留这么多行。summary.md 前段是 8，这里压到 5。
        /// 配合下面精简过的列（分类表 8 列而非 15 列），整份文件约 8–10 KB / 2500–3000 token，
        /// 可以直接粘进任何网页对话框。
        /// </summary>
        private const int TopPerCatBrief = 5;

        /// <summary>brief 里模组只留前 10（summary 是 20）。</summary>
        private const int TopModBrief = 10;

        /// <summary>brief 里峰值窗口只留前 5（summary 是 10）。</summary>
        private const int TopPeakBrief = 5;

        /// <summary>
        /// 生成 brief.md。与 summary.md 的区别是「只保留做决策必需的」：
        /// 不含附录全量、不含口径说明 13 条、不含保留策略。
        /// 实测同一会话 summary.md 是 140 KB / 约 4 万 token，brief.md 约 10 KB / 2500 token。
        /// 返回写入路径，失败返回空串。
        /// </summary>
        public static string WriteBrief(ProbeSession s, List<CostRow> sessionRows, List<CostRow> modRows,
                                        double sessionTotalMs, int patchedTypes)
        {
            if (s == null || string.IsNullOrEmpty(s.BriefPath))
            {
                return "";
            }
            string text;
            try
            {
                text = BuildBrief(s, sessionRows, modRows, sessionTotalMs, patchedTypes);
            }
            catch (Exception e)
            {
                PLog.Err("生成 brief 内容失败: " + e);
                return "";
            }
            try
            {
                File.WriteAllText(s.BriefPath, text, new UTF8Encoding(false));
                return s.BriefPath;
            }
            catch (Exception e)
            {
                PLog.Err("写 brief.md 失败: " + e.Message);
                return "";
            }
        }

        private static string BuildBrief(ProbeSession s, List<CostRow> sessionRows, List<CostRow> modRows,
                                         double sessionTotalMs, int patchedTypes)
        {
            var sb = new StringBuilder(8192);
            double msPerTick = FrameProbe.SessionMsPerTick();
            double tps = ProbeEngine.SessionTps;

            sb.Append("# PerfProbe 精简报告\n\n");
            sb.Append("> 这是给「只能读文本」的 AI 用的版本，已去掉全量附录。\n")
              .Append("> 同一会话的完整数据在同名 `.summary.md`（含每类全量明细）与 `.jsonl`（逐窗口原始记录）。\n\n");

            // ---- 会话头（只留决策相关的 6 项）----
            sb.Append("| 项 | 值 |\n|---|---|\n");
            Row(sb, "会话文件", s.BaseName + ".jsonl");
            Row(sb, "开始时间", s.StartLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Row(sb, "时长（秒）", s.ElapsedSec.ToString("0.0", CultureInfo.InvariantCulture));
            Row(sb, "**每 tick 均耗时（毫秒）**", msPerTick.ToString("0.0000", CultureInfo.InvariantCulture));
            Row(sb, "tick 耗时 P95（毫秒）", FrameProbe.SessionP95().ToString("0.0000", CultureInfo.InvariantCulture));
            Row(sb, "实测 TPS", tps.ToString("0.0", CultureInfo.InvariantCulture));
            if (FrameStats.SesFrames > 0)
            {
                double fps = FrameStats.SesFps();
                Row(sb, "**帧率（FPS）**", fps.ToString("0.0", CultureInfo.InvariantCulture));
                Row(sb, "平均帧长（毫秒）", FrameStats.SesAvgFrameMs().ToString("0.000", CultureInfo.InvariantCulture));
                Row(sb, "帧率是否卡住 TPS", fps >= 30.0 ? "否（≥30 FPS）" : "是（<30 FPS，TPS 随帧率下降）");
                if (FrameStats.SesIdleFrames > 0)
                {
                    Row(sb, "已排除空闲帧", FrameStats.SesIdleFrames.ToString(CultureInfo.InvariantCulture));
                }
            }
            Row(sb, "速度档", SpeedsText(s));
            Row(sb, "采集 tick 数", FrameProbe.SessionTicks.ToString(CultureInfo.InvariantCulture));
            Row(sb, "写盘丢行数", s.Writer.Dropped.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');

            // ---- 结论：分类合计 + 单条最高 + 模组最高 ----
            AppendBriefConclusion(sb, msPerTick, tps, sessionRows, modRows);

            // ---- 峰值窗口 Top5 ----
            AppendBriefPeak(sb, sessionRows);

            // ---- 分类 Top5 ----
            AppendBriefCatTable(sb, sessionRows, sessionTotalMs);

            // ---- 模组 Top10 ----
            AppendBriefModTable(sb, modRows);

            // ---- 人物：只留外壳三项 + 个体 Top5 ----
            AppendBriefPawn(sb, sessionRows, sessionTotalMs);

            // ---- 帧阶段：只留前 5 段 + 未测部分 ----
            AppendBriefFrame(sb);

            // ---- 读这份文件的人必须知道的 5 条 ----
            AppendBriefNotes(sb);

            return sb.ToString();
        }

        private static void AppendBriefConclusion(StringBuilder sb, double msPerTick, double tps,
                                                  List<CostRow> rows, List<CostRow> modRows)
        {
            sb.Append("## 结论\n\n");
            if (FrameProbe.SessionTicks <= 0)
            {
                sb.Append("本次会话没有采集到有效 tick 样本。\n\n");
                return;
            }
            sb.Append("每 tick 平均 ").Append(msPerTick.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(" 毫秒，实测 ").Append(tps.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" tick/秒。\n\n");

            double[] catSum = new double[8];
            for (int i = 0; i < rows.Count; i++)
            {
                if (!CountsTowardCatTotal(rows[i]))
                {
                    continue;
                }
                int ci = CatIndex(rows[i].Cat);
                if (ci >= 0)
                {
                    catSum[ci] += rows[i].SelfMs;
                }
            }
            string[] names = { "建筑", "物品", "其它", "人物", "事件", "组件", "模组", "全局系统" };
            sb.Append("按类合计：");
            bool first = true;
            for (int i = 0; i < names.Length; i++)
            {
                if (i == 6 || catSum[i] <= 0.0)
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append("；");
                }
                first = false;
                sb.Append(names[i]).Append(' ').Append(catSum[i].ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms");
            }
            sb.Append("。\n\n");

            CostRow top = default(CostRow);
            bool hasTop = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (IsDerivedCat(rows[i].Cat))
                {
                    continue;
                }
                if (!hasTop || rows[i].SelfMs > top.SelfMs)
                {
                    top = rows[i];
                    hasTop = true;
                }
            }
            if (hasTop && top.SelfMs > 0.0)
            {
                sb.Append("单条最高：`").Append(top.Cat).Append('|').Append(Json.Esc(top.Name))
                  .Append("`（模组 ").Append(Json.Esc(top.Mod)).Append("），")
                  .Append(top.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms，占 ")
                  .Append(top.Pct.ToString("0.0", CultureInfo.InvariantCulture)).Append("%");
                if (top.Alive > 0)
                {
                    sb.Append("，").Append(top.Alive).Append(" 个活跃实例，每实例 ")
                      .Append(top.PerInstMs.ToString("0.000000", CultureInfo.InvariantCulture)).Append(" ms");
                }
                sb.Append("。\n\n");
            }

            if (modRows != null && modRows.Count > 0)
            {
                CostRow m = modRows[0];
                sb.Append("耗时最高的模组：**").Append(Json.Esc(m.Name)).Append("**，")
                  .Append(m.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms，占 ")
                  .Append(m.Pct.ToString("0.0", CultureInfo.InvariantCulture)).Append("%。\n\n");
            }
        }

        private static void AppendBriefPeak(StringBuilder sb, List<CostRow> rows)
        {
            if (CostTable.WindowCount <= 0)
            {
                return;
            }
            var top = new List<CostRow>(16);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].MaxW <= 0.0 || IsDerivedCat(rows[i].Cat))
                {
                    continue;
                }
                top.Add(rows[i]);
            }
            if (top.Count == 0)
            {
                return;
            }
            top.Sort(delegate (CostRow a, CostRow b) { return b.MaxW.CompareTo(a.MaxW); });

            sb.Append("## 峰值窗口 Top").Append(TopPeakBrief).Append("\n\n");
            sb.Append("`MaxW` = 该行在任意一个窗口里的最高耗时；`P50W` = 典型窗口。")
              .Append("两者差距大 = 平时不显、出事才爆。\n\n");
            sb.Append("| 类 | 名称 | 模组 | MaxW | P50W | 形态 | 占最贵窗口 % |\n");
            sb.Append("|---|---|---|---|---|---|---|\n");
            int take = top.Count < TopPeakBrief ? top.Count : TopPeakBrief;
            for (int i = 0; i < take; i++)
            {
                CostRow r = top[i];
                sb.Append("| ").Append(r.Cat).Append(" | ").Append(Json.Esc(r.Name)).Append(" | ")
                  .Append(Json.Esc(r.Mod)).Append(" | ")
                  .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.P50W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Kind(r)).Append(" | ")
                  .Append(r.PctPeak.ToString("0.00", CultureInfo.InvariantCulture)).Append(" |\n");
            }
            sb.Append('\n');
        }

        private static void AppendBriefCatTable(StringBuilder sb, List<CostRow> rows, double totalMs)
        {
            sb.Append("## 分类 Top").Append(TopPerCatBrief).Append("\n\n");
            sb.Append("总 tick 耗时 ").Append(totalMs.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" ms。`selfMs` 可加总。\n\n");
            sb.Append("| 类 | 名称 | 模组 | selfMs | 占比% | P50W | MaxW | 形态 |\n");
            sb.Append("|---|---|---|---|---|---|---|---|\n");

            string[] cats = { ThingCost.CatBuilding, ThingCost.CatItem, ThingCost.CatPawn,
                              ThingCost.CatComponent, ThingCost.CatOther, ThingCost.CatGlobal };
            for (int c = 0; c < cats.Length; c++)
            {
                int shown = 0;
                for (int i = 0; i < rows.Count && shown < TopPerCatBrief; i++)
                {
                    CostRow r = rows[i];
                    if (!string.Equals(r.Cat, cats[c], StringComparison.Ordinal))
                    {
                        continue;
                    }
                    sb.Append("| ").Append(r.Cat).Append(" | ").Append(Json.Esc(r.Name)).Append(" | ")
                      .Append(Json.Esc(r.Mod)).Append(" | ")
                      .Append(r.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                      .Append(r.Pct.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | ")
                      .Append(r.P50W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                      .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                      .Append(Kind(r)).Append(" |\n");
                    shown++;
                }
            }
            sb.Append('\n');
        }

        private static void AppendBriefModTable(StringBuilder sb, List<CostRow> modRows)
        {
            sb.Append("## 模组 Top").Append(TopModBrief).Append("\n\n");
            if (modRows == null || modRows.Count == 0)
            {
                sb.Append("（无数据）\n\n");
                return;
            }
            sb.Append("| 模组 | selfMs | 占比% |\n|---|---|---|\n");
            int take = modRows.Count < TopModBrief ? modRows.Count : TopModBrief;
            for (int i = 0; i < take; i++)
            {
                CostRow r = modRows[i];
                sb.Append("| ").Append(Json.Esc(r.Name)).Append(" | ")
                  .Append(r.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.00", CultureInfo.InvariantCulture)).Append(" |\n");
            }
            sb.Append('\n');
        }

        private static void AppendBriefPawn(StringBuilder sb, List<CostRow> rows, double totalMs)
        {
            var shell = new List<CostRow>(4);
            double shellSum = 0.0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!string.Equals(rows[i].Cat, ThingCost.CatPawn, StringComparison.Ordinal))
                {
                    continue;
                }
                if (PawnCost.IsShell(rows[i].Name))
                {
                    shell.Add(rows[i]);
                    shellSum += rows[i].SelfMs;
                }
            }
            if (shell.Count == 0)
            {
                return;
            }

            sb.Append("## 人物：外壳层三项\n\n");
            sb.Append("`pawn_body` + `pawn_interval` + `pawn_other` **恒等于**所有 pawn 的 `Thing.DoTick` 总时间。")
              .Append("核心组 / 扩展组是 body 的内部细分，**不能与这三项相加**。\n\n");
            sb.Append("| 方面 | selfMs | 占比% | P50W | MaxW | 形态 | 活跃 pawn | 每实例 ms |\n");
            sb.Append("|---|---|---|---|---|---|---|---|\n");
            for (int i = 0; i < shell.Count; i++)
            {
                CostRow r = shell[i];
                sb.Append("| ").Append(Json.Esc(r.Name)).Append(" | ")
                  .Append(r.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.P50W.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Kind(r)).Append(" | ")
                  .Append(AliveCell(r)).Append(" | ")
                  .Append(PerInstCell(r)).Append(" |\n");
            }
            sb.Append('\n');
            if (shellSum > 0.0 && totalMs > 0.0)
            {
                sb.Append("人物合计 **").Append(shellSum.ToString("0.000", CultureInfo.InvariantCulture))
                  .Append(" ms**，占全会话 **")
                  .Append((shellSum / totalMs * 100.0).ToString("0.00", CultureInfo.InvariantCulture))
                  .Append("%**。\n\n");
            }

            // 个体 Top5
            sb.Append("## 人物个体 / 种族 Top").Append(TopPerCatBrief).Append("\n\n");
            sb.Append("人形按个体列（`名字[kindDef]`），动物与机械体按种族合并。\n\n");
            sb.Append("| 名称 | 模组 | selfMs | 占比% | MaxW | 形态 | 实例数 | 每实例 ms |\n");
            sb.Append("|---|---|---|---|---|---|---|---|\n");
            int shown = 0;
            for (int i = 0; i < rows.Count && shown < TopPerCatBrief; i++)
            {
                if (!string.Equals(rows[i].Cat, ThingCost.CatPawnInst, StringComparison.Ordinal))
                {
                    continue;
                }
                CostRow r = rows[i];
                sb.Append("| ").Append(Json.Esc(r.Name)).Append(" | ")
                  .Append(Json.Esc(r.Mod)).Append(" | ")
                  .Append(r.SelfMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.00", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.MaxW.ToString("0.000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Kind(r)).Append(" | ")
                  .Append(AliveCell(r)).Append(" | ")
                  .Append(PerInstCell(r)).Append(" |\n");
                shown++;
            }
            if (shown == 0)
            {
                sb.Append("| （无数据） | | | | | | | |\n");
            }
            sb.Append('\n');
        }

        /// <summary>
        /// brief 只保留读数据必需的 5 条口径。summary.md 有 13 条，
        /// 那些是给「要复核数字来源」的 agent 看的，网页对话用不上。
        /// </summary>
        /// <summary>
        /// brief 版帧阶段：只留前 5 段与未测部分。
        /// 完整 10 段在 summary.md，这里保持精简 —— brief 的目标是「一个屏幕读完」。
        /// </summary>
        private static void AppendBriefFrame(StringBuilder sb)
        {
            if (FrameStats.SesFrames <= 0)
            {
                return;
            }
            sb.Append("## 帧阶段分解\n\n");
            double avgFrame = FrameStats.SesAvgFrameMs();
            double fps = FrameStats.SesFps();
            sb.Append("每帧平均 ").Append(avgFrame.ToString("0.000", CultureInfo.InvariantCulture))
              .Append(" 毫秒（").Append(fps.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" FPS）。\n\n");
            sb.Append("| 阶段 | 每帧毫秒 | 占帧长 % |\n|---|---|---|\n");
            List<FrameStageRow> rows = FrameStats.SesRows();
            int take = rows.Count < 5 ? rows.Count : 5;
            for (int i = 0; i < take; i++)
            {
                FrameStageRow r = rows[i];
                sb.Append("| ").Append(Json.Esc(FrameCost.Label(FrameCost.NameOf(r.Id)))).Append(" | ")
                  .Append(r.MsPerFrame.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Pct.ToString("0.00", CultureInfo.InvariantCulture)).Append(" |\n");
            }
            double unacc = FrameStats.SesFrames > 0 ? FrameStats.SesUnaccountedMs() / FrameStats.SesFrames : 0.0;
            sb.Append("| **未测（引擎渲染 / Present / 显存）** | **")
              .Append(unacc.ToString("0.0000", CultureInfo.InvariantCulture)).Append("** | **")
              .Append(avgFrame > 0.0 ? (unacc / avgFrame * 100.0).ToString("0.00", CultureInfo.InvariantCulture) : "0.00")
              .Append("** |\n\n");
            sb.Append("未测部分大 → 瓶颈在 GPU 侧（显存换页、绘制批次、贴图尺寸）；")
              .Append("逻辑侧大 → 瓶颈在 CPU 的每帧逻辑，继续看下面的 tick 归因表。\n\n");
        }

        private static void AppendBriefNotes(StringBuilder sb)
        {
            sb.Append("## 读这份数据必须知道的 5 条\n\n");
            sb.Append("1. `selfMs` 是自身耗时，**可跨行加总**；`inclMs` 含子调用，加总会重复计数。\n");
            sb.Append("2. `cat=pawn` 的外壳三项（body / interval / other）相加 = 人物总时间；")
              .Append("核心组与扩展组是 body 的内部细分，**已在 body 里，不要重复加**。\n");
            sb.Append("3. `cat=mod` 是跨类二次汇总，**不能与其它类相加**；`cat=pawninst` 与 `cat=pawn` 覆盖同一批 tick，二者不要同时计入合计。\n");
            sb.Append("4. 只有 `fast` 与 `superfast` 档进统计；暂停、一倍速、四倍速只累加覆盖缺口。\n");
            sb.Append("5. `形态` 是机械判别：`持续型` = 最大窗口/中值窗口 < 2 倍；`波动型` ≥ 2 倍；")
              .Append("`爆发型` ≥ 3 倍且标准差 ≥ 中值；`偶发` = 中值为 0。爆发型是间歇卡顿的元凶，持续型是常态负担。\n");
        }
    }
}
