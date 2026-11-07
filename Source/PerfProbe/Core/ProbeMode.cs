namespace PerfProbe
{
    /// <summary>
    /// 采集模式。写入文件名的前缀与每行快照的 mode 字段都取这里的值。
    ///
    /// 历史上还有一个 Bench（基准测试）模式，靠「前 90 秒不采集 / 之后采集」的 A/B 对比
    /// 估算探针自身开销。已删除，理由：
    ///   1. 探针开销本来就有两条更准的度量 —— CalibrateOverhead() 用真实热路径标定单次
    ///      Enter/Exit 成本，Patch_Thing_DoTick 用 1/64 抽样实测框架成本。A/B 是第三条、最粗的。
    ///   2. A/B 法两段不是同一时段，开局的地图初始化 / pawn 生成 / 寻路冷启动全落在浅段，
    ///      测出来的差异主要是游戏本身前后不同，不是探针开销。
    ///   3. 该模式的「浅段不采集明细」实际从未实现 —— 没有任何 patch 读 InDeepPhase()，
    ///      于是浅段也在全量计时，两段本质相同，对比失去意义。
    /// 保留枚举而非整个删掉 mode 概念，是为了让 jsonl 的 mode 字段、index.json 的
    /// sessions[].mode 与旧数据文件继续兼容。
    /// </summary>
    public enum ProbeMode
    {
        /// <summary>被动采集：随游戏正常运行持续记录。</summary>
        Passive = 0
    }

    public static class ProbeModeUtil
    {
        public static string ToTag(this ProbeMode m)
        {
            return "passive";
        }
    }
}
