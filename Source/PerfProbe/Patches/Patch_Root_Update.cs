using HarmonyLib;
using Verse;

namespace PerfProbe
{
    /// <summary>
    /// 帧级请求消费（双保险）。
    /// 主路径是设置面板按钮直接同步调 ProbeEngine，不依赖这里；
    /// 但任何"暂停中设置的标志"（例如以后新增的自动化入口）都会被这一层捞出来，
    /// 因为 Root.Update 每帧执行，不受 TickManager.Paused 影响 ——
    /// 而挂在 TickManager.DoSingleTick 上的消费路径在暂停时根本不运行。
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class Patch_Root_Update
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            ProbeEngine.ConsumeRequestsAtFrameLevel();
        }
    }
}
