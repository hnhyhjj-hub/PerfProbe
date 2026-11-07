using System;
using System.Collections.Generic;
using Verse;

namespace PerfProbe
{
    /// <summary>一个帧阶段在某个口径下的耗时。</summary>
    public struct FrameStageRow
    {
        public int Id;
        public double SelfMs;
        public double MsPerFrame;
        public double Pct;
    }

    /// <summary>
    /// 帧级耗时累加。与 CostTable 刻意分开，因为分母不同：
    /// CostTable 的分母是「本窗口所有 tick 的耗时和」，这里的分母是「本窗口的帧数」。
    /// 混在一起会让两边的占比全部失真。
    ///
    /// 帧边界取 Root.Update 的前缀：它每帧必被调用一次，且无论在主菜单还是游戏内都在。
    /// 用「上一帧起点 → 本帧起点」作为帧长，这样 render 与 Present 也落在区间内，
    /// 再减去各已测阶段的 self 时间，剩下的就是引擎渲染 + 显存等待 —— 这正是
    /// 判断「CPU 侧卡还是 GPU 侧卡」所缺的那块数据。
    ///
    /// 只在主线程累加：并行 mod 会让 worker 线程也走进这些方法。
    ///
    /// **只统计活跃帧**（本帧至少跑了一个 tick）。暂停与读档时帧照跑但不产生 tick，
    /// 帧长只有十几毫秒，把它们混进来会把会话帧率抬高近一倍 —— 实测一轮 503 秒的会话里，
    /// 3 个暂停窗口贡献了 61% 的帧数，会话帧率被拉成 29.2 FPS，而真实游玩是 14.9 FPS。
    /// 空闲帧单独计数，用于在报告里说明「排除了多少帧」。
    /// </summary>
    public static class FrameStats
    {
        /// <summary>帧长环形缓冲。1024 帧 ≈ 5 秒窗口下约 85 个窗口的历史。</summary>
        private const int Capacity = 1024;

        private static readonly double[] _ring = new double[Capacity];
        private static readonly double[] _scratch = new double[Capacity];
        private static int _head;
        private static int _count;

        /// <summary>帧内暂存：帧结束时才知道这帧算不算活跃帧，所以不能直接写窗口累计。</summary>
        private static readonly double[] _frameStage = new double[FrameCost.Count];
        private static readonly double[] _winStage = new double[FrameCost.Count];
        private static readonly double[] _sesStage = new double[FrameCost.Count];
        private static readonly List<FrameStageRow> _rowBuf = new List<FrameStageRow>(FrameCost.Count);

        private static int _winFrames;
        private static int _sesFrames;
        private static double _winFrameMs;
        private static double _sesFrameMs;

        private static int _winIdleFrames;
        private static int _sesIdleFrames;

        private static long _frameStartTs;
        private static bool _frameOpen;
        private static bool _frameCaptured;
        private static bool _frameHasTick;

        /// <summary>当前帧是否在采集。各阶段 patch 靠它决定要不要记账。</summary>
        public static bool FrameActive => _frameOpen && _frameCaptured;

        /// <summary>本窗口活跃帧数（跑过至少一个 tick 的帧）。</summary>
        public static int WinFrames => _winFrames;
        public static int SesFrames => _sesFrames;
        public static double WinFrameMs => _winFrameMs;
        public static double SesFrameMs => _sesFrameMs;

        /// <summary>本窗口被排除的空闲帧数（暂停 / 读档 / 主菜单）。</summary>
        public static int WinIdleFrames => _winIdleFrames;
        public static int SesIdleFrames => _sesIdleFrames;

        /// <summary>
        /// 帧边界。由 Patch_Frame_Boundary 挂在 Root.Update 前缀上调用。
        /// 只挂这一个点是刻意的：Root_Play.Update 先跑 base.Update()（即 Root.Update）
        /// 再跑 Game.UpdatePlay()，所以 Root.Update 的起点既是「本帧逻辑开始」，
        /// 也在 OnGUI / render / Present 之前 —— 帧长因此覆盖了整帧的全部阶段。
        /// 若改成挂 Root_Play.Update，主菜单（没有 Root_Play）就采不到帧。
        /// </summary>
        public static void BeginFrame()
        {
            if (_frameOpen)
            {
                FinalizeFrame();
            }
            _frameOpen = true;
            _frameStartTs = Qpc.Now();
            _frameCaptured = ProbeEngine.FrameShouldCapture;
            _frameHasTick = false;
        }

        /// <summary>
        /// 标记「本帧跑过 tick」。由 ProbeEngine.EndTick 调用 —— 这是判定活跃帧的唯一依据。
        /// 暂停、读档、主菜单里 Root.Update 照跑，但一个 tick 都不会产生，这类帧的帧长
        /// 只有十几毫秒，混进帧率会把结果抬高近一倍。
        /// </summary>
        public static void MarkTick()
        {
            if (_frameOpen)
            {
                _frameHasTick = true;
            }
        }

        private static void FinalizeFrame()
        {
            double ms = Qpc.ToMs(Qpc.Now() - _frameStartTs);
            _frameOpen = false;
            bool captured = _frameCaptured;
            bool active = _frameHasTick;
            _frameCaptured = false;
            _frameHasTick = false;

            if (!captured || ms <= 0.0)
            {
                DiscardFrameStages();
                return;
            }

            if (!active)
            {
                _winIdleFrames++;
                DiscardFrameStages();
                return;
            }

            _winFrameMs += ms;
            _winFrames++;
            for (int i = 0; i < FrameCost.Count; i++)
            {
                _winStage[i] += _frameStage[i];
            }
            DiscardFrameStages();

            _ring[_head] = ms;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }

        private static void DiscardFrameStages()
        {
            for (int i = 0; i < FrameCost.Count; i++)
            {
                _frameStage[i] = 0.0;
            }
        }

        /// <summary>
        /// 记一段帧内耗时（self，已扣除嵌套子段）。
        /// 先写本帧暂存，帧结束时才决定是否并入窗口累计 —— 因为「这帧算不算活跃帧」
        /// 要等到帧末才知道（tick 可能在本帧任何时刻发生）。空闲帧的阶段数据随帧丢弃，
        /// 否则暂停时那几十毫秒的 UI 开销会污染活跃帧的分母。
        /// </summary>
        public static void AddStage(int id, double selfMs)
        {
            if (id < 0 || id >= FrameCost.Count || selfMs <= 0.0)
            {
                return;
            }
            if (!FrameActive)
            {
                return;
            }
            _frameStage[id] += selfMs;
        }

        /// <summary>窗口关闭时结转到会话累计。</summary>
        public static void CloseWindow()
        {
            _sesFrameMs += _winFrameMs;
            _sesFrames += _winFrames;
            _sesIdleFrames += _winIdleFrames;
            for (int i = 0; i < FrameCost.Count; i++)
            {
                _sesStage[i] += _winStage[i];
            }
            _winFrameMs = 0.0;
            _winFrames = 0;
            _winIdleFrames = 0;
            for (int i = 0; i < FrameCost.Count; i++)
            {
                _winStage[i] = 0.0;
            }
        }

        public static void ResetSession()
        {
            _winFrameMs = 0.0;
            _winFrames = 0;
            _winIdleFrames = 0;
            _sesFrameMs = 0.0;
            _sesFrames = 0;
            _sesIdleFrames = 0;
            _head = 0;
            _count = 0;
            _frameOpen = false;
            _frameCaptured = false;
            _frameHasTick = false;
            for (int i = 0; i < FrameCost.Count; i++)
            {
                _frameStage[i] = 0.0;
                _winStage[i] = 0.0;
                _sesStage[i] = 0.0;
            }
        }

        public static double WinStageMs(int id)
        {
            return id >= 0 && id < FrameCost.Count ? _winStage[id] : 0.0;
        }

        public static double SesStageMs(int id)
        {
            return id >= 0 && id < FrameCost.Count ? _sesStage[id] : 0.0;
        }

        /// <summary>本窗口已测阶段的 self 合计。帧长减去它即为未测部分。</summary>
        public static double WinAccountedMs()
        {
            double sum = 0.0;
            for (int i = 0; i < FrameCost.Count; i++)
            {
                sum += _winStage[i];
            }
            return sum;
        }

        public static double SesAccountedMs()
        {
            double sum = 0.0;
            for (int i = 0; i < FrameCost.Count; i++)
            {
                sum += _sesStage[i];
            }
            return sum;
        }

        /// <summary>未测部分：引擎渲染、Present、显存等待、以及测量盲区。可能为负（抖动），按 0 处理。</summary>
        public static double WinUnaccountedMs()
        {
            double v = _winFrameMs - WinAccountedMs();
            return v > 0.0 ? v : 0.0;
        }

        public static double SesUnaccountedMs()
        {
            double v = _sesFrameMs - SesAccountedMs();
            return v > 0.0 ? v : 0.0;
        }

        /// <summary>
        /// 帧率。分母用「帧长之和」而不是窗口墙钟跨度：
        /// 暂停时帧照跑但一个 tick 都不产生，窗口不会 flush，用墙钟做分母会让 FPS 虚高。
        /// 帧长之和本身就是相邻帧起点的间隔累加，等价于这段真实耗时，自洽且不受暂停影响。
        /// </summary>
        /// <summary>
        /// 帧率。分母刻意用帧长之和而不是墙钟跨度：暂停时帧照跑但不产生 tick，
        /// 窗口不会 flush，用墙钟会让 FPS 虚高。帧长之和本身是相邻帧起点的间隔累加，
        /// 自洽且不受暂停影响。
        /// </summary>
        public static double WinFps()
        {
            return _winFrameMs > 0.0 ? _winFrames * 1000.0 / _winFrameMs : 0.0;
        }

        public static double SesFps()
        {
            return _sesFrameMs > 0.0 ? _sesFrames * 1000.0 / _sesFrameMs : 0.0;
        }

        /// <summary>本窗口平均帧长。</summary>
        public static double WinAvgFrameMs()
        {
            return _winFrames > 0 ? _winFrameMs / _winFrames : 0.0;
        }

        public static double SesAvgFrameMs()
        {
            return _sesFrames > 0 ? _sesFrameMs / _sesFrames : 0.0;
        }

        public static double WinFrameP50()
        {
            return Percentile(50.0);
        }

        public static double WinFrameP95()
        {
            return Percentile(95.0);
        }

        /// <summary>
        /// 帧长百分位。环形缓冲里存的是历史帧长（跨窗口），
        /// 因此 P95 反映的是「最近这段时间的最差帧」，而不是仅本窗口。
        /// </summary>
        private static double Percentile(double p)
        {
            int n = _count;
            if (n <= 0)
            {
                return 0.0;
            }
            int start = n == Capacity ? _head : 0;
            for (int i = 0; i < n; i++)
            {
                _scratch[i] = _ring[(start + i) % Capacity];
            }
            Array.Sort(_scratch, 0, n);
            double pos = (n - 1) * p / 100.0;
            int lo = (int)pos;
            int hi = lo + 1 < n ? lo + 1 : lo;
            double frac = pos - lo;
            return _scratch[lo] + (_scratch[hi] - _scratch[lo]) * frac;
        }

        /// <summary>本窗口各阶段行，按 selfMs 降序。</summary>
        public static List<FrameStageRow> WinRows()
        {
            return BuildRows(_winStage, _winFrames, _winFrameMs);
        }

        public static List<FrameStageRow> SesRows()
        {
            return BuildRows(_sesStage, _sesFrames, _sesFrameMs);
        }

        private static List<FrameStageRow> BuildRows(double[] src, int frames, double totalMs)
        {
            _rowBuf.Clear();
            for (int i = 0; i < FrameCost.Count; i++)
            {
                if (src[i] <= 0.0)
                {
                    continue;
                }
                FrameStageRow r;
                r.Id = i;
                r.SelfMs = src[i];
                r.MsPerFrame = frames > 0 ? src[i] / frames : 0.0;
                r.Pct = totalMs > 0.0 ? src[i] / totalMs * 100.0 : 0.0;
                _rowBuf.Add(r);
            }
            _rowBuf.Sort(CompareBySelfDesc);
            return _rowBuf;
        }

        private static int CompareBySelfDesc(FrameStageRow a, FrameStageRow b)
        {
            if (a.SelfMs > b.SelfMs)
            {
                return -1;
            }
            return a.SelfMs < b.SelfMs ? 1 : 0;
        }
    }
}
