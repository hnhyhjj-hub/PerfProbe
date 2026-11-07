using System;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 模组设置面板。动作按钮直接同步调 ProbeEngine，不设请求标志。
    /// 原因：Dialog_ModSettings 的 forcePause=true 会让游戏暂停，
    /// 而暂停时 TickManager.DoSingleTick 不被调用 —— 挂在 tick 上的请求标志
    /// 永远不会被消费，按钮点了也不会生效。
    /// </summary>
    internal static class ProbeSettingsUI
    {
        private static Vector2 _scroll = Vector2.zero;
        private static string _lastAction = "";

        public static void Draw(Rect inRect, ProbeSettings s)
        {
            if (s == null)
            {
                return;
            }
            var view = new Rect(0f, 0f, inRect.width - 24f, 620f);
            Widgets.BeginScrollView(inRect, ref _scroll, view);
            var listing = new Listing_Standard();
            listing.Begin(view);

            listing.Label("PerfProbe · 性能探针（只读观测，不改变任何游戏行为）");
            listing.GapLine(6f);
            listing.Gap(6f);

            listing.CheckboxLabeled("启用采集（关闭时对游戏零影响）", ref s.Enabled,
                "关闭后所有 patch 立即返回，不产生任何计时与文件写入。");

            listing.Gap(6f);
            listing.Label("采集速度档（只采勾选的档位，其它档位的 tick 计入 gapMs 缺口）：");
            listing.CheckboxLabeled("  快进（Fast，3 倍）", ref s.CaptureFast);
            listing.CheckboxLabeled("  极快（Superfast，6 倍）", ref s.CaptureSuperfast,
                "推荐保持开启：这一档 tick 吞吐最高，最接近真实性能压力。");
            listing.CheckboxLabeled("  超快（Ultrafast，15 倍）", ref s.CaptureUltrafast,
                "不推荐：一帧内塞入大量 tick，单 tick 均摊会被系统性低估。");

            listing.Gap(6f);
            s.WindowSec = (int)listing.SliderLabeled(
                "窗口长度（秒）：" + s.WindowSec, s.WindowSec,
                ProbeSettings.WindowSecMin, ProbeSettings.WindowSecMax, 0.5f,
                "每个窗口落一行 jsonl，并重置窗口内累加。");
            s.SampleK = (int)listing.SliderLabeled(
                "抽样分母 K：" + s.SampleK, s.SampleK,
                ProbeSettings.SampleKMin, ProbeSettings.SampleKMax, 0.5f,
                "每 K 个对象里只精确计时 1 个，落盘的 selfMs 是抽样后的累计值（不做外推）。"
                + "K=1 为全量，也是唯一能让各类占比可横向比较的设置 —— 组件类不抽样、"
                + "其它类走 K 抽样，倍率不同会让跨类比较失真。");
            s.PhaseTicks = (int)listing.SliderLabeled(
                "相位轮换（刻）：" + s.PhaseTicks, s.PhaseTicks,
                ProbeSettings.PhaseTicksMin, ProbeSettings.PhaseTicksMax, 0.5f,
                "每这么多刻换一批被抽中的对象，保证长期覆盖全部对象。");
            s.KeepSessions = (int)listing.SliderLabeled(
                "保留会话数：" + s.KeepSessions, s.KeepSessions,
                ProbeSettings.KeepSessionsMin, ProbeSettings.KeepSessionsMax, 0.5f,
                "超出后按时间从旧到新成组删除 jsonl、summary 与 brief。");

            listing.Gap(6f);
            listing.CheckboxLabeled("自动清理旧会话", ref s.AutoCleanup);
            listing.Gap(6f);
            listing.Label("输出目录（留空则用默认目录）：");
            s.OutputDir = listing.TextEntry(s.OutputDir);

            listing.Gap(6f);
            listing.Label("当前输出目录：" + SafeDir());
            listing.Gap(6f);

            listing.GapLine(6f);
            listing.Label("动作（点击即刻生效，无需等待 tick）：");
            if (listing.ButtonText("开始被动采集（跟随游戏速度，读到即采）"))
            {
                DoStart();
            }
            if (listing.ButtonText("停止并输出汇总"))
            {
                DoStop();
            }
            if (listing.ButtonText("导出反馈包（把报告/日志/mod 列表打成一个 zip）"))
            {
                DoExportPack();
            }

            listing.Gap(6f);
            listing.Label(StatusLine());

            listing.Gap(6f);
            listing.Label("输出文件：<输出目录>/passive-<日期>-<时间>-sNN.jsonl，同名 .summary.md 与 .brief.md；"
                          + "schema.md 与 index.json 由模组自动维护。");
            listing.Gap(6f);
            listing.Label("反馈包：把最新会话报告、探针日志、裁剪后的 Player.log 与 ModsConfig.xml "
                          + "打成一个 zip，放在同一目录。不含任何硬件信息与用户名。");

            listing.End();
            Widgets.EndScrollView();
        }

        private static void DoStart()
        {
            try
            {
                if (string.IsNullOrEmpty(ProbePaths.ActiveDir))
                {
                    ProbePaths.Resolve(ProbeEngine.Settings != null ? ProbeEngine.Settings.OutputDir : null);
                }
                // 「启用采集」与动作按钮是两套机制，必须在这里一起打开：
                // 关闭设置面板会触发 Mod.WriteSettings → ProbeEngine.ApplySettings，
                // 而 ApplySettings 见到 !Enabled 就会 EndSession("设置中关闭采集")。
                // 于是「点了开始 → 关面板 → 立刻变成未采集、0 落盘」。
                ProbeSettings s = ProbeEngine.Settings;
                bool turnedOn = false;
                if (s != null && !s.Enabled)
                {
                    s.Enabled = true;
                    turnedOn = true;
                }
                bool ok = ProbeEngine.StartSession(ProbeMode.Passive);
                if (!ok)
                {
                    _lastAction = "开始失败：看 Player.log 的 [PerfProbe] 行（输出目录或文件打开失败）";
                    return;
                }
                _lastAction = "已在采集"
                              + (turnedOn ? "，已顺带打开「启用采集」" : "");
            }
            catch (Exception e)
            {
                _lastAction = "开始失败: " + e.Message;
            }
        }


        private static void DoStop()
        {
            try
            {
                ProbeEngine.EndSession("用户手动停止");
                _lastAction = "已停止并输出汇总";
            }
            catch (Exception e)
            {
                _lastAction = "停止失败: " + e.Message;
            }
        }

        /// <summary>
        /// 导出反馈包。采集进行中也能用 —— 正在写的 jsonl 以 FileShare.ReadWrite 打开，
        /// 但此时还没有 summary，包里只会有 jsonl 与 probe.log，所以更建议先停止再导出。
        /// </summary>
        private static void DoExportPack()
        {
            try
            {
                string err;
                string zip = FeedbackPack.Create(out err);
                if (string.IsNullOrEmpty(zip))
                {
                    _lastAction = "反馈包生成失败：" + err;
                    return;
                }
                _lastAction = "反馈包已生成: " + zip;

                try
                {
                    Application.OpenURL("file:///" + zip.Replace('\\', '/'));
                }
                catch
                {
                }

                try
                {
                    Messages.Message("反馈包已生成，请把 zip 发给作者\n" + System.IO.Path.GetFileName(zip),
                                     MessageTypeDefOf.PositiveEvent, false);
                }
                catch
                {
                }
            }
            catch (Exception e)
            {
                _lastAction = "反馈包生成失败: " + e.Message;
            }
        }

        private static string SafeDir()
        {
            try
            {
                string d = ProbePaths.ActiveDir;
                if (string.IsNullOrEmpty(d))
                {
                    return "(不可用，请检查路径权限)";
                }
                if (ProbePaths.FellBack)
                {
                    return d + "（已回退：" + ProbePaths.FallbackReason + "）";
                }
                return d;
            }
            catch
            {
                return "(不可用)";
            }
        }

        private static string StatusLine()
        {
            string extra = string.IsNullOrEmpty(_lastAction) ? "" : "　（" + _lastAction + "）";
            try
            {
                ProbeSession sess = ProbeEngine.Session;
                if (sess == null)
                {
                    return "状态：未采集" + Health() + extra;
                }
                return "状态：采集中"
                       + "　已运行 " + sess.ElapsedSec.ToString("0", CultureInfo.InvariantCulture) + " 秒"
                       + "　已落盘 " + sess.Lines + " 行"
                       + "　会话累计 " + (CostTable.SessionTotalMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 秒"
                       + FrameLine()
                       + Health() + extra;
            }
            catch (Exception e)
            {
                return "状态读取失败: " + e.Message + extra;
            }
        }

        /// <summary>
        /// 实时帧率。设置面板是读「现在卡不卡」最方便的地方，
        /// 但要提醒：面板打开期间游戏被 forcePause，帧率会比正常游玩时高，
        /// 因此只适合看数量级与趋势，不能当基线。
        /// </summary>
        private static string FrameLine()
        {
            try
            {
                if (FrameStats.SesFrames <= 0)
                {
                    return "";
                }
                double fps = FrameStats.SesFps();
                return "　帧率 " + fps.ToString("0.0", CultureInfo.InvariantCulture) + " FPS"
                       + (fps < 30.0 ? "（已低于 30，TPS 正被帧率拖累）" : "")
                       + (FrameStats.SesIdleFrames > 0
                           ? "（另有 " + FrameStats.SesIdleFrames.ToString(CultureInfo.InvariantCulture) + " 空闲帧未计入）"
                           : "");
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 挂载健康度。没有这行的话，patch 全线挂载失败时界面只会显示「0 落盘」，
        /// 看不出是「没在采集」还是「根本采不到」—— 这两种情况处置完全不同。
        /// </summary>
        private static string Health()
        {
            if (!PerfProbeBootstrap.Started)
            {
                return "　⚠ 挂载未执行（[StaticConstructorOnStartup] 未跑，看 Player.log）";
            }
            if (PerfProbeBootstrap.PatchOk <= 0)
            {
                return "　⚠ patch 全部挂载失败，采集不可能工作（看 Player.log 的「挂载 patch 类失败」）";
            }
            if (PerfProbeBootstrap.PatchBad > 0)
            {
                return "　⚠ patch 部分失败：成功 " + PerfProbeBootstrap.PatchOk
                       + " / 失败 " + PerfProbeBootstrap.PatchBad + "（详见 Player.log）";
            }
            return "　patch " + PerfProbeBootstrap.PatchOk + " 类正常";
        }

    }
}
